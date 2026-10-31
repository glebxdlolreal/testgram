namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;

/// <summary>
/// Activate or deactivate a purchased <a href="https://fragment.com">fragment.com</a> username
/// associated to a supergroup or channel we own. The editable username cannot be switched off.
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// 400 USERNAME_INVALID The provided username is not valid.
/// 400 USERNAME_NOT_MODIFIED The username was not modified.
/// 400 USERNAMES_ACTIVE_TOO_MUCH The maximum number of active usernames was reached.
/// See https://corefork.telegram.org/method/channels.toggleUsername and
/// https://corefork.telegram.org/api/fragment
/// </summary>
internal sealed class ToggleUsernameHandler(
    IQueryProcessor queryProcessor,
    IAccessHashHelper2 accessHashHelper,
    IChannelAdminRightsChecker channelAdminRightsChecker,
    ICommandBus commandBus)
    : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestToggleUsername, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Channels.RequestToggleUsername obj)
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
        var updated = UsernameListEditor.Toggle(current, obj.Username, obj.Active, forBot: false);
        if (updated == null)
        {
            RpcErrors.RpcErrors400.UsernameNotModified.ThrowRpcError();
            return null!;
        }

        // updateChannel and the admin log entry come from the event handler.
        await commandBus.PublishAsync(new SetChannelUsernamesCommand(ChannelId.Create(channel.ChannelId),
            input.ToRequestInfo(), updated.ToList(), current));

        return new TBoolTrue();
    }
}
