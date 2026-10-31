namespace MyTelegram.Messenger.Services.Premium;

/// <summary>A limit that differs between Premium and non-Premium accounts, read from app config.</summary>
/// <param name="Key">The key without its suffix: <c>channels_limit</c> reads <c>channels_limit_default</c> / <c>channels_limit_premium</c>.</param>
public sealed record PremiumLimit(string Key, int DefaultFallback, int PremiumFallback)
{
    // Fallbacks are real Telegram's values (appConfig measured 2026-10-31), used only if the key is missing.
    public static readonly PremiumLimit DialogsPinned = new("dialogs_pinned_limit", 5, 10);
    public static readonly PremiumLimit DialogsFolderPinned = new("dialogs_folder_pinned_limit", 100, 200);
    public static readonly PremiumLimit AboutLength = new("about_length_limit", 70, 140);
    public static readonly PremiumLimit Channels = new("channels_limit", 500, 1000);
    public static readonly PremiumLimit ChannelsPublic = new("channels_public_limit", 10, 20);
    public static readonly PremiumLimit UploadMaxFileParts = new("upload_max_fileparts", 4000, 8000);
    public static readonly PremiumLimit BotsCreate = new("bots_create_limit", 20, 40);
    public static readonly PremiumLimit CaptionLength = new("caption_length_limit", 1024, 4096);
}

/// <summary>
/// Resolves the <a href="https://corefork.telegram.org/api/premium#doubled-limits">limits Premium
/// doubles</a> for a given account: the server enforces the same numbers it advertises in app config,
/// so a client that stops at its limit and a server that refuses past it agree.
/// </summary>
public interface IPremiumLimitResolver
{
    Task<int> GetLimitAsync(long userId, PremiumLimit limit);
}

/// <inheritdoc />
public sealed class PremiumLimitResolver(IUserAppService userAppService, IAppConfigHelper appConfigHelper)
    : IPremiumLimitResolver, ITransientDependency
{
    public async Task<int> GetLimitAsync(long userId, PremiumLimit limit)
    {
        var user = await userAppService.GetAsync(userId);

        return user?.Premium == true
            ? appConfigHelper.GetInt32Value($"{limit.Key}_premium", limit.PremiumFallback)
            : appConfigHelper.GetInt32Value($"{limit.Key}_default", limit.DefaultFallback);
    }
}

/// <summary>Checks that need a count as well as the limit.</summary>
public interface IPremiumLimitGuard
{
    /// <summary>channels_limit: CHANNELS_TOO_MUCH when the user is already in as many channels and supergroups as allowed.</summary>
    Task EnsureCanJoinChannelAsync(long userId);

    /// <summary>channels_limit for a batch of joins (a chat folder link): all of them must fit.</summary>
    Task EnsureCanJoinChannelsAsync(long userId, int count);

    /// <summary>Whether the user is already in as many channels and supergroups as channels_limit allows.</summary>
    Task<bool> IsAtChannelLimitAsync(long userId);

    /// <summary>
    /// The same limit checked on someone else's behalf (an admin adding or approving them):
    /// USER_CHANNELS_TOO_MUCH.
    /// </summary>
    Task EnsureUserCanJoinChannelAsync(long userId);

    /// <summary>channels_public_limit: CHANNELS_ADMIN_PUBLIC_TOO_MUCH when the user already owns as many public channels as allowed.</summary>
    Task EnsureCanOwnPublicChannelAsync(long userId);

    /// <summary>
    /// upload_max_fileparts: FILE_PARTS_INVALID for an uploaded file in more parts than the account may
    /// upload (4000 or 8000 parts of 512 KB, i.e. 2 GB or 4 GB).
    /// </summary>
    Task EnsureUploadPartsAsync(long userId, params IInputFile?[] files);

    /// <inheritdoc cref="EnsureUploadPartsAsync(long, IInputFile?[])"/>
    Task EnsureUploadPartsAsync(long userId, IInputMedia? media);
}

/// <inheritdoc />
public sealed class PremiumLimitGuard(IPremiumLimitResolver premiumLimitResolver, IQueryProcessor queryProcessor)
    : IPremiumLimitGuard, ITransientDependency
{
    public async Task EnsureCanJoinChannelAsync(long userId)
    {
        if (await IsAtChannelLimitAsync(userId))
        {
            RpcErrors.RpcErrors400.ChannelsTooMuch.ThrowRpcError();
        }
    }

    public async Task EnsureCanJoinChannelsAsync(long userId, int count)
    {
        if (count <= 0)
        {
            return;
        }

        var limit = await premiumLimitResolver.GetLimitAsync(userId, PremiumLimit.Channels);
        var joined = await queryProcessor.ProcessAsync(new GetJoinedChannelCountQuery(userId));
        if (joined + count > limit)
        {
            RpcErrors.RpcErrors400.ChannelsTooMuch.ThrowRpcError();
        }
    }

    public async Task EnsureUserCanJoinChannelAsync(long userId)
    {
        if (await IsAtChannelLimitAsync(userId))
        {
            RpcErrors.RpcErrors400.UserChannelsTooMuch.ThrowRpcError();
        }
    }

    public async Task EnsureCanOwnPublicChannelAsync(long userId)
    {
        var limit = await premiumLimitResolver.GetLimitAsync(userId, PremiumLimit.ChannelsPublic);
        var owned = await queryProcessor.ProcessAsync(new GetAdminedPublicChannelIdsQuery(userId));
        if (owned.Count >= limit)
        {
            RpcErrors.RpcErrors400.ChannelsAdminPublicTooMuch.ThrowRpcError();
        }
    }

    public async Task EnsureUploadPartsAsync(long userId, params IInputFile?[] files)
    {
        var parts = files.OfType<TInputFileBig>().Select(f => f.Parts).DefaultIfEmpty(0).Max();
        if (parts == 0)
        {
            // Only big files carry a part count worth checking; a small file is under 10 MB.
            return;
        }

        if (parts > await premiumLimitResolver.GetLimitAsync(userId, PremiumLimit.UploadMaxFileParts))
        {
            RpcErrors.RpcErrors400.FilePartsInvalid.ThrowRpcError();
        }
    }

    public Task EnsureUploadPartsAsync(long userId, IInputMedia? media)
    {
        return EnsureUploadPartsAsync(userId, UploadedFiles(media).ToArray());
    }

    private static IEnumerable<IInputFile?> UploadedFiles(IInputMedia? media)
    {
        switch (media)
        {
            case TInputMediaUploadedDocument document:
                yield return document.File;
                yield return document.Thumb;
                break;
            case TInputMediaUploadedPhoto photo:
                yield return photo.File;
                break;
            case TInputMediaPaidMedia paidMedia when paidMedia.ExtendedMedia != null:
                foreach (var file in paidMedia.ExtendedMedia.SelectMany(UploadedFiles))
                {
                    yield return file;
                }

                break;
        }
    }

    public async Task<bool> IsAtChannelLimitAsync(long userId)
    {
        var limit = await premiumLimitResolver.GetLimitAsync(userId, PremiumLimit.Channels);
        var joined = await queryProcessor.ProcessAsync(new GetJoinedChannelCountQuery(userId));
        return joined >= limit;
    }
}
