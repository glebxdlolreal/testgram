namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;

/// <summary>
/// Disable all purchased usernames of a supergroup or channel we own; the editable username is kept.
/// Together with clearing the editable username through <c>channels.updateUsername</c> this makes
/// the chat private.
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// See https://corefork.telegram.org/method/channels.deactivateAllUsernames and
/// https://corefork.telegram.org/api/fragment
/// </summary>
internal sealed class DeactivateAllUsernamesHandler(
    IQueryProcessor queryProcessor,
    IAccessHashHelper2 accessHashHelper,
    IChannelAdminRightsChecker channelAdminRightsChecker,
    ICommandBus commandBus)
    : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestDeactivateAllUsernames, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Channels.RequestDeactivateAllUsernames obj)
    {
        if (obj.Channel is not TInputChannel inputChannel)
        {
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
            return null!;
        }

        await accessHashHelper.CheckAccessHashAsync(input, inputChannel);
        await channelAdminRightsChecker.ThrowIfNotChannelOwnerAsync(inputChannel.ChannelId, input.UserId);

        var channel = await queryProcessor.ProcessAsync(new GetChannelByIdQuery(inputChannel.ChannelId));
        if (channel == null)
        {
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
            return null!;
        }

        var current = UsernameListEditor.Effective(channel.Usernames.ToUsernameItems(), channel.UserName);
        var updated = UsernameListEditor.DeactivateAll(current);
        if (updated != null)
        {
            await commandBus.PublishAsync(new SetChannelUsernamesCommand(ChannelId.Create(channel.ChannelId),
                input.ToRequestInfo(), updated.ToList(), current));
        }

        return new TBoolTrue();
    }
}
