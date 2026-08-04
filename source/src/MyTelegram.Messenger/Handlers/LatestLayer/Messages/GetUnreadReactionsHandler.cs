namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Get unread reactions to messages you sent.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getUnreadReactions"/> </c></para>
/// </summary>
internal sealed class GetUnreadReactionsHandler(
    IQueryProcessor queryProcessor,
    IPeerHelper peerHelper,
    IMessageAppService messageAppService,
    IMessageConverterService messageConverterService,
    IChatConverterService chatConverterService,
    IUserConverterService userConverterService)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetUnreadReactions, MyTelegram.Schema.Messages.IMessages>
{
    protected override async Task<MyTelegram.Schema.Messages.IMessages> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetUnreadReactions obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        var savedPeer = obj.SavedPeerId == null ? null : peerHelper.GetPeer(obj.SavedPeerId, input.UserId);
        var ownerPeerId = peer.PeerType == PeerType.Channel ? peer.PeerId : input.UserId;
        var readState = await queryProcessor.ProcessAsync(
            new GetUserConfigByKeyQuery(input.UserId, ReactionReadState.GetKey(peer, obj.TopMsgId, savedPeer)));
        var readDate = ReactionReadState.ParseReadDate(readState?.Value);

        var limit = obj.Limit > 0 && obj.Limit <= 100 ? obj.Limit : 20;
        var messageReadModels = await queryProcessor.ProcessAsync(
            new GetMessagesWithUnreadReactionsQuery(
                ownerPeerId,
                input.UserId,
                obj.OffsetId,
                limit,
                obj.MaxId,
                obj.MinId,
                readDate,
                obj.TopMsgId,
                savedPeer));

        // Build the real messages: returning stubs would leave the client with blank rows.
        var messages = messageConverterService.ToMessageList(input.UserId, messageReadModels, [], [], [], input.Layer);

        var (userIds, channelIds) = messageAppService.GetExtraPeerIds(messageReadModels);
        var channelIdList = channelIds.ToList();
        var channelMemberReadModels = await queryProcessor.ProcessAsync(
            new GetChannelMemberListByChannelIdListQuery(input.UserId, channelIdList));
        var channels = await chatConverterService.GetChannelListAsync(input, channelIdList, channelMemberReadModels, input.Layer);
        var users = await userConverterService.GetUserListAsync(input, userIds.ToList(), false, false, input.Layer);

        return new TMessages
        {
            Messages = [.. messages],
            Chats = [.. channels],
            Users = [.. users],
            Topics = new TVector<IForumTopic>()
        };
    }
}
