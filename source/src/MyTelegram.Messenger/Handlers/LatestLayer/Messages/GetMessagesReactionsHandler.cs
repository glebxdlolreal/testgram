using MyTelegram.Messenger.Helpers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Get message reactions.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getMessagesReactions"/> </c></para>
/// </summary>
internal sealed class GetMessagesReactionsHandler(
    IQueryProcessor queryProcessor,
    IPeerHelper peerHelper,
    IUserConverterService userConverterService,
    IChannelAppService channelAppService)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetMessagesReactions, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetMessagesReactions obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        var ownerPeerId = peer.PeerType == PeerType.Channel ? peer.PeerId : input.UserId;

        // Only broadcast channels hide who reacted; supergroups/megagroups show reactions
        bool isBroadcast = false;
        if (peer.PeerType == PeerType.Channel)
        {
            var channel = await channelAppService.GetAsync(peer.PeerId);
            isBroadcast = channel?.Broadcast ?? false;
        }
        var readState = await queryProcessor.ProcessAsync(
            new GetUserConfigByKeyQuery(input.UserId, ReactionReadState.GetKey(peer)));
        var readDate = ReactionReadState.ParseReadDate(readState?.Value);
        var updates = new List<IUpdate>();
        var allUserIds = new List<long>();

        foreach (var msgId in obj.Id)
        {
            var msg = await queryProcessor.ProcessAsync(new GetMessageByPeerIdAndMessageIdQuery(ownerPeerId, msgId)) as MessageReadModel;
            if (msg == null) continue;

            var recentReactions2 = msg.RecentReactions2 ?? [];

            var reactionCounts = (msg.Reactions ?? []).Select(r =>
            {
                // ChosenOrder: index among current user's reactions (bigger = newer per API spec)
                var userReactions = recentReactions2
                    .Where(rr => rr.SenderUserId == input.UserId)
                    .Select((rr, i) => (rr, i))
                    .ToList();
                var chosen = userReactions.FindIndex(x =>
                    x.rr.Reaction is TReactionEmoji e1 && r.Reaction is TReactionEmoji e2 && e1.Emoticon == e2.Emoticon ||
                    x.rr.Reaction is TReactionCustomEmoji c1 && r.Reaction is TReactionCustomEmoji c2 && c1.DocumentId == c2.DocumentId);
                return (IReactionCount)new TReactionCount
                {
                    Reaction = r.Reaction,
                    Count = r.Count,
                    ChosenOrder = chosen >= 0 ? userReactions[chosen].i : (int?)null
                };
            }).ToList();

            var isChannel = isBroadcast;

            // Paid reactions are surfaced through top_reactors only: listing them here would expose
            // the sender of a reaction they may have sent anonymously.
            var nonPaidReactions = recentReactions2.Where(r => r.Reaction is not TReactionPaid).ToList();

            var recentReactions = isChannel ? [] : nonPaidReactions.Select(r => (IMessagePeerReaction)new TMessagePeerReaction
            {
                PeerId = new TPeerUser { UserId = r.SenderUserId },
                Date = r.Date,
                Big = r.Big,
                My = r.SenderUserId == input.UserId,
                Unread = msg.SenderUserId == input.UserId && ReactionReadState.IsUnread(r, input.UserId, readDate),
                Reaction = r.Reaction
            }).ToList();

            if (!isChannel)
                allUserIds.AddRange(nonPaidReactions.Select(r => r.SenderUserId));

            updates.Add(new TUpdateMessageReactions
            {
                Peer = peer.ToPeer(),
                MsgId = msgId,
                Reactions = new TMessageReactions
                {
                    Results = new TVector<IReactionCount>(reactionCounts),
                    RecentReactions = recentReactions.Count > 0 ? new TVector<IMessagePeerReaction>(recentReactions) : null,
                    TopReactors = TopReactorsConverter.ToTl(msg.TopReactors, input.UserId),
                    // Reactions on your own Saved Messages double as tags.
                    ReactionsAsTags = peer.PeerType == PeerType.User && peer.PeerId == input.UserId,
                    CanSeeList = !isChannel
                }
            });
        }

        var users = allUserIds.Count > 0
            ? await userConverterService.GetUserListAsync(input, allUserIds.Distinct().ToList(), false, false, input.Layer)
            : [];

        return new TUpdates
        {
            Updates = new TVector<IUpdate>(updates),
            Users = new TVector<IUser>(users),
            Chats = new TVector<IChat>(),
            Date = CurrentDate,
            Seq = 0
        };
    }
}
