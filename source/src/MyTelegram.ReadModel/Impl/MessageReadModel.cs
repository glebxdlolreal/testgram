using MyTelegram.Schema.Extensions;

namespace MyTelegram.ReadModel.Impl;

public class MessageReadModel : IMessageReadModel,
    IAmReadModelFor<MessageAggregate, MessageId, OutboxMessageCreatedEvent>,
    IAmReadModelFor<MessageAggregate, MessageId, InboxMessageCreatedEvent>,
    IAmReadModelFor<MessageAggregate, MessageId, OutboxMessageEditedEventV2>,
    IAmReadModelFor<MessageAggregate, MessageId, InboxMessageEditedEventV2>,
    IAmReadModelFor<MessageAggregate, MessageId, InboxMessagePinnedUpdatedEvent>,
    IAmReadModelFor<MessageAggregate, MessageId, OutboxMessagePinnedUpdatedEvent>,
    //IAmReadModelFor<MessageAggregate, MessageId, UpdatePinnedMessageStartedEvent>,
    //IAmReadModelFor<MessageAggregate, MessageId, MessageDeletedEvent>,
    //IAmReadModelFor<MessageAggregate, MessageId, OutboxMessageDeletedEvent>,
    IAmReadModelFor<MessageAggregate, MessageId, InboxMessageDeletedEvent>,
    //IAmReadModelFor<MessageAggregate, MessageId, SelfMessageDeletedEvent>,
    //IAmReadModelFor<SendMessageSaga, SendMessageSagaId, SendOutboxMessageCompletedEvent>,
    //IAmReadModelFor<SendMessageSaga, SendMessageSagaId, ReceiveInboxMessageCompletedEvent>,
    IAmReadModelFor<MessageAggregate, MessageId, MessageDeleted4Event>,
    IAmReadModelFor<MessageAggregate, MessageId, ReplyChannelMessageCompletedEvent>,
    IAmReadModelFor<MessageAggregate, MessageId, ChannelMessagePinnedEvent>,
    IAmReadModelFor<MessageAggregate, MessageId, MessageReplyUpdatedEvent>,
    IAmReadModelFor<MessageAggregate, MessageId, ChannelMessageDeletedEvent>,
    IAmReadModelFor<SendMessageSaga, SendMessageSagaId, PostChannelIdUpdatedSagaEvent>,
    IAmReadModelFor<MessageAggregate, MessageId, MessageUnpinnedEvent>,
    IAmReadModelFor<MessageAggregate, MessageId, MessagePinnedUpdatedEvent>,
    IAmReadModelFor<MessageAggregate, MessageId, MessageViewsIncrementedEvent>,
    IAmReadModelFor<MessageAggregate, MessageId, MessageViewsIncrementedEvent2>,
    IAmReadModelFor<MessageAggregate, MessageId, MessageReactionsUpdatedEvent>
{
    public int Date { get; private set; }
    public int? EditDate { get; private set; }
    public bool EditHide { get; private set; }
    public byte[]? Entities { get; private set; }
    public TVector<IMessageEntity>? Entities2 { get; private set; }
    public MessageFwdHeader? FwdHeader { get; private set; }
    public long? GroupedId { get; private set; }
    public string Id { get; private set; } = null!;
    public byte[]? Media { get; private set; }
    public IMessageMedia? Media2 { get; private set; }
    public string Message { get; private set; } = null!;
    public string? MessageActionData { get; private set; }
    public IMessageAction? MessageAction { get; private set; }
    public MessageActionType MessageActionType { get; private set; }
    public MessageType MessageType { get; private set; }
    public List<MessageReactor>? TopReactors { get; private set; }

    /// <summary>Number of paid reaction senders the client renders as "top" donors.</summary>
    private const int TopReactorsCount = 3;
    public int MessageId { get; private set; }
    public bool Out { get; private set; }
    public bool NoForwards { get; private set; }
    public long OwnerPeerId { get; private set; }
    public bool Pinned { get; private set; }
    public bool Post { get; private set; }
    public string? PostAuthor { get; private set; }
    public int Pts { get; private set; }
    public int? ReplyToMsgId { get; private set; }
    public int? TopMsgId { get; private set; }
    public int SenderMessageId { get; private set; }
    public long SenderPeerId { get; private set; }
    public long SenderUserId { get; private set; }
    public SendMessageType SendMessageType { get; private set; }
    public bool Silent { get; private set; }
    public long ToPeerId { get; private set; }
    public PeerType ToPeerType { get; private set; }
    public Peer? SavedPeerId { get; private set; }
    public int? Views { get; private set; }
    public long? LinkedChannelId { get; private set; }
    public int Replies { get; private set; }
    public int? SavedFromMsgId { get; private set; }
    public long? SavedFromPeerId { get; private set; }
    public virtual long? Version { get; set; }
    public long? PollId { get; private set; }
    public byte[]? ReplyMarkup { get; private set; }
    public IReplyMarkup? ReplyMarkup2 { get; private set; }
    public IInputReplyTo? ReplyTo { get; private set; }
    public Peer? SendAs { get; private set; }
    public MessageReply? Reply { get; private set; }
    public long? PostChannelId { get; private set; }
    public int? PostMessageId { get; private set; }
    public bool IsQuickReplyMessage { get; private set; }
    public int? ShortcutId { get; private set; }
    public QuickReplyItem? QuickReplyItem { get; private set; }
    public Guid BatchId { get; private set; }

    public long? Effect { get; private set; }

    public bool FromScheduled { get; private set; }

    public int? ScheduleDate { get; private set; }
    public int? TtlPeriod { get; private set; }
    public List<ReactionCount>? Reactions { get; private set; }
    public List<MessagePeerReaction>? RecentReactions2 { get; private set; }
    public List<Reaction>? RecentReactions { get; private set; }
    public bool CanSeeList { get; private set; }

    public int? ExpirationTime { get; private set; }
    public bool InvertMedia { get; private set; }
    public bool PublicPosts { get; private set; }
    public List<string> Hashtags { get; private set; } = [];
    public List<long>? MentionedUserIds { get; private set; }
    public long? TodoId { get; private set; }
    public ReadOnlyMemory<byte>? EncryptedData { get; private set; }
    public long? PaidMessageStars { get; private set; }
    public ISuggestedPost? SuggestedPost { get; private set; }
    public bool PaidSuggestedPostStars { get; private set; }
    public bool PaidSuggestedPostTon { get; private set; }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<MessageAggregate, MessageId, OutboxMessageCreatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var messageItem = domainEvent.AggregateEvent.OutboxMessageItem;
        Id = domainEvent.AggregateIdentity.Value;
        OwnerPeerId = messageItem.OwnerPeer.PeerId;
        SenderPeerId = messageItem.SenderPeer.PeerId;
        SenderUserId = messageItem.SenderUserId;
        ToPeerType = messageItem.ToPeer.PeerType;
        ToPeerId = messageItem.ToPeer.PeerId;
        MessageType = messageItem.MessageType;
        MessageId = messageItem.MessageId;
        Message = messageItem.Message;
        Entities2 = messageItem.Entities;
        Date = messageItem.Date;
        SenderMessageId = messageItem.MessageId;
        //MessageActionData = messageItem.MessageActionData;
        MessageActionType = messageItem.MessageActionType;
        //ReplyToMsgId = messageItem.ReplyToMsgId;
        TopMsgId = messageItem.TopMsgId;
        FwdHeader = messageItem.FwdHeader;
        SendMessageType = messageItem.SendMessageType;
        Media2 = messageItem.Media;
        GroupedId = messageItem.GroupId;
        Out = messageItem.IsOut;
        NoForwards = messageItem.NoForwards;
        Views = messageItem.Views;
        Post = messageItem.Post;
        LinkedChannelId = domainEvent.AggregateEvent.LinkedChannelId;
        PostAuthor = messageItem.PostAuthor;

        if (domainEvent.AggregateEvent.OutboxMessageItem.FwdHeader != null)
        {
            var fwdHeader = domainEvent.AggregateEvent.OutboxMessageItem.FwdHeader;
            if (fwdHeader.SavedFromPeer != null)
            {
                SavedFromPeerId = fwdHeader.SavedFromPeer.PeerId;
                SavedFromMsgId = fwdHeader.SavedFromMsgId;
            }
        }

        Silent = false;
        PollId = messageItem.PollId;
        ReplyMarkup2 = messageItem.ReplyMarkup;
        ReplyTo = messageItem.InputReplyTo;
        ReplyToMsgId = messageItem.InputReplyTo.ToReplyToMsgId();
        SendAs = messageItem.SendAs;
        Reply = messageItem.Reply;
        EditHide = messageItem.EditHide;
        PostChannelId = messageItem.PostChannelId;
        PostMessageId = messageItem.PostMessageId;
        if (messageItem.BatchId.HasValue)
        {
            BatchId = messageItem.BatchId.Value;
        }

        Effect = messageItem.Effect;
        MessageAction = messageItem.MessageAction;
        Pts = messageItem.Pts;
        FromScheduled = messageItem.ScheduleDate.HasValue;
        ScheduleDate = messageItem.ScheduleDate;
        TtlPeriod = messageItem.TtlPeriod;
        if (messageItem.TtlPeriod.HasValue && messageItem.TtlPeriod != 0)
        {
            ExpirationTime = messageItem.Date + messageItem.TtlPeriod.Value;
        }
        Pinned = messageItem.Pinned;
        InvertMedia = messageItem.InvertMedia;
        MentionedUserIds = messageItem.MentionedUserIds;
        EncryptedData = messageItem.EncryptedData;
        PaidMessageStars = messageItem.PaidMessageStars;
        SuggestedPost = messageItem.SuggestedPost;
        PaidSuggestedPostStars = messageItem.PaidSuggestedPostStars;
        PaidSuggestedPostTon = messageItem.PaidSuggestedPostTon;
        SavedPeerId = messageItem.SavedPeerId;

        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<MessageAggregate, MessageId, InboxMessageCreatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var messageItem = domainEvent.AggregateEvent.InboxMessageItem;
        Id = domainEvent.AggregateIdentity.Value;
        OwnerPeerId = messageItem.OwnerPeer.PeerId;
        SenderPeerId = messageItem.SenderPeer.PeerId;
        SenderUserId = messageItem.SenderUserId;
        ToPeerType = messageItem.ToPeer.PeerType;
        ToPeerId = messageItem.ToPeer.PeerId;
        MessageType = messageItem.MessageType;
        MessageId = messageItem.MessageId;
        Message = messageItem.Message;
        Entities2 = messageItem.Entities;
        Date = messageItem.Date;
        SenderMessageId = domainEvent.AggregateEvent.SenderMessageId;
        //MessageActionData = messageItem.MessageActionData;
        MessageActionType = messageItem.MessageActionType;
        //ReplyToMsgId = messageItem.ReplyToMsgId;
        FwdHeader = messageItem.FwdHeader;
        SendMessageType = messageItem.SendMessageType;
        Media2 = messageItem.Media;
        GroupedId = messageItem.GroupId;
        Out = messageItem.IsOut;
        NoForwards = messageItem.NoForwards;
        Views = messageItem.Views;

        Silent = false;
        PollId = messageItem.PollId;
        ReplyMarkup2 = messageItem.ReplyMarkup;
        ReplyTo = messageItem.InputReplyTo;
        ReplyToMsgId = messageItem.InputReplyTo.ToReplyToMsgId();
        //RandomId = messageItem.RandomId;
        if (messageItem.BatchId.HasValue)
        {
            BatchId = messageItem.BatchId.Value;
        }
        Effect = messageItem.Effect;
        MessageAction = messageItem.MessageAction;
        Pts = messageItem.Pts;
        FromScheduled = messageItem.ScheduleDate.HasValue;
        ScheduleDate = messageItem.ScheduleDate;
        TtlPeriod = messageItem.TtlPeriod;
        if (messageItem.TtlPeriod.HasValue && messageItem.TtlPeriod != 0)
        {
            ExpirationTime = messageItem.Date + messageItem.TtlPeriod.Value;
        }

        InvertMedia = messageItem.InvertMedia;
        EncryptedData = messageItem.EncryptedData;
        PaidMessageStars = messageItem.PaidMessageStars;
        SuggestedPost = messageItem.SuggestedPost;
        PaidSuggestedPostStars = messageItem.PaidSuggestedPostStars;
        PaidSuggestedPostTon = messageItem.PaidSuggestedPostTon;
        SavedPeerId = messageItem.SavedPeerId;

        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<MessageAggregate, MessageId, OutboxMessageEditedEventV2> domainEvent,
        CancellationToken cancellationToken)
    {
        var item = domainEvent.AggregateEvent.NewMessageItem;
        Message = item.Message;
        Entities2 = item.Entities;
        EditDate = item.EditDate;
        ReplyMarkup2 = item.ReplyMarkup;
        Media2 = item.Media;
        InvertMedia = item.InvertMedia;
        EncryptedData = item.EncryptedData;

        return Task.CompletedTask;
    }

    public Task ApplyAsync(
        IReadModelContext context,
        IDomainEvent<MessageAggregate, MessageId, MessageViewsIncrementedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        Views = domainEvent.AggregateEvent.Views;

        return Task.CompletedTask;
    }

    public Task ApplyAsync(
        IReadModelContext context,
        IDomainEvent<MessageAggregate, MessageId, MessageViewsIncrementedEvent2> domainEvent,
        CancellationToken cancellationToken)
    {
        Views = domainEvent.AggregateEvent.Views;

        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<MessageAggregate, MessageId, InboxMessageEditedEventV2> domainEvent,
        CancellationToken cancellationToken)
    {
        var item = domainEvent.AggregateEvent.NewMessageItem;
        Message = item.Message;
        Entities2 = item.Entities;
        EditDate = item.EditDate;
        ReplyMarkup2 = item.ReplyMarkup;
        Media2 = item.Media;
        InvertMedia = item.InvertMedia;
        EncryptedData = item.EncryptedData;

        return Task.CompletedTask;
    }

    //public Task ApplyAsync(IReadModelContext context,
    //    IDomainEvent<SendMessageSaga, SendMessageSagaId, SendOutboxMessageCompletedEvent> domainEvent,
    //    CancellationToken cancellationToken)
    //{
    //    if (string.IsNullOrEmpty(Id))
    //    {
    //        Id = MyTelegram.Domain.Aggregates.Messaging.MessageId.Create(
    //            domainEvent.AggregateEvent.MessageItem.OwnerPeer.PeerId,
    //            domainEvent.AggregateEvent.MessageItem.MessageId).Value;
    //    }
    //    Pts = domainEvent.AggregateEvent.Pts;
    //    return Task.CompletedTask;
    //}

    //public Task ApplyAsync(IReadModelContext context,
    //    IDomainEvent<SendMessageSaga, SendMessageSagaId, ReceiveInboxMessageCompletedEvent> domainEvent,
    //    CancellationToken cancellationToken)
    //{
    //    if (string.IsNullOrEmpty(Id))
    //    {
    //        Id = MyTelegram.Domain.Aggregates.Messaging.MessageId.Create(
    //            domainEvent.AggregateEvent.MessageItem.OwnerPeer.PeerId,
    //            domainEvent.AggregateEvent.MessageItem.MessageId).Value;
    //    }
    //    Pts = domainEvent.AggregateEvent.Pts;
    //    return Task.CompletedTask;
    //}

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<MessageAggregate, MessageId, InboxMessagePinnedUpdatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        Pinned = domainEvent.AggregateEvent.Pinned;
        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<MessageAggregate, MessageId, OutboxMessagePinnedUpdatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        Pinned = domainEvent.AggregateEvent.Pinned;
        return Task.CompletedTask;
    }

    //public Task ApplyAsync(IReadModelContext context,
    //    IDomainEvent<MessageAggregate, MessageId, UpdatePinnedMessageStartedEvent> domainEvent,
    //    CancellationToken cancellationToken)
    //{
    //    Pinned = domainEvent.AggregateEvent.Pinned;
    //    return Task.CompletedTask;
    //}

    //public Task ApplyAsync(IReadModelContext context,
    //    IDomainEvent<MessageAggregate, MessageId, MessageDeletedEvent> domainEvent,
    //    CancellationToken cancellationToken)
    //{
    //    context.MarkForDeletion();
    //    return Task.CompletedTask;
    //}

    //public Task ApplyAsync(IReadModelContext context,
    //    IDomainEvent<MessageAggregate, MessageId, OutboxMessageDeletedEvent> domainEvent,
    //    CancellationToken cancellationToken)
    //{
    //    context.MarkForDeletion();
    //    return Task.CompletedTask;
    //}

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<MessageAggregate, MessageId, InboxMessageDeletedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        context.MarkForDeletion();
        return Task.CompletedTask;
    }

    //public Task ApplyAsync(IReadModelContext context,
    //    IDomainEvent<MessageAggregate, MessageId, SelfMessageDeletedEvent> domainEvent,
    //    CancellationToken cancellationToken)
    //{
    //    context.MarkForDeletion();
    //    return Task.CompletedTask;
    //}

    public Task ApplyAsync(IReadModelContext context, IDomainEvent<MessageAggregate, MessageId, MessageDeleted4Event> domainEvent, CancellationToken cancellationToken)
    {
        context.MarkForDeletion();
        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context, IDomainEvent<MessageAggregate, MessageId, ReplyChannelMessageCompletedEvent> domainEvent, CancellationToken cancellationToken)
    {

        Reply = domainEvent.AggregateEvent.Reply;

        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context, IDomainEvent<MessageAggregate, MessageId, ChannelMessagePinnedEvent> domainEvent, CancellationToken cancellationToken)
    {
        Pinned = true;

        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context, IDomainEvent<MessageAggregate, MessageId, MessageReplyUpdatedEvent> domainEvent, CancellationToken cancellationToken)
    {
        if (Reply != null)
        {
            Reply.ChannelId = domainEvent.AggregateEvent.ChannelId;
        }

        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context, IDomainEvent<MessageAggregate, MessageId, ChannelMessageDeletedEvent> domainEvent, CancellationToken cancellationToken)
    {
        context.MarkForDeletion();

        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context, IDomainEvent<SendMessageSaga, SendMessageSagaId, PostChannelIdUpdatedSagaEvent> domainEvent, CancellationToken cancellationToken)
    {
        PostChannelId = domainEvent.AggregateEvent.PostChannelId;
        PostMessageId = domainEvent.AggregateEvent.PostMessageId;

        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context, IDomainEvent<MessageAggregate, MessageId, MessageUnpinnedEvent> domainEvent, CancellationToken cancellationToken)
    {
        Pinned = false;

        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context, IDomainEvent<MessageAggregate, MessageId, MessagePinnedUpdatedEvent> domainEvent, CancellationToken cancellationToken)
    {
        Pinned = domainEvent.AggregateEvent.Pinned;

        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context, IDomainEvent<MessageAggregate, MessageId, MessageReactionsUpdatedEvent> domainEvent, CancellationToken cancellationToken)
    {
        var reactions = domainEvent.AggregateEvent.Reactions;
        var counts = new Dictionary<long, ReactionCount>();
        foreach (var r in reactions)
        {
            var id = r.GetReactionId();
            if (counts.TryGetValue(id, out var existing))
                existing.Count++;
            else
                counts[id] = new ReactionCount(
                    r.IsPaid ? new TReactionPaid()
                        : string.IsNullOrEmpty(r.Emoticon)
                        ? new TReactionCustomEmoji { DocumentId = r.CustomEmojiDocumentId!.Value }
                        : (IReaction)new TReactionEmoji { Emoticon = r.Emoticon },
                    1, r.Emoticon, r.CustomEmojiDocumentId);
        }
        Reactions = counts.Values.ToList();
        RecentReactions2 = reactions.Select(r => new MessagePeerReaction
        {
            PeerId = new Peer(PeerType.User, r.UserId),
            SenderUserId = r.UserId,
            Date = r.Date ?? 0,
            Reaction = r.IsPaid ? new TReactionPaid()
                : string.IsNullOrEmpty(r.Emoticon)
                ? new TReactionCustomEmoji { DocumentId = r.CustomEmojiDocumentId!.Value }
                : (IReaction)new TReactionEmoji { Emoticon = r.Emoticon }
        }).ToList();

        TopReactors = BuildTopReactors(reactions);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Builds the paid reaction leaderboard shown under a post: senders ranked by how many stars
    /// they spent, with the top three flagged.
    /// See https://corefork.telegram.org/api/reactions#paid-reactions
    /// </summary>
    private static List<MessageReactor>? BuildTopReactors(List<Reaction> reactions)
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
                return new MessageReactor
                {
                    // An anonymous reactor exposes no peer at all.
                    PeerId = anonymous
                        ? null
                        : peerId != 0
                            ? new Peer(PeerType.Channel, peerId)
                            : new Peer(PeerType.User, g.Key),
                    Anonymous = anonymous,
                    SenderUserId = g.Key,
                    Count = g.Count()
                };
            })
            .OrderByDescending(r => r.Count)
            .ToList();

        for (var i = 0; i < reactors.Count && i < TopReactorsCount; i++)
        {
            reactors[i].Top = true;
        }

        return reactors;
    }
}
