namespace MyTelegram.Domain.Aggregates.Dialog;

public class DialogState : AggregateState<DialogAggregate, DialogId, DialogState>,
    IApply<DialogCreatedEvent>,
    IApply<InboxMessageReceivedEvent>,
    IApply<SetOutboxTopMessageSuccessEvent>,
    IApply<ReadInboxMessage2Event>,
    IApply<OutboxMessageHasReadEvent>,
    IApply<DraftSavedEvent>,
    //IApply<OutboxAlreadyReadEvent>,
    IApply<ReadChannelInboxMessageEvent>,
    IApply<ChannelHistoryClearedEvent>,
    IApply<HistoryClearedEvent>,
    IApply<ParticipantHistoryClearedEvent>,
    IApply<DialogPinChangedEvent>,
    IApply<PinnedOrderChangedEvent>,
    IApply<DialogUnreadMarkChangedEvent>,
    IApply<DraftClearedEvent>,
    //IApply<DeleteUserMessagesStartedEvent>,
    IApply<MentionCreatedEvent>,
    IApply<AntiSpamMentionDeletionAppliedEvent>,
    IApply<AntiSpamMentionAdmittedEvent>,
    IApply<AntiSpamMentionAdmissionReleasedEvent>,
    IApply<MentionRecountRequestedEvent>,
    IApply<MentionRecountCompletedEvent>,
    IApply<MentionReadIntentAdmittedEvent>,
    IApply<MentionReadIntentAppliedEvent>,
    IApply<MentionReadIntentReleasedEvent>,
    IApply<MentionReadEvent>,
    IApply<UnreadMentionsReadEvent>,
    IApply<UnreadMentionsCountSyncedEvent>,
    IApply<UnreadReactionCreatedEvent>,
    IApply<UnreadReactionRemovedEvent>,
    IApply<UnreadReactionsReadEvent>,
    IApply<PollVoteCreatedEvent>,
    IApply<PollVotesReadEvent>,
    IApply<UpdateReadChannelOutboxEvent>,
    IApply<UpdateReadChannelInboxEvent>,
    IApply<ReadInboxMaxIdUpdatedEvent>,
    IApply<ReadOutboxMaxIdUpdatedEvent>,
    IApply<TopMessageIdUpdatedEvent>,
    IApply<DialogUpdatedEvent>,
    IApply<DialogFolderUpdatedEvent>
{
    public int ChannelHistoryMinId { get; private set; }
    public Draft? Draft { get; private set; }

    /// <summary>
    /// The <see cref="DraftTopicKey"/>s of the topics that currently hold a draft. Only their presence
    /// is tracked, not their contents: the state is read to decide whether a clear is worth an event,
    /// and the drafts themselves live in the read models.
    /// </summary>
    public HashSet<string> TopicDraftKeys { get; private set; } = [];
    public int? FolderId { get; private set; }
    public long OwnerId { get; private set; }
    public bool Pinned { get; private set; }
    public int ReadInboxMaxId { get; private set; }
    public int ReadOutboxMaxId { get; private set; }
    public Peer ToPeer { get; private set; } = null!;
    public int TopMessageId { get; private set; }
    public int UnreadCount { get; private set; }
    public bool UnreadMark { get; private set; }
    public int UnreadMentionsCount { get; private set; }
    public int UnreadReactionsCount { get; private set; }

    /// <summary>The messages counted by <see cref="UnreadReactionsCount"/>.</summary>
    public HashSet<int> UnreadReactionMessageIds { get; private set; } = [];
    public int UnreadPollVotesCount { get; private set; }

    public string? MentionReadOperation { get; private set; }

    public void Apply(MentionReadIntentAdmittedEvent aggregateEvent) => MentionReadOperation = aggregateEvent.OperationKey;
    public void Apply(MentionReadIntentAppliedEvent aggregateEvent) { }
    public void Apply(MentionReadIntentReleasedEvent aggregateEvent)
    {
        if (MentionReadOperation == aggregateEvent.OperationKey) MentionReadOperation = null;
    }

    public string? AntiSpamMentionOperation { get; private set; }
    public int AntiSpamMentionMessageId { get; private set; }
    public bool AntiSpamMentionConsumed { get; private set; }
    public bool MentionRecountRequired { get; private set; }

    public void Apply(AntiSpamMentionAdmittedEvent aggregateEvent)
    {
        HasAntiSpamMentionEffects = true;
        AntiSpamMentionOperation = aggregateEvent.OperationKey;
        AntiSpamMentionMessageId = aggregateEvent.MessageId;
        AntiSpamMentionConsumed = aggregateEvent.Consumed;
    }

    public void Apply(AntiSpamMentionAdmissionReleasedEvent aggregateEvent)
    {
        if (AntiSpamMentionOperation == aggregateEvent.OperationKey)
        {
            AntiSpamMentionOperation = null;
            AntiSpamMentionMessageId = 0;
            AntiSpamMentionConsumed = false;
        }
        MentionRecountRequired = true;
    }

    public void Apply(MentionRecountRequestedEvent aggregateEvent) => MentionRecountRequired = true;
    public void Apply(MentionRecountCompletedEvent aggregateEvent) => MentionRecountRequired = false;

    public void Apply(AntiSpamMentionDeletionAppliedEvent aggregateEvent)
    {
        HasAntiSpamMentionEffects = true;
        if (AntiSpamMentionOperation != null && AntiSpamMentionMessageId == aggregateEvent.MessageId)
            AntiSpamMentionConsumed = true;
        UnreadMentionsCount = aggregateEvent.UnreadMentionsCount;
    }

    public bool HasAntiSpamMentionEffects { get; private set; }

    public void Apply(ChannelHistoryClearedEvent aggregateEvent)
    {
        ChannelHistoryMinId = aggregateEvent.HistoryMinId;
    }

    //public void Apply(DeleteUserMessagesStartedEvent aggregateEvent)
    //{
    //}

    public void Apply(DialogCreatedEvent aggregateEvent)
    {
        OwnerId = aggregateEvent.OwnerId;
        ToPeer = aggregateEvent.ToPeer;
        ChannelHistoryMinId = aggregateEvent.ChannelHistoryMinId;
        TopMessageId = aggregateEvent.TopMessageId;
    }

    public void Apply(DialogFolderUpdatedEvent aggregateEvent)
    {
        FolderId = aggregateEvent.FolderId;
    }

    public void Apply(DialogPinChangedEvent aggregateEvent)
    {
        Pinned = aggregateEvent.Pinned;
    }

    public void Apply(DialogUnreadMarkChangedEvent aggregateEvent)
    {
        UnreadMark = aggregateEvent.UnreadMark;
    }

    public void Apply(DialogUpdatedEvent aggregateEvent)
    {
        TopMessageId = aggregateEvent.TopMessageId;
        OwnerId = aggregateEvent.OwnerUserId;
        ToPeer = aggregateEvent.ToPeer;
    }

    /// <summary>Whether the chat, or one of its topics, currently holds a draft.</summary>
    public bool HasDraft(string topicKey)
    {
        return DraftTopicKey.IsChatLevel(topicKey)
            ? Draft != null
            : TopicDraftKeys.Contains(topicKey);
    }

    public void Apply(DraftClearedEvent aggregateEvent)
    {
        foreach (var topic in DraftTopicKey.OrChatLevel(aggregateEvent.Topics))
        {
            if (topic.IsChatLevel)
            {
                Draft = null;
            }
            else
            {
                TopicDraftKeys.Remove(topic.Key);
            }
        }
    }

    public void Apply(DraftSavedEvent aggregateEvent)
    {
        var topicKey = DraftTopicKey.Create(aggregateEvent.Draft);
        if (DraftTopicKey.IsChatLevel(topicKey))
        {
            Draft = aggregateEvent.Draft;
        }
        else
        {
            TopicDraftKeys.Add(topicKey);
        }
    }

    public void Apply(HistoryClearedEvent aggregateEvent)
    {
        ChannelHistoryMinId = aggregateEvent.HistoryMinId;
    }

    public void Apply(InboxMessageReceivedEvent aggregateEvent)
    {
        OwnerId = aggregateEvent.OwnerPeerId;
        UnreadCount++;
        TopMessageId = aggregateEvent.MessageId;
        ToPeer = aggregateEvent.ToPeer;
    }

    public void Apply(MentionCreatedEvent aggregateEvent)
    {
        UnreadMentionsCount = aggregateEvent.UnreadMentionsCount;
    }

    public void Apply(MentionReadEvent aggregateEvent)
    {
        if (AntiSpamMentionOperation != null && AntiSpamMentionMessageId == aggregateEvent.MessageId)
            AntiSpamMentionConsumed = true;
        UnreadMentionsCount = aggregateEvent.UnreadMentionsCount;
    }

    public void Apply(UnreadMentionsReadEvent aggregateEvent)
    {
        if (AntiSpamMentionOperation != null) AntiSpamMentionConsumed = true;
        UnreadMentionsCount = aggregateEvent.UnreadMentionsCount;
    }

    public void Apply(UnreadMentionsCountSyncedEvent aggregateEvent)
    {
        UnreadMentionsCount = aggregateEvent.UnreadMentionsCount;
    }

    public void Apply(UnreadReactionCreatedEvent aggregateEvent)
    {
        UnreadReactionsCount = aggregateEvent.UnreadReactionsCount;
        UnreadReactionMessageIds.Add(aggregateEvent.MessageId);
    }

    public void Apply(UnreadReactionRemovedEvent aggregateEvent)
    {
        UnreadReactionsCount = aggregateEvent.UnreadReactionsCount;
        UnreadReactionMessageIds.Remove(aggregateEvent.MessageId);
    }

    public void Apply(UnreadReactionsReadEvent aggregateEvent)
    {
        UnreadReactionsCount = aggregateEvent.UnreadReactionsCount;
        UnreadReactionMessageIds.Clear();
    }

    public void Apply(PollVoteCreatedEvent aggregateEvent)
    {
        UnreadPollVotesCount = aggregateEvent.UnreadPollVotesCount;
    }

    public void Apply(PollVotesReadEvent aggregateEvent)
    {
        UnreadPollVotesCount = 0;
    }

    //public void Apply(OutboxAlreadyReadEvent aggregateEvent)
    //{
    //}

    public void Apply(OutboxMessageHasReadEvent aggregateEvent)
    {
        ReadOutboxMaxId = aggregateEvent.MaxMessageId;
        ToPeer = aggregateEvent.ToPeer;
    }

    public void Apply(ParticipantHistoryClearedEvent aggregateEvent)
    {
        ChannelHistoryMinId = aggregateEvent.HistoryMinId;
    }

    public void Apply(PinnedOrderChangedEvent aggregateEvent)
    {
    }

    public void Apply(ReadChannelInboxMessageEvent aggregateEvent)
    {
        if (TopMessageId < aggregateEvent.MaxId)
        {
            TopMessageId = aggregateEvent.MaxId;
        }
    }

    public void Apply(ReadInboxMaxIdUpdatedEvent aggregateEvent)
    {
        ReadInboxMaxId = aggregateEvent.ReadInboxMaxId;
    }

    public void Apply(ReadInboxMessage2Event aggregateEvent)
    {
        ToPeer = aggregateEvent.ToPeer;
        ReadInboxMaxId = aggregateEvent.MaxMessageId;
        UnreadCount = aggregateEvent.UnreadCount;
        if (TopMessageId < aggregateEvent.MaxMessageId)
        {
            TopMessageId = aggregateEvent.MaxMessageId;
        }
    }

    public void Apply(ReadOutboxMaxIdUpdatedEvent aggregateEvent)
    {
        ReadOutboxMaxId = aggregateEvent.ReadOutboxMaxId;
    }

    public void Apply(SetOutboxTopMessageSuccessEvent aggregateEvent)
    {
        OwnerId = aggregateEvent.OwnerPeerId;
        TopMessageId = aggregateEvent.MessageId;
        ToPeer = aggregateEvent.ToPeer;
        if (aggregateEvent.ClearDraft)
        {
            Draft = null;
        }
    }

    public void Apply(TopMessageIdUpdatedEvent aggregateEvent)
    {
        TopMessageId = aggregateEvent.NewTopMessageId;
    }

    public void Apply(UpdateReadChannelInboxEvent aggregateEvent)
    {
        ReadInboxMaxId = aggregateEvent.MaxId;
    }

    public void Apply(UpdateReadChannelOutboxEvent aggregateEvent)
    {
        ReadOutboxMaxId = aggregateEvent.MaxId;
    }

    public void LoadSnapshot(DialogSnapshot snapshot)
    {
        MentionReadOperation = snapshot.MentionReadOperation;
        AntiSpamMentionOperation = snapshot.AntiSpamMentionOperation;
        AntiSpamMentionMessageId = snapshot.AntiSpamMentionMessageId;
        AntiSpamMentionConsumed = snapshot.AntiSpamMentionConsumed;
        MentionRecountRequired = snapshot.MentionRecountRequired;
        HasAntiSpamMentionEffects = snapshot.HasAntiSpamMentionEffects;
        OwnerId = snapshot.OwnerId;
        TopMessageId = snapshot.TopMessage;
        ReadInboxMaxId = snapshot.ReadInboxMaxId;
        ReadOutboxMaxId = snapshot.ReadOutboxMaxId;
        UnreadCount = snapshot.UnreadCount;
        ToPeer = snapshot.ToPeer;
        UnreadMark = snapshot.UnreadMark;
        Pinned = snapshot.Pinned;
        ChannelHistoryMinId = snapshot.ChannelHistoryMinId;
        Draft = snapshot.Draft;
        TopicDraftKeys = snapshot.TopicDraftKeys == null ? [] : [.. snapshot.TopicDraftKeys];
        UnreadMentionsCount = snapshot.UnreadMentionsCount;
        UnreadReactionsCount = snapshot.UnreadReactionsCount;
        UnreadReactionMessageIds = snapshot.UnreadReactionMessageIds == null ? [] : [.. snapshot.UnreadReactionMessageIds];
        UnreadPollVotesCount = snapshot.UnreadPollVotesCount;
        FolderId = snapshot.FolderId;
    }
}