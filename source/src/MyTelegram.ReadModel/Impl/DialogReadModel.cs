namespace MyTelegram.ReadModel.Impl;

public class DialogReadModel : IDialogReadModel,
    IAmReadModelFor<DialogAggregate, DialogId, DialogCreatedEvent>,
    IAmReadModelFor<DialogAggregate, DialogId, SetOutboxTopMessageSuccessEvent>,
    IAmReadModelFor<DialogAggregate, DialogId, InboxMessageReceivedEvent>,
    IAmReadModelFor<DialogAggregate, DialogId, DraftSavedEvent>,
    IAmReadModelFor<DialogAggregate, DialogId, DraftClearedEvent>,
    IAmReadModelFor<DialogAggregate, DialogId, ReadInboxMessage2Event>,
    IAmReadModelFor<DialogAggregate, DialogId, OutboxMessageHasReadEvent>,
    IAmReadModelFor<DialogAggregate, DialogId, ReadChannelInboxMessageEvent>,
    IAmReadModelFor<DialogAggregate, DialogId, ChannelHistoryClearedEvent>,
    IAmReadModelFor<DialogAggregate, DialogId, HistoryClearedEvent>,
    IAmReadModelFor<DialogAggregate, DialogId, ParticipantHistoryClearedEvent>,
    IAmReadModelFor<DialogAggregate, DialogId, DialogPinChangedEvent>,
    IAmReadModelFor<DialogAggregate, DialogId, UpdateReadChannelOutboxEvent>,
    IAmReadModelFor<DialogAggregate, DialogId, PinnedOrderChangedEvent>,
    IAmReadModelFor<MessageAggregate, MessageId, OutboxMessagePinnedUpdatedEvent>,
    IAmReadModelFor<MessageAggregate, MessageId, InboxMessagePinnedUpdatedEvent>,
    IAmReadModelFor<MessageAggregate, MessageId, OutboxMessageCreatedEvent>,
    IAmReadModelFor<SendMessageSaga, SendMessageSagaId, SendOutboxMessageCompletedSagaEvent>,
    IAmReadModelFor<PeerNotifySettingsAggregate, PeerNotifySettingsId, PeerNotifySettingsUpdatedEvent>,
    IAmReadModelFor<DialogAggregate, DialogId, ReadInboxMaxIdUpdatedEvent>,
    IAmReadModelFor<DialogAggregate, DialogId, ReadOutboxMaxIdUpdatedEvent>,
    IAmReadModelFor<DialogAggregate, DialogId, TopMessageIdUpdatedEvent>,
    IAmReadModelFor<DialogAggregate, DialogId, UpdateReadChannelInboxEvent>,
    IAmReadModelFor<DialogAggregate, DialogId, DialogFolderUpdatedEvent>,
    IAmReadModelFor<DialogAggregate, DialogId, UnreadReactionCreatedEvent>,
    IAmReadModelFor<DialogAggregate, DialogId, UnreadReactionRemovedEvent>,
    IAmReadModelFor<DialogAggregate, DialogId, UnreadReactionsReadEvent>,
    IAmReadModelFor<DialogAggregate, DialogId, MentionCreatedEvent>,
    IAmReadModelFor<DialogAggregate, DialogId, AntiSpamMentionDeletionAppliedEvent>,
    IAmReadModelFor<DialogAggregate, DialogId, MentionReadEvent>,
    IAmReadModelFor<DialogAggregate, DialogId, UnreadMentionsReadEvent>,
    IAmReadModelFor<DialogAggregate, DialogId, UnreadMentionsCountSyncedEvent>,
    IAmReadModelFor<DialogAggregate, DialogId, PollVoteCreatedEvent>,
    IAmReadModelFor<DialogAggregate, DialogId, PollVotesReadEvent>
{
    public long MentionCountSourceSequence { get; private set; }
    public long AntiSpamEffectsSequence { get; private set; }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<DialogAggregate, DialogId, AntiSpamMentionDeletionAppliedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        AntiSpamEffectsSequence = Math.Max(AntiSpamEffectsSequence, domainEvent.AggregateSequenceNumber);
        if (domainEvent.AggregateSequenceNumber <= MentionCountSourceSequence) return Task.CompletedTask;
        MentionCountSourceSequence = domainEvent.AggregateSequenceNumber;
        UnreadMentionsCount = domainEvent.AggregateEvent.UnreadMentionsCount;
        return Task.CompletedTask;
    }

    public virtual int ChannelHistoryMinId { get; private set; }
    public virtual DateTime CreationTime { get; private set; }
    public virtual Draft? Draft { get; private set; }
    public virtual string Id { get; private set; } = null!;
    public virtual int MaxSendOutMessageId { get; private set; }
    public virtual PeerNotifySettings? NotifySettings { get; protected set; }
    public virtual long OwnerId { get; private set; }
    //public virtual string TopMessageBoxId { get; private set; }
    public virtual bool Pinned { get; private set; }

    public virtual int PinnedMsgId { get; private set; }
    //public int Date { get; private set; }
    //public string Message { get; private set; }
    //public bool NoWebpage { get; private set; }
    //public int ReplyToMsgId { get; private set; }
    //public byte[] Entities { get; private set; }
    //#endregion
    ///// <summary>
    ///// only for channel
    ///// </summary>
    //public int NewTopMessageId { get; private set; }
    public virtual int PinnedOrder { get; private set; }

    public virtual int Pts { get; private set; }
    public virtual int ReadInboxMaxId { get; private set; }
    //public Draft Draft => new(Message, NoWebpage, ReplyToMsgId, Date, Entities);
    public virtual int ReadOutboxMaxId { get; private set; }

    public virtual long ToPeerId { get; private set; }
    public virtual PeerType ToPeerType { get; private set; }
    public virtual int TopMessage { get; set; }
    public virtual int UnreadCount { get; private set; }
    public virtual long? Version { get; set; }
    public virtual bool IsDeleted { get; private set; }
    public int? TtlPeriod { get; set; }
    public int UnreadMentionsCount { get; private set; }
    public int UnreadReactionsCount { get; private set; }
    public int UnreadPollVotesCount { get; private set; }
    public int? FolderId { get; private set; }
    public bool ViewForumAsMessages { get; private set; }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<DialogAggregate, DialogId, PollVoteCreatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        UnreadPollVotesCount = domainEvent.AggregateEvent.UnreadPollVotesCount;

        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<DialogAggregate, DialogId, PollVotesReadEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        UnreadPollVotesCount = 0;

        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<DialogAggregate, DialogId, ChannelHistoryClearedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        ChannelHistoryMinId = domainEvent.AggregateEvent.HistoryMinId;
        //IsDeleted = true;
        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<DialogAggregate, DialogId, DialogCreatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        Id = domainEvent.AggregateIdentity.Value;
        OwnerId = domainEvent.AggregateEvent.OwnerId;
        ToPeerType = domainEvent.AggregateEvent.ToPeer.PeerType;
        ToPeerId = domainEvent.AggregateEvent.ToPeer.PeerId;
        ChannelHistoryMinId = domainEvent.AggregateEvent.ChannelHistoryMinId;
        CreationTime = domainEvent.AggregateEvent.CreationTime;
        TopMessage = domainEvent.AggregateEvent.TopMessageId;
        //TopMessageBoxId = domainEvent.AggregateEvent.TopMessageBoxId;
        Pts = 0;

        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<DialogAggregate, DialogId, DialogPinChangedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        Pinned = domainEvent.AggregateEvent.Pinned;
        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<DialogAggregate, DialogId, UnreadReactionCreatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        UnreadReactionsCount = domainEvent.AggregateEvent.UnreadReactionsCount;
        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<DialogAggregate, DialogId, UnreadReactionRemovedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        UnreadReactionsCount = domainEvent.AggregateEvent.UnreadReactionsCount;
        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<DialogAggregate, DialogId, UnreadReactionsReadEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        UnreadReactionsCount = domainEvent.AggregateEvent.UnreadReactionsCount;
        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<DialogAggregate, DialogId, MentionCreatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        if (domainEvent.AggregateSequenceNumber <= MentionCountSourceSequence) return Task.CompletedTask;
        MentionCountSourceSequence = domainEvent.AggregateSequenceNumber;
        UnreadMentionsCount = domainEvent.AggregateEvent.UnreadMentionsCount;
        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<DialogAggregate, DialogId, MentionReadEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        if (domainEvent.AggregateSequenceNumber <= MentionCountSourceSequence) return Task.CompletedTask;
        MentionCountSourceSequence = domainEvent.AggregateSequenceNumber;
        UnreadMentionsCount = domainEvent.AggregateEvent.UnreadMentionsCount;
        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<DialogAggregate, DialogId, UnreadMentionsReadEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        if (domainEvent.AggregateSequenceNumber <= MentionCountSourceSequence) return Task.CompletedTask;
        MentionCountSourceSequence = domainEvent.AggregateSequenceNumber;
        UnreadMentionsCount = domainEvent.AggregateEvent.UnreadMentionsCount;
        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<DialogAggregate, DialogId, UnreadMentionsCountSyncedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        if (domainEvent.AggregateSequenceNumber <= MentionCountSourceSequence) return Task.CompletedTask;
        MentionCountSourceSequence = domainEvent.AggregateSequenceNumber;
        UnreadMentionsCount = domainEvent.AggregateEvent.UnreadMentionsCount;
        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<DialogAggregate, DialogId, DraftSavedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        // dialog.draft is the chat level draft only; a topic draft lives in its own DraftReadModel row
        // and is served through forumTopic.draft / monoForumDialog.draft.
        if (!DraftTopicKey.IsChatLevel(DraftTopicKey.Create(domainEvent.AggregateEvent.Draft)))
        {
            return Task.CompletedTask;
        }

        Id = domainEvent.AggregateIdentity.Value;

        // Typing into a chat that has no dialog yet creates this row, so it has to carry the identity
        // too — otherwise it stays owner 0 and messages.getDialogs, which filters by owner, can never
        // return the draft.
        OwnerId = domainEvent.AggregateEvent.OwnerPeerId;
        ToPeerType = domainEvent.AggregateEvent.Peer.PeerType;
        ToPeerId = domainEvent.AggregateEvent.Peer.PeerId;
        if (!Version.HasValue)
        {
            CreationTime = DateTime.UtcNow;
        }

        Draft = domainEvent.AggregateEvent.Draft;
        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<DialogAggregate, DialogId, DraftClearedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        if (DraftTopicKey.OrChatLevel(domainEvent.AggregateEvent.Topics).Any(p => p.IsChatLevel))
        {
            Draft = null;
        }

        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<DialogAggregate, DialogId, HistoryClearedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        ChannelHistoryMinId = domainEvent.AggregateEvent.HistoryMinId;

        //IsDeleted = true;

        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<DialogAggregate, DialogId, InboxMessageReceivedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        Id = domainEvent.AggregateIdentity.Value;
        OwnerId = domainEvent.AggregateEvent.OwnerPeerId;

        TopMessage = domainEvent.AggregateEvent.MessageId;
        //TopMessageBoxId = domainEvent.AggregateEvent.MessageBoxId.Value;
        ToPeerType = domainEvent.AggregateEvent.ToPeer.PeerType;
        ToPeerId = domainEvent.AggregateEvent.ToPeer.PeerId;

        UnreadCount++;
        if (!Version.HasValue)
        {
            CreationTime = DateTime.UtcNow;
        }

        IsDeleted = false;

        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<DialogAggregate, DialogId, OutboxMessageHasReadEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        Id = domainEvent.AggregateIdentity.Value;

        ReadOutboxMaxId = domainEvent.AggregateEvent.MaxMessageId;
        if (TopMessage < ReadOutboxMaxId)
        {
            TopMessage = ReadOutboxMaxId;
        }

        IsDeleted = false;

        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<DialogAggregate, DialogId, ParticipantHistoryClearedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        ChannelHistoryMinId = domainEvent.AggregateEvent.HistoryMinId;
        //IsDeleted = true;
        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<DialogAggregate, DialogId, PinnedOrderChangedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        PinnedOrder = domainEvent.AggregateEvent.Order;
        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<DialogAggregate, DialogId, ReadChannelInboxMessageEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        Id = domainEvent.AggregateIdentity.Value;
        ReadInboxMaxId = domainEvent.AggregateEvent.MaxId;
        if (TopMessage < ReadInboxMaxId)
        {
            TopMessage = ReadInboxMaxId;
            //TopMessageBoxId = domainEvent.AggregateEvent.MessageBoxId;
        }

        var unreadCount = TopMessage - domainEvent.AggregateEvent.MaxId;
        if (unreadCount < 0)
        {
            unreadCount = 0;
        }

        UnreadCount = unreadCount;
        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<DialogAggregate, DialogId, ReadInboxMessage2Event> domainEvent,
        CancellationToken cancellationToken)
    {
        Id = domainEvent.AggregateIdentity.Value;

        ReadInboxMaxId = domainEvent.AggregateEvent.MaxMessageId;
        if (TopMessage < ReadInboxMaxId)
        {
            TopMessage = ReadInboxMaxId;
        }

        UnreadCount = domainEvent.AggregateEvent.UnreadCount;

        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<DialogAggregate, DialogId, SetOutboxTopMessageSuccessEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        Id = domainEvent.AggregateIdentity.Value;
        TopMessage = domainEvent.AggregateEvent.MessageId;
        //OwnerId = domainEvent.AggregateEvent.OwnerPeerId;
        //TopMessage = domainEvent.AggregateEvent.MessageId;
        ////TopMessageBoxId = domainEvent.AggregateEvent.MessageBoxId.Value;
        //ToPeerType = domainEvent.AggregateEvent.ToPeer.PeerType;
        //ToPeerId = domainEvent.AggregateEvent.ToPeer.PeerId;

        ////ReadInboxMaxId = domainEvent.AggregateEvent.MessageId;
        //MaxSendOutMessageId = domainEvent.AggregateEvent.MessageId;

        //Pts=domainEvent.AggregateEvent.
        if (!Version.HasValue)
        {
            CreationTime = DateTime.UtcNow;
        }

        if (domainEvent.AggregateEvent.ClearDraft)
        {
            Draft = null;
        }

        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<MessageAggregate, MessageId, InboxMessagePinnedUpdatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        PinnedMsgId = domainEvent.AggregateEvent.MessageId;
        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<MessageAggregate, MessageId, OutboxMessagePinnedUpdatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        Pinned = domainEvent.AggregateEvent.Pinned;
        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<PeerNotifySettingsAggregate, PeerNotifySettingsId, PeerNotifySettingsUpdatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        Id = DialogId.Create(domainEvent.AggregateEvent.OwnerPeerId,
            domainEvent.AggregateEvent.PeerType,
            domainEvent.AggregateEvent.PeerId).Value;
        NotifySettings = domainEvent.AggregateEvent.PeerNotifySettings;

        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context, IDomainEvent<MessageAggregate, MessageId, OutboxMessageCreatedEvent> domainEvent, CancellationToken cancellationToken)
    {
        Id = DialogId.Create(domainEvent.AggregateEvent.RequestInfo.UserId,
            domainEvent.AggregateEvent.OutboxMessageItem.ToPeer).Value;
        TopMessage = domainEvent.AggregateEvent.OutboxMessageItem.MessageId;

        TopMessage = domainEvent.AggregateEvent.OutboxMessageItem.MessageId;
        OwnerId = domainEvent.AggregateEvent.RequestInfo.UserId;

        ToPeerType = domainEvent.AggregateEvent.OutboxMessageItem.ToPeer.PeerType;
        ToPeerId = domainEvent.AggregateEvent.OutboxMessageItem.ToPeer.PeerId;
        if (!Version.HasValue)
        {
            CreationTime = DateTime.UtcNow;
        }

        ReadInboxMaxId = domainEvent.AggregateEvent.OutboxMessageItem.MessageId;

        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context, IDomainEvent<MessageAggregate, MessageId, InboxMessageCreatedEvent> domainEvent, CancellationToken cancellationToken)
    {
        Id = DialogId.Create(domainEvent.AggregateEvent.InboxMessageItem.OwnerPeer.PeerId,
            domainEvent.AggregateEvent.InboxMessageItem.ToPeer).Value;

        TopMessage = domainEvent.AggregateEvent.InboxMessageItem.MessageId;
        OwnerId = domainEvent.AggregateEvent.InboxMessageItem.OwnerPeer.PeerId;

        ToPeerType = domainEvent.AggregateEvent.InboxMessageItem.ToPeer.PeerType;
        ToPeerId = domainEvent.AggregateEvent.InboxMessageItem.ToPeer.PeerId;
        if (!Version.HasValue)
        {
            CreationTime = DateTime.UtcNow;
        }

        UnreadCount++;

        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context, IDomainEvent<SendMessageSaga, SendMessageSagaId, SendOutboxMessageCompletedSagaEvent> domainEvent, CancellationToken cancellationToken)
    {
        Id = DialogId.Create(domainEvent.AggregateEvent.MessageItem.SenderPeer.PeerId,
            domainEvent.AggregateEvent.MessageItem.ToPeer).Value;

        Pts = domainEvent.AggregateEvent.MessageItem.Pts;
        TopMessage = domainEvent.AggregateEvent.MessageItem.MessageId;

        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context, IDomainEvent<DialogAggregate, DialogId, UpdateReadChannelOutboxEvent> domainEvent, CancellationToken cancellationToken)
    {
        Id = DialogId.Create(domainEvent.AggregateEvent.MessageSenderUserId, PeerType.Channel, domainEvent.AggregateEvent.ChannelId).Value;

        ReadOutboxMaxId = domainEvent.AggregateEvent.MaxId;

        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context, IDomainEvent<DialogAggregate, DialogId, ReadInboxMaxIdUpdatedEvent> domainEvent, CancellationToken cancellationToken)
    {
        ReadInboxMaxId = domainEvent.AggregateEvent.ReadInboxMaxId;
        UnreadCount = domainEvent.AggregateEvent.UnreadCount;

        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context, IDomainEvent<DialogAggregate, DialogId, ReadOutboxMaxIdUpdatedEvent> domainEvent, CancellationToken cancellationToken)
    {
        ReadOutboxMaxId = domainEvent.AggregateEvent.ReadOutboxMaxId;

        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context, IDomainEvent<DialogAggregate, DialogId, TopMessageIdUpdatedEvent> domainEvent, CancellationToken cancellationToken)
    {
        TopMessage = domainEvent.AggregateEvent.NewTopMessageId;

        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context, IDomainEvent<DialogAggregate, DialogId, UpdateReadChannelInboxEvent> domainEvent, CancellationToken cancellationToken)
    {
        ReadInboxMaxId = domainEvent.AggregateEvent.MaxId;

        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context, IDomainEvent<DialogAggregate, DialogId, DialogFolderUpdatedEvent> domainEvent, CancellationToken cancellationToken)
    {
        FolderId = domainEvent.AggregateEvent.FolderId;

        return Task.CompletedTask;
    }
}
