using EventFlow.Exceptions;
using MyTelegram.Messenger.Services.Mentions;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Get unread messages where we were mentioned
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getUnreadMentions"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetUnreadMentionsHandler(
    IQueryProcessor queryProcessor,
    ICommandBus commandBus,
    IAggregateStore aggregateStore,
    MyTelegram.Messenger.Services.AntiSpam.AntiSpamMentionRecountService recounts,
    IPeerHelper peerHelper,
    IMentionReadStateService mentionReadStateService,
    IChannelAppService channelAppService,
    IMessageAppService messageAppService,
    IMessageConverterService messageConverterService,
    IChatConverterService chatConverterService,
    IUserConverterService userConverterService)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetUnreadMentions, MyTelegram.Schema.Messages.IMessages>
{
    protected override async Task<MyTelegram.Schema.Messages.IMessages> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetUnreadMentions obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer.PeerType == PeerType.Channel)
        {
            var channel = await channelAppService.GetAsync((long?)peer.PeerId);
            if (channel == null)
            {
                RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
            }

            if (await channelAppService.SendRpcErrorIfNotChannelMemberAsync(input, channel!))
            {
                return null!;
            }
        }

        var ownerPeerId = peer.PeerType == PeerType.Channel ? peer.PeerId : input.UserId;
        var dialogId = DialogId.Create(input.UserId, peer);
        var recountVersion = !obj.TopMsgId.HasValue
            ? (await aggregateStore.LoadAsync<DialogAggregate, DialogId>(dialogId, CancellationToken.None)).Version
            : 0;
        var readState = await mentionReadStateService.GetAsync(input.UserId, peer);
        var readMaxId = readState?.ReadMaxId ?? 0;
        IReadOnlyList<int> readIds = readState?.ReadIds ?? [];

        var limit = obj.Limit > 0 && obj.Limit <= 100 ? obj.Limit : 20;
        var messageReadModels = await queryProcessor.ProcessAsync(
            new GetMessagesWithUnreadMentionsQuery(
                ownerPeerId,
                input.UserId,
                peer,
                obj.TopMsgId,
                readMaxId,
                readIds,
                obj.OffsetId,
                obj.AddOffset,
                limit,
                obj.MaxId,
                obj.MinId));

        // The @ button counts down through the mention history, so the client needs the total, not
        // just the page: answer with messagesSlice.
        var count = await queryProcessor.ProcessAsync(
            new GetUnreadMentionsCountQuery(ownerPeerId, input.UserId, peer, obj.TopMsgId, readMaxId, readIds));

        // The dialog counter is event-sourced and can drift when mentions vanish outside readMention
        // (a cleared history, a deleted account). This is the one place where the exact number is
        // known anyway, so use it to put the badge back in step.
        if (!obj.TopMsgId.HasValue)
        {
            try
            {
                // Queue first so a process crash or rejected fence cannot lose reconciliation.
                await recounts.EnqueueAsync(input.UserId, peer, CancellationToken.None);
                await commandBus.PublishAsync(
                    new SyncUnreadMentionsCountCommand(dialogId, count, recountVersion));
            }
            catch (DomainError)
            {
                // No dialog aggregate: nothing to keep in step.
            }
        }

        // Build the real messages: returning stubs would leave the client with blank rows.
        var messages = messageConverterService.ToMessageList(input.UserId, messageReadModels, [], [], [], input.Layer);

        var (userIds, channelIds) = messageAppService.GetExtraPeerIds(messageReadModels);
        var channelIdList = channelIds.ToList();
        var channelMemberReadModels = await queryProcessor.ProcessAsync(
            new GetChannelMemberListByChannelIdListQuery(input.UserId, channelIdList));
        var channels = await chatConverterService.GetChannelListAsync(input, channelIdList, channelMemberReadModels, input.Layer);
        var users = await userConverterService.GetUserListAsync(input, userIds.ToList(), false, false, input.Layer);

        return new TMessagesSlice
        {
            Count = count,
            Messages = [.. messages],
            Chats = [.. channels],
            Users = [.. users],
            Topics = new TVector<IForumTopic>()
        };
    }
}
