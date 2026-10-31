namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;

/// <summary>
/// Reorder active usernames of a supergroup or channel we own. The order must list every active
/// username exactly once.
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// 400 ORDER_INVALID The specified username order is invalid.
/// See https://corefork.telegram.org/method/channels.reorderUsernames and
/// https://corefork.telegram.org/api/fragment
/// </summary>
internal sealed class ReorderUsernamesHandler(
    IQueryProcessor queryProcessor,
    IAccessHashHelper2 accessHashHelper,
    IChannelAdminRightsChecker channelAdminRightsChecker,
    ICommandBus commandBus)
    : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestReorderUsernames, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Channels.RequestReorderUsernames obj)
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
        var updated = UsernameListEditor.Reorder(current, obj.Order);
        if (updated != null)
        {
            await commandBus.PublishAsync(new SetChannelUsernamesCommand(ChannelId.Create(channel.ChannelId),
                input.ToRequestInfo(), updated.ToList(), current));
        }

        return new TBoolTrue();
    }
}
