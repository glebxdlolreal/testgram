using MyTelegram.Messenger.Services.Mentions;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;
/// <summary>
/// Mark <a href="https://corefork.telegram.org/api/channel">channel/supergroup</a> message contents as read, emitting an <a href="https://corefork.telegram.org/constructor/updateChannelReadMessagesContents">updateChannelReadMessagesContents</a>.
/// Also clears the @ badge of the messages, see <a href="https://corefork.telegram.org/api/mentions">mentions</a>.
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 406 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// <para><c>See <a href="https://corefork.telegram.org/method/channels.readMessageContents"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ReadMessageContentsHandler(
    IQueryProcessor queryProcessor,
    IPeerHelper peerHelper,
    IChannelAppService channelAppService,
    MentionReadIntentService mentionReadIntentService)
    : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestReadMessageContents, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Channels.RequestReadMessageContents obj)
    {
        var peer = peerHelper.GetChannel(obj.Channel);
        var channel = await channelAppService.GetAsync((long?)peer.PeerId);
        if (channel == null)
        {
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
        }

        if (await channelAppService.SendRpcErrorIfNotChannelMemberAsync(input, channel!))
        {
            return null!;
        }

        var messageIds = obj.Id?.Distinct().Where(p => p > 0).ToList() ?? [];
        if (messageIds.Count == 0)
        {
            return new TBoolTrue();
        }

        var messages = await queryProcessor.ProcessAsync(
            new GetMessagesByOwnerAndMessageIdListQuery(peer.PeerId, messageIds));

        var mentionedIds = messages
            .Where(p => p.MentionedUserIds?.Contains(input.UserId) ?? false)
            .Select(p => p.MessageId)
            .ToList();

        if (mentionedIds.Count == 0)
        {
            return new TBoolTrue();
        }

        await mentionReadIntentService.ReadAsync(input.UserId, peer, mentionedIds);

        return new TBoolTrue();
    }
}
