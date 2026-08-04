using EventFlow.Exceptions;
using MyTelegram.Messenger.Services.Bots;
using MyTelegram.Messenger.Services.Caching;
using MyTelegram.Messenger.Services.Interfaces;

namespace MyTelegram.Messenger.QueryServer.DomainEventHandlers;

public class ReactionDomainEventHandler(
    IObjectMessageSender objectMessageSender,
    ICommandBus commandBus,
    IIdGenerator idGenerator,
    IAckCacheService ackCacheService,
    IQueryProcessor queryProcessor,
    IPtsHelper ptsHelper,
    IChannelAppService channelAppService,
    IPeerHelper peerHelper,
    IBotUpdatesSender botUpdatesSender)
    : DomainEventHandlerBase(objectMessageSender, commandBus, idGenerator, ackCacheService),
        ISubscribeSynchronousTo<MessageAggregate, MessageId, MessageReactionsUpdatedEvent>
{
    public async Task HandleAsync(
        IDomainEvent<MessageAggregate, MessageId, MessageReactionsUpdatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var e = domainEvent.AggregateEvent;
        var messageItem = e.MessageItem;
        var reactions = e.Reactions;

        var isChannel = messageItem.OwnerPeer.PeerType == PeerType.Channel && messageItem.ToPeer.PeerType == PeerType.Channel;
        if (isChannel)
        {
            var channel = await channelAppService.GetAsync(messageItem.OwnerPeer.PeerId);
            isChannel = channel?.Broadcast ?? false;
        }

        // Reactions on your own Saved Messages double as tags.
        var reactionsAsTags = messageItem.OwnerPeer.PeerType == PeerType.User
                              && messageItem.ToPeer.PeerType == PeerType.User
                              && messageItem.OwnerPeer.PeerId == messageItem.ToPeer.PeerId;

        // chosen_order, my and unread are per-viewer, so the payload is rebuilt for each recipient
        // rather than sharing one object across the broadcast.
        TMessageReactions BuildMessageReactions(long viewerUserId)
        {
            var viewerReactionOrders = reactions
                .Where(r => r.UserId == viewerUserId && !r.IsPaid)
                .Select((r, i) => (Id: r.GetReactionId(), Order: i))
                .GroupBy(x => x.Id)
                .ToDictionary(g => g.Key, g => g.First().Order);

            var reactionCounts = reactions
                .GroupBy(r => r.GetReactionId())
                .Select(g =>
                {
                    var first = g.First();
                    return (IReactionCount)new TReactionCount
                    {
                        Reaction = first.IsPaid ? new TReactionPaid()
                            : string.IsNullOrEmpty(first.Emoticon)
                            ? new TReactionCustomEmoji { DocumentId = first.CustomEmojiDocumentId!.Value }
                            : (IReaction)new TReactionEmoji { Emoticon = first.Emoticon },
                        Count = g.Count(),
                        ChosenOrder = viewerReactionOrders.TryGetValue(g.Key, out var order) ? order : (int?)null
                    };
                })
                .ToList();

            // Paid reactions are surfaced through top_reactors only: listing them here would expose
            // the sender of a reaction they may have chosen to send anonymously.
            var recentReactions = isChannel
                ? []
                : reactions.Where(r => !r.IsPaid).Select(r => (IMessagePeerReaction)new TMessagePeerReaction
                {
                    PeerId = new TPeerUser { UserId = r.UserId },
                    Date = r.Date ?? 0,
                    Big = r.Big,
                    My = r.UserId == viewerUserId,
                    Unread = r.UserId != viewerUserId && messageItem.SenderUserId == viewerUserId,
                    Reaction = string.IsNullOrEmpty(r.Emoticon)
                        ? new TReactionCustomEmoji { DocumentId = r.CustomEmojiDocumentId!.Value }
                        : new TReactionEmoji { Emoticon = r.Emoticon }
                }).ToList();

            return new TMessageReactions
            {
                Results = new TVector<IReactionCount>(reactionCounts),
                RecentReactions = recentReactions.Count > 0 ? new TVector<IMessagePeerReaction>(recentReactions) : null,
                TopReactors = BuildTopReactors(reactions, viewerUserId),
                ReactionsAsTags = reactionsAsTags,
                CanSeeList = !isChannel
            };
        }

        var messageReactions = BuildMessageReactions(e.RequestInfo.UserId);

        var ownerPeer = messageItem.OwnerPeer;
        var pts = await ptsHelper.IncrementPtsAsync(ownerPeer.PeerId, 1);
        
        var senderPeer = messageItem.SenderUserId == 0 ? ownerPeer : new Peer(PeerType.User, messageItem.SenderUserId);

        var updateReactions = new TUpdateMessageReactions
        {
            Peer = senderPeer.ToPeer(),
            MsgId = messageItem.MessageId,
            Reactions = messageReactions
        };

        var updates = new TUpdates
        {
            Updates = new TVector<IUpdate>(updateReactions),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = DateTime.UtcNow.ToTimestamp(),
            Seq = 0
        };

        await SendRpcMessageToClientAsync(e.RequestInfo, updates, ownerPeer.PeerId, pts, ownerPeer.PeerType);

        await TryIncrementUnreadReactionsAsync(e, messageItem);
        await NotifyBotsAsync(e, messageItem, isChannel);

        var toPeer = messageItem.ToPeer;
        if (toPeer.PeerType == PeerType.User)
        {
            // For outbox PM messages (Out=true), the inbox event already handles both sides.
            // Only push to the other party from the inbox event (Out=false) to avoid duplicates.
            if (messageItem.IsOut)
                return;

            var otherUserId = messageItem.SenderUserId == e.RequestInfo.UserId
                ? toPeer.PeerId
                : messageItem.SenderUserId;

            // Get the outbox MessageId at the sender's side
            var inboxReadModel = await queryProcessor.ProcessAsync(
                new GetMessageByPeerIdAndMessageIdQuery(ownerPeer.PeerId, messageItem.MessageId)) as IMessageReadModel;
            var outboxMsgId = inboxReadModel?.SenderMessageId > 0
                ? inboxReadModel.SenderMessageId
                : messageItem.MessageId;

            // The other user (sender of the original message) sees peer = the reactor (ownerPeer)
            var otherPeer = new Peer(PeerType.User, otherUserId);
            var otherUpdate = new TUpdateMessageReactions
            {
                Peer = ownerPeer.ToPeer(),
                MsgId = outboxMsgId,
                Reactions = BuildMessageReactions(otherUserId)
            };
            var otherUpdates = new TUpdates
            {
                Updates = new TVector<IUpdate>(otherUpdate),
                Users = new TVector<IUser>(),
                Chats = new TVector<IChat>(),
                Date = DateTime.UtcNow.ToTimestamp(),
                Seq = 0
            };
            await PushUpdatesToPeerAsync(otherPeer, otherUpdates,
                excludeAuthKeyId: e.RequestInfo.PermAuthKeyId,
                pts: pts);
        }
        else if (toPeer.PeerType == PeerType.Channel || toPeer.PeerType == PeerType.Chat)
        {
            var channelUpdate = new TUpdateMessageReactions
            {
                Peer = toPeer.ToPeer(),
                MsgId = messageItem.MessageId,
                // One payload reaches every member, so it is built for no particular viewer: the
                // per-viewer flags stay unset and clients refresh them via messages.getMessagesReactions.
                Reactions = BuildMessageReactions(0)
            };
            var channelUpdates = new TUpdates
            {
                Updates = new TVector<IUpdate>(channelUpdate),
                Users = new TVector<IUser>(),
                Chats = new TVector<IChat>(),
                Date = DateTime.UtcNow.ToTimestamp(),
                Seq = 0
            };
            await PushUpdatesToPeerAsync(toPeer, channelUpdates,
                excludeAuthKeyId: e.RequestInfo.PermAuthKeyId,
                pts: pts);
        }
    }

    /// <summary>
    /// Raises the unread reaction badge on the message author's dialog when somebody else reacts.
    /// Cleared again by messages.readReactions.
    /// See https://corefork.telegram.org/api/reactions
    /// </summary>
    private async Task TryIncrementUnreadReactionsAsync(MessageReactionsUpdatedEvent e, MessageItem messageItem)
    {
        var authorUserId = messageItem.SenderUserId;

        // Reacting to your own message, or to a message with no human author, raises no badge.
        if (authorUserId == 0 || authorUserId == e.RequestInfo.UserId)
        {
            return;
        }

        // Only count reactions that were actually added by someone else.
        if (!e.Reactions.Any(r => r.UserId != authorUserId))
        {
            return;
        }

        var dialogPeer = messageItem.ToPeer.PeerType == PeerType.User
            ? new Peer(PeerType.User, e.RequestInfo.UserId)
            : messageItem.ToPeer;

        try
        {
            await commandBus.PublishAsync(new CreateUnreadReactionCommand(
                DialogId.Create(authorUserId, dialogPeer),
                messageItem.MessageId));
        }
        catch (DomainError)
        {
            // No dialog aggregate yet (for example a legacy chat): the badge is best-effort.
        }
    }

    /// <summary>
    /// Delivers reaction updates to bots that are members of the chat. Bots in groups and private
    /// chats see who reacted (updateBotMessageReaction); in broadcast channels, where the reaction
    /// list is hidden, they only get anonymous counters (updateBotMessageReactions).
    /// See https://corefork.telegram.org/api/reactions
    /// </summary>
    private async Task NotifyBotsAsync(MessageReactionsUpdatedEvent e, MessageItem messageItem, bool isBroadcast)
    {
        var botUserIds = await GetBotMemberIdsAsync(messageItem);
        if (botUserIds.Count == 0)
        {
            return;
        }

        var peer = messageItem.ToPeer.ToPeer();
        var date = DateTime.UtcNow.ToTimestamp();

        if (isBroadcast)
        {
            var reactionCounts = e.Reactions
                .GroupBy(r => r.GetReactionId())
                .Select(g => (IReactionCount)new TReactionCount
                {
                    Reaction = ToReaction(g.First()),
                    Count = g.Count()
                })
                .ToList();

            foreach (var botUserId in botUserIds)
            {
                await botUpdatesSender.PushUpdateToBotAsync(botUserId, qts => new TUpdateBotMessageReactions
                {
                    Peer = peer,
                    MsgId = messageItem.MessageId,
                    Date = date,
                    Reactions = new TVector<IReactionCount>(reactionCounts),
                    Qts = qts
                });
            }

            return;
        }

        // Paid reactions are excluded: they may be anonymous and are not part of the public list.
        var actorUserId = e.RequestInfo.UserId;
        var oldReactions = e.OldReactions
            .Where(r => r.UserId == actorUserId && !r.IsPaid)
            .Select(ToReaction)
            .ToList();
        var newReactions = e.Reactions
            .Where(r => r.UserId == actorUserId && !r.IsPaid)
            .Select(ToReaction)
            .ToList();

        // Nothing changed for this actor, so there is nothing to report.
        if (oldReactions.Count == 0 && newReactions.Count == 0)
        {
            return;
        }

        var actor = new Peer(PeerType.User, actorUserId).ToPeer();

        foreach (var botUserId in botUserIds)
        {
            await botUpdatesSender.PushUpdateToBotAsync(botUserId, qts => new TUpdateBotMessageReaction
            {
                Peer = peer,
                MsgId = messageItem.MessageId,
                Date = date,
                Actor = actor,
                OldReactions = new TVector<IReaction>(oldReactions),
                NewReactions = new TVector<IReaction>(newReactions),
                Qts = qts
            });
        }
    }

    private async Task<List<long>> GetBotMemberIdsAsync(MessageItem messageItem)
    {
        switch (messageItem.ToPeer.PeerType)
        {
            case PeerType.Channel:
            {
                var channel = await channelAppService.GetAsync(messageItem.ToPeer.PeerId);
                return channel?.Bots?.Distinct().ToList() ?? [];
            }

            // In a private chat the counterparty may itself be a bot.
            case PeerType.User:
            {
                var counterpartyUserId = messageItem.ToPeer.PeerId;
                return peerHelper.IsBotUser(counterpartyUserId) ? [counterpartyUserId] : [];
            }

            default:
                return [];
        }
    }

    private static IReaction ToReaction(Reaction reaction)
    {
        return reaction.IsPaid
            ? new TReactionPaid()
            : string.IsNullOrEmpty(reaction.Emoticon)
                ? new TReactionCustomEmoji { DocumentId = reaction.CustomEmojiDocumentId!.Value }
                : new TReactionEmoji { Emoticon = reaction.Emoticon };
    }

    /// <summary>
    /// Builds the paid reaction leaderboard for one viewer.
    /// See https://corefork.telegram.org/api/reactions#paid-reactions
    /// </summary>
    private static TVector<IMessageReactor>? BuildTopReactors(List<Reaction> reactions, long viewerUserId)
    {
        var paidReactions = reactions.Where(r => r.IsPaid).ToList();
        if (paidReactions.Count == 0)
        {
            return null;
        }

        var reactors = paidReactions
            .GroupBy(r => r.UserId)
            .Select(g =>
            {
                var anonymous = g.Any(r => r.Anonymous);
                var peerId = g.Select(r => r.AnonymousPeerId).FirstOrDefault(id => id != 0);
                return new TMessageReactor
                {
                    Anonymous = anonymous,
                    My = g.Key == viewerUserId,
                    PeerId = anonymous
                        ? null
                        : peerId != 0
                            ? new TPeerChannel { ChannelId = peerId }
                            : new TPeerUser { UserId = g.Key },
                    Count = g.Count()
                };
            })
            .OrderByDescending(r => r.Count)
            .ToList();

        for (var i = 0; i < reactors.Count && i < 3; i++)
        {
            reactors[i].Top = true;
        }

        return new TVector<IMessageReactor>(reactors.Cast<IMessageReactor>());
    }
}
