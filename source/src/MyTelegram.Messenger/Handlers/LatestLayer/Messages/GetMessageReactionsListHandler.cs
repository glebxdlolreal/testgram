namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
internal sealed class GetMessageReactionsListHandler(
    IQueryProcessor queryProcessor,
    IPeerHelper peerHelper,
    IUserConverterService userConverterService,
    IChannelAppService channelAppService)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetMessageReactionsList, MyTelegram.Schema.Messages.IMessageReactionsList>
{
    protected override async Task<MyTelegram.Schema.Messages.IMessageReactionsList> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetMessageReactionsList obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        var isChannel = peer.PeerType == PeerType.Channel;
        var ownerPeerId = isChannel ? peer.PeerId : input.UserId;
        var msg = await queryProcessor.ProcessAsync(new GetMessageByPeerIdAndMessageIdQuery(ownerPeerId, obj.Id)) as MessageReadModel;

        var reactions = msg?.RecentReactions2 ?? [];

        // Paid reactions are exposed through top_reactors only: including them here would reveal a
        // sender who chose to donate anonymously.
        reactions = reactions.Where(r => r.Reaction is not TReactionPaid).ToList();

        if (obj.Reaction != null)
            reactions = reactions.Where(r =>
                obj.Reaction is TReactionEmoji emoji && r.Reaction is TReactionEmoji re && re.Emoticon == emoji.Emoticon ||
                obj.Reaction is TReactionCustomEmoji custom && r.Reaction is TReactionCustomEmoji rc && rc.DocumentId == custom.DocumentId
            ).ToList();

        // Only broadcast channels hide who reacted
        bool isBroadcast = false;
        if (isChannel)
        {
            var channel = await channelAppService.GetAsync(peer.PeerId);
            isBroadcast = channel?.Broadcast ?? false;
        }

        if (isBroadcast)
        {
            return new TMessageReactionsList
            {
                Count = reactions.Count,
                Reactions = [],
                Users = new TVector<IUser>(),
                Chats = new TVector<IChat>(),
            };
        }

        var readState = await queryProcessor.ProcessAsync(
            new GetUserConfigByKeyQuery(input.UserId, ReactionReadState.GetKey(peer)));
        var readDate = ReactionReadState.ParseReadDate(readState?.Value);

        // Pagination via offset (index-based)
        int.TryParse(obj.Offset, out var offset);
        var limit = obj.Limit > 0 && obj.Limit <= 100 ? obj.Limit : 50;
        var page = reactions.Skip(offset).Take(limit).ToList();

        var reactionsList = page.Select(r => (IMessagePeerReaction)new TMessagePeerReaction
        {
            PeerId = new TPeerUser { UserId = r.SenderUserId },
            Date = r.Date,
            My = r.SenderUserId == input.UserId,
            Unread = msg?.SenderUserId == input.UserId && ReactionReadState.IsUnread(r, input.UserId, readDate),
            Reaction = r.Reaction
        }).ToList();

        var userIds = page.Select(r => r.SenderUserId).Distinct().ToList();
        var users = userIds.Count > 0
            ? await userConverterService.GetUserListAsync(input, userIds, false, false, input.Layer)
            : [];

        string? nextOffset = (offset + page.Count < reactions.Count) ? (offset + page.Count).ToString() : null;

        return new TMessageReactionsList
        {
            Count = reactions.Count,
            Reactions = new TVector<IMessagePeerReaction>(reactionsList),
            Users = new TVector<IUser>(users),
            Chats = new TVector<IChat>(),
            NextOffset = nextOffset
        };
    }
}
