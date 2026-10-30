using MyTelegram.Domain.Aggregates.AntiSpamCompletion;
using MyTelegram.Schema.Extensions;

namespace MyTelegram.ReadModel.Impl;

public class MessageReadModel : IMessageReadModel,
    IAmReadModelFor<AntiSpamCompletionAggregate, AntiSpamCompletionId, AntiSpamCompletionCreatedEvent>,
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
    IAmReadModelFor<MessageAggregate, MessageId, AntiSpamReplyDeletionAppliedEvent>,
    IAmReadModelFor<MessageAggregate, MessageId, MessageReplyCountDecrementedEvent>,
    IAmReadModelFor<MessageAggregate, MessageId, ChannelMessageDeletedEvent>,
    IAmReadModelFor<SendMessageSaga, SendMessageSagaId, PostChannelIdUpdatedSagaEvent>,
    IAmReadModelFor<MessageAggregate, MessageId, MessageUnpinnedEvent>,
    IAmReadModelFor<MessageAggregate, MessageId, MessagePinnedUpdatedEvent>,
    IAmReadModelFor<MessageAggregate, MessageId, MessageViewsIncrementedEvent>,
    IAmReadModelFor<MessageAggregate, MessageId, MessageViewsIncrementedEvent2>,
    IAmReadModelFor<MessageAggregate, MessageId, MessageReactionsUpdatedEvent>,
    IAmReadModelFor<MessageAggregate, MessageId, MessageTodoUpdatedEvent>,
    IAmReadModelFor<MessageAggregate, MessageId, MessageInvoiceReceiptUpdatedEvent>
{
    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<AntiSpamCompletionAggregate, AntiSpamCompletionId, AntiSpamCompletionCreatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        context.MarkForDeletion();
        return Task.CompletedTask;
    }

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
    public bool ForumTopic { get; private set; }
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

    public int? GeoLivePeriod { get; private set; }
    public int? GeoLiveHeading { get; private set; }
    public int? GeoLiveProximityRadius { get; private set; }
    public double? GeoLat { get; private set; }
    public double? GeoLong { get; private set; }

    /// <summary>
    /// Mirrors the queryable fields of a <c>messageMediaGeoLive</c> out of the media blob so a query
    /// can select live locations directly (<c>MessageType.Geo</c> also matches static locations and
    /// venues). Clears them for any other media so an edit that replaces a live location leaves no
    /// stale live-location state behind. See https://corefork.telegram.org/api/live-location
    /// </summary>
    private void ApplyGeoLive(IMessageMedia? media)
    {
        if (media is TMessageMediaGeoLive geoLive)
        {
            GeoLivePeriod = geoLive.Period;
            GeoLiveHeading = geoLive.Heading;
            GeoLiveProximityRadius = geoLive.ProximityNotificationRadius;
            if (geoLive.Geo is TGeoPoint point)
            {
                GeoLat = point.Lat;
                GeoLong = point.Long;
            }
            else
            {
                GeoLat = null;
                GeoLong = null;
            }
        }
        else
        {
            GeoLivePeriod = null;
            GeoLiveHeading = null;
            GeoLiveProximityRadius = null;
            GeoLat = null;
            GeoLong = null;
        }
    }

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
        ApplyGeoLive(messageItem.Media);
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

        Silent = messageItem.Silent;
        PollId = messageItem.PollId;
        ReplyMarkup2 = messageItem.ReplyMarkup;
        ReplyTo = messageItem.InputReplyTo;
        ReplyToMsgId = messageItem.InputReplyTo.ToReplyToMsgId();
        ForumTopic = messageItem.ForumTopic;
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
        FromScheduled = messageItem.ScheduleDate.HasValue || messageItem.FromScheduled;
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
        ApplyGeoLive(messageItem.Media);
        GroupedId = messageItem.GroupId;
        Out = messageItem.IsOut;
        NoForwards = messageItem.NoForwards;
        Views = messageItem.Views;

        Silent = messageItem.Silent;
        PollId = messageItem.PollId;
        ReplyMarkup2 = messageItem.ReplyMarkup;
        ReplyTo = messageItem.InputReplyTo;
        ReplyToMsgId = messageItem.InputReplyTo.ToReplyToMsgId();
        ForumTopic = messageItem.ForumTopic;
        //RandomId = messageItem.RandomId;
        if (messageItem.BatchId.HasValue)
        {
            BatchId = messageItem.BatchId.Value;
        }
        Effect = messageItem.Effect;
        MessageAction = messageItem.MessageAction;
        Pts = messageItem.Pts;
        FromScheduled = messageItem.ScheduleDate.HasValue || messageItem.FromScheduled;
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
        ApplyGeoLive(item.Media);
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
        ApplyGeoLive(item.Media);
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

        if (!AcceptReplyCount(domainEvent.AggregateIdentity.Value, domainEvent.AggregateSequenceNumber)) return Task.CompletedTask;
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

    public string? ReplyCountSourceId { get; private set; }
    public long ReplyCountSourceSequence { get; private set; }
    public string? AntiSpamEffectsSourceId { get; private set; }
    public long AntiSpamEffectsSequence { get; private set; }

    private bool AcceptReplyCount(string sourceId, long sequence)
    {
        // A linked post's reply count is owned by its discussion root, not the post aggregate.
        // Refuse a different established owner rather than comparing unrelated sequences.
        if (ReplyCountSourceId != null && ReplyCountSourceId != sourceId) return false;
        if (sequence <= ReplyCountSourceSequence) return false;
        ReplyCountSourceId = sourceId;
        ReplyCountSourceSequence = sequence;
        return true;
    }

    public Task ApplyAsync(IReadModelContext context, IDomainEvent<MessageAggregate, MessageId, AntiSpamReplyDeletionAppliedEvent> domainEvent, CancellationToken cancellationToken)
    {
        var sourceId = domainEvent.AggregateIdentity.Value;
        var effect = domainEvent.AggregateEvent;
        if (effect.ExpectedStaleSourceId != null && effect.PostChannelId.HasValue && effect.PostMessageId.HasValue &&
            Id == MyTelegram.Domain.Aggregates.Messaging.MessageId.Create(effect.PostChannelId.Value, effect.PostMessageId.Value).Value && Id != sourceId &&
            ReplyCountSourceId == effect.ExpectedStaleSourceId)
        {
            // The command issuer verified the old source does not own this immutable post pair.
            // Compare-and-swap only that exact stale source; never reset the root or a newer owner.
            ReplyCountSourceId = null;
            ReplyCountSourceSequence = 0;
            AntiSpamEffectsSourceId = null;
            AntiSpamEffectsSequence = 0;
        }
        if (ReplyCountSourceId != null && ReplyCountSourceId != sourceId) return Task.CompletedTask;
        AntiSpamEffectsSourceId = sourceId;
        AntiSpamEffectsSequence = Math.Max(AntiSpamEffectsSequence, domainEvent.AggregateSequenceNumber);
        if (!AcceptReplyCount(sourceId, domainEvent.AggregateSequenceNumber)) return Task.CompletedTask;
        if (domainEvent.AggregateEvent.Reply is { } snapshot)
        {
            var repliesPts = Id == sourceId ? domainEvent.AggregateEvent.Pts : Reply?.RepliesPts ?? snapshot.RepliesPts;
            Reply = new MessageReply(snapshot.ChannelId, domainEvent.AggregateEvent.Replies, repliesPts,
                snapshot.MaxId, snapshot.RecentRepliers == null ? null : [.. snapshot.RecentRepliers]);
        }
        else if (Reply != null)
        {
            Reply.Replies = domainEvent.AggregateEvent.Replies;
            if (Id == sourceId) Reply.RepliesPts = domainEvent.AggregateEvent.Pts;
        }
        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context, IDomainEvent<MessageAggregate, MessageId, MessageReplyCountDecrementedEvent> domainEvent, CancellationToken cancellationToken)
    {
        if (!AcceptReplyCount(domainEvent.AggregateIdentity.Value, domainEvent.AggregateSequenceNumber)) return Task.CompletedTask;
        if (Reply != null)
        {
            Reply.Replies = domainEvent.AggregateEvent.Replies;
            Reply.RepliesPts = domainEvent.AggregateEvent.Pts;
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

    public Task ApplyAsync(IReadModelContext context, IDomainEvent<MessageAggregate, MessageId, MessageTodoUpdatedEvent> domainEvent, CancellationToken cancellationToken)
    {
        var e = domainEvent.AggregateEvent;
        Media2 = TodoMediaFactory.Create(e.Todo, e.Completions);
        // Media2 takes precedence over the legacy serialized Media column when mapping to TL
        // (see MessageMapper: source.Media2 ?? source.Media), so the stale blob must be dropped.
        Media = null;

        return Task.CompletedTask;
    }

    /// <summary>
    /// Records the receipt message on a paid invoice, so the bubble offers "Receipt" instead of "Pay".
    /// See https://corefork.telegram.org/api/payments#5-checkout
    /// </summary>
    public Task ApplyAsync(IReadModelContext context, IDomainEvent<MessageAggregate, MessageId, MessageInvoiceReceiptUpdatedEvent> domainEvent, CancellationToken cancellationToken)
    {
        var current = Media2;
        if (current == null && Media is { Length: > 0 })
        {
            var buffer = new ReadOnlyMemory<byte>(Media);
            current = buffer.Read<IMessageMedia>();
        }

        var updated = InvoiceMediaFactory.WithReceipt(current, domainEvent.AggregateEvent.ReceiptMsgId);
        if (ReferenceEquals(updated, current))
        {
            return Task.CompletedTask;
        }

        Media2 = updated;
        // Media2 takes precedence over the legacy serialized Media column when mapping to TL
        // (see MessageMapper: source.Media2 ?? source.Media), so the stale blob must be dropped.
        Media = null;

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
