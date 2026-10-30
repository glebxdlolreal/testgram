namespace MyTelegram.Messenger.Services;

/// <summary>
/// Helpers for classifying peers — distinguishes regular users from bots and "system" users
/// (notification, support, anonymous, group-anonymous bot). Used to gate features that must
/// not be exposed to bots (gift sending, NFT transfer, premium gifting, contact rename, etc.).
/// </summary>
public static class PeerKindHelper
{
    /// <summary>
    /// True when <paramref name="userId"/> is one of the built-in system user ids
    /// (notification 777000, group-anonymous 568888, anonymous 2666000, default-support 569999,
    /// replies 1271266957, chat importer 1474613229, GIF search bot).
    /// </summary>
    public static bool IsSystemUserId(long userId)
    {
        return userId == MyTelegramConsts.NotificationServiceUserId
            || userId == MyTelegramConsts.GroupAnonymousBotUserId
            || userId == MyTelegramConsts.AnonymousUserId
            || userId == MyTelegramConsts.DefaultSupportUserId
            || userId == MyTelegramConsts.RepliesServiceUserId
            || userId == MyTelegramConsts.ChatImporterBotUserId
            || userId == MyTelegramConsts.GifSearchBotUserId
            || userId == MyTelegramConsts.AntiSpamBotUserId;
    }

    /// <summary>
    /// Loads the user read model and returns true when it is missing, a bot, or a system user.
    /// Throws PEER_ID_INVALID when null is passed for <paramref name="userId"/>.
    /// </summary>
    public static async Task<bool> IsBotOrSystemAsync(IQueryProcessor queryProcessor, long userId)
    {
        if (IsSystemUserId(userId))
        {
            return true;
        }

        var user = await queryProcessor.ProcessAsync(new GetUserByIdQuery(userId));
        return user == null || user.Bot;
    }

    /// <summary>
    /// True when <paramref name="user"/> is a bot or a system user. Null is treated as system
    /// (callers should reject the peer in either case).
    /// </summary>
    public static bool IsBotOrSystem(IUserReadModel? user)
    {
        if (user == null)
        {
            return true;
        }

        return user.Bot || IsSystemUserId(user.UserId);
    }

    /// <summary>
    /// Throws PEER_ID_INVALID when the resolved user is a bot or a system user. Use at the
    /// entry of any handler that must not target bots (gift/NFT/premium send, AddContact, ...).
    /// </summary>
    public static async Task EnsureNotBotOrSystemAsync(IQueryProcessor queryProcessor, long userId)
    {
        if (await IsBotOrSystemAsync(queryProcessor, userId))
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }
    }
}
