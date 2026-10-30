namespace MyTelegram.Domain.Aggregates.Dialog;

[EnableAutoGeneration]
public partial class DialogAggregate : MyInMemorySnapshotAggregateRoot<DialogAggregate, DialogId, DialogSnapshot>
{
    private readonly DialogState _state = new();

    public DialogAggregate(DialogId id) : base(id, SnapshotEveryFewVersionsStrategy.Default)
    {
        Register(_state);
    }

    [DoNotInheritRequestCommand]
    public void UpdateDialogFolder(RequestInfo requestInfo, int? folderId)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        var ownerUserId = _state.OwnerId;
        Emit(new DialogFolderUpdatedEvent(requestInfo, ownerUserId, _state.ToPeer, folderId));
    }

    [DoNotInheritRequestCommand]
    public void UpdateDialog(RequestInfo requestInfo, long ownerUserId, Peer toPeer, int topMessageId, int pts, int? defaultHistoryTtl, bool isMonoForum)
    {
        Emit(new DialogUpdatedEvent(requestInfo, ownerUserId, toPeer, topMessageId, pts, IsNew, defaultHistoryTtl, isMonoForum));
    }
	
    public void ClearChannelHistory(RequestInfo requestInfo, int availableMinId)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        var channelId = _state.ToPeer.PeerId;
        var historyMinId = availableMinId;
        Emit(new ChannelHistoryClearedEvent(requestInfo, channelId, historyMinId));
    }

    /// <summary>
    /// Drops the drafts of the given topics, or of the chat itself for <see cref="DraftTopic.ChatLevel"/>.
    /// Called from <c>messages.saveDraft</c> with empty values, from a send with <c>clear_draft</c> and
    /// from <c>messages.clearAllDrafts</c>.
    /// </summary>
    /// <remarks>
    /// Several topics at once rather than one command each, because a request command is deduplicated by
    /// the request's <c>msg_id</c> alone (<c>DistinctCommand</c> hashes nothing else), so a second
    /// command for the same dialog within one request would be skipped and that draft would survive
    /// <c>clearAllDrafts</c>.
    ///
    /// <para>Only the topics that hold a draft are emitted: emitting unconditionally would push an
    /// <c>updateDraftMessage</c> with <c>draftMessageEmpty</c> to every other session on every message
    /// sent, since clients set <c>clear_draft</c> on practically every send.</para>
    /// </remarks>
    public void ClearDrafts(RequestInfo requestInfo,
        long ownerPeerId,
        Peer peer,
        List<DraftTopic> requestedTopics)
    {
        var topics = requestedTopics.Where(p => _state.HasDraft(p.Key)).ToList();
        if (topics.Count == 0)
        {
            return;
        }

        Emit(new DraftClearedEvent(requestInfo, ownerPeerId, peer, topics));
    }

    public void ClearHistory(RequestInfo requestInfo,
        bool revoke,
        string messageActionData,
        long randomId,
        List<int> messageIdListToBeDelete,
        int historyMinId)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        Emit(new HistoryClearedEvent(requestInfo,
            _state.OwnerId,
            _state.TopMessageId,
            revoke,
            _state.ToPeer,
            messageActionData,
            randomId,
            messageIdListToBeDelete,
            historyMinId
        ));
    }

    public void ClearParticipantHistory(RequestInfo requestInfo)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        var historyMinId = _state.TopMessageId;
        Emit(new ParticipantHistoryClearedEvent(requestInfo, _state.OwnerId, historyMinId));
    }

    public void CreateDialog(
        RequestInfo requestInfo,
        long ownerId,
        Peer toPeer,
        int channelHistoryMinId,
        int topMessageId)
    {
        //Specs.AggregateIsNew.ThrowDomainErrorIfNotSatisfied(this);
        var creationTime = DateTime.UtcNow;
        Emit(new DialogCreatedEvent(ownerId,
            toPeer,
            channelHistoryMinId,
            topMessageId,
            creationTime
        ));
    }

    /// <summary>
    /// Increments the unread mention counter shown as an @ badge in the dialog list.
    /// See https://corefork.telegram.org/api/mentions
    /// </summary>
    public void CreateMention(int messageId)
    {
        // The command is published from the send-message saga, which cannot know whether the
        // mentioned user already has a dialog with the peer. Emitting on a fresh aggregate would
        // store an event with a null ToPeer, so skip instead: the dialog gets its counter on the
        // next mention, once joining or receiving a message has created it.
        if (IsNew)
        {
            return;
        }

        var unreadMentionsCount = _state.UnreadMentionsCount + 1;
        var ownerUserId = _state.OwnerId;
        Emit(new MentionCreatedEvent(ownerUserId, _state.ToPeer, messageId, unreadMentionsCount));
    }

    /// <summary>
    /// Bumps <c>unread_poll_votes_count</c> after someone votes on a poll the dialog owner
    /// created. Only fired for non-anonymous polls.
    /// </summary>
    public void CreatePollVote(int messageId)
    {
        var unreadPollVotesCount = _state.UnreadPollVotesCount + 1;
        var ownerUserId = _state.OwnerId;
        Emit(new PollVoteCreatedEvent(ownerUserId, _state.ToPeer, messageId, unreadPollVotesCount));
    }

    /// <summary>
    /// Clears <c>unread_poll_votes_count</c> when the owner reads the votes.
    /// </summary>
    public void ReadPollVotes()
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        var ownerUserId = _state.OwnerId;
        Emit(new PollVotesReadEvent(ownerUserId, _state.ToPeer));
    }

    public void MarkDialogAsUnread(bool unread)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        var unreadMark = unread;
        Emit(new DialogUnreadMarkChangedEvent(unreadMark));
    }

    /// <summary>
    /// Increments the unread reaction counter shown as a badge in the dialog list.
    /// See https://corefork.telegram.org/api/reactions#notifications-about-reactions
    /// </summary>
    public void CreateUnreadReaction(int messageId)
    {
        var unreadReactionsCount = _state.UnreadReactionsCount + 1;
        var ownerUserId = _state.OwnerId;
        Emit(new UnreadReactionCreatedEvent(ownerUserId, _state.ToPeer, messageId, unreadReactionsCount));
    }

    /// <summary>
    /// Clears the unread reaction counter, called from messages.readReactions.
    /// </summary>
    public void ReadUnreadReactions()
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        var ownerUserId = _state.OwnerId;
        var unreadReactionsCount = 0;
        Emit(new UnreadReactionsReadEvent(ownerUserId, _state.ToPeer, unreadReactionsCount));
    }

    public void OutboxMessageHasRead(RequestInfo requestInfo,
        int maxMessageId,
        long ownerPeerId,
        Peer toPeer
    )
    {
        //Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        var hasRead = maxMessageId > _state.ReadOutboxMaxId;
        if (_state.ReadOutboxMaxId > maxMessageId)
        {
            maxMessageId = _state.ReadOutboxMaxId;
        }
        Emit(new OutboxMessageHasReadEvent(requestInfo,
            maxMessageId,
            ownerPeerId,
            toPeer,
            hasRead
            ));
    }

    public void ReadChannelInboxMessage(RequestInfo requestInfo,
        long readerUserId,
        long channelId,
        int maxId,
        long senderUserId,
        int? topMsgId)
    {
        // Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);

        // When user reading channel messages and does not join the channel,
        // the dialog has not been created,no verification required
        Emit(new ReadChannelInboxMessageEvent(requestInfo,
            readerUserId,
            channelId,
            maxId,
            senderUserId,
            topMsgId));
    }

    [DoNotInheritRequestCommand]
    public void ReadInboxMessage2(RequestInfo requestInfo,
        long readerUserId,
        long ownerPeerId,
        int maxMessageId,
        int unreadCount,
        Peer toPeer,
        int date
    )
    {
        //Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        //var unreadCount = _state.TopMessage - maxId;
        //if (unreadCount < 0)
        //{
        //    unreadCount = 0;
        //}

        var readCount = _state.UnreadCount - unreadCount;
        if (readCount < 0)
        {
            readCount = 0;
        }

        Emit(new ReadInboxMessage2Event(requestInfo,
            readerUserId,
            ownerPeerId,
            maxMessageId,
            readCount,
            unreadCount,
            toPeer, date));
    }

    /// <summary>
    /// Decrements the unread mention counter for a single message, called from
    /// messages.readMessageContents / channels.readMessageContents.
    /// </summary>
    public bool HasAntiSpamMentionEffects => _state.HasAntiSpamMentionEffects;

    internal void ApplyAntiSpamMentionDeletion(string operationKey, int messageId, bool alreadyRead)
    {
        alreadyRead |= _state.AntiSpamMentionOperation != null &&
                       _state.AntiSpamMentionMessageId == messageId && _state.AntiSpamMentionConsumed;
        Emit(new AntiSpamMentionDeletionAppliedEvent(operationKey, messageId,
            alreadyRead ? _state.UnreadMentionsCount : Math.Max(0, _state.UnreadMentionsCount - 1)));
    }

    [DisableAutoGeneration]
    public void ReadMention(int messageId)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        if (_state.AntiSpamMentionOperation != null && _state.AntiSpamMentionMessageId == messageId &&
            _state.AntiSpamMentionConsumed) return;
        var unreadMentionsCount = _state.UnreadMentionsCount - 1;
        if (unreadMentionsCount < 0)
        {
            unreadMentionsCount = 0;
        }
        var ownerUserId = _state.OwnerId;
        Emit(new MentionReadEvent(ownerUserId, _state.ToPeer, messageId, unreadMentionsCount));
    }

    /// <summary>
    /// Puts the unread mention counter back in step with the messages that actually still carry an
    /// unread mention. Deleting a history or clearing a chat removes mentions without going through
    /// ReadMention, so the counter is re-synced whenever the exact number is computed anyway.
    /// </summary>
    [DisableAutoGeneration]
    public void SyncUnreadMentionsCount(int unreadMentionsCount, int? expectedVersion = null)
    {
        if (IsNew || unreadMentionsCount < 0) return;
        if (_state.AntiSpamMentionOperation != null || _state.MentionReadOperation != null ||
            expectedVersion.HasValue && Version != expectedVersion.Value)
        {
            if (!_state.MentionRecountRequired) Emit(new MentionRecountRequestedEvent());
            return;
        }
        if (unreadMentionsCount != _state.UnreadMentionsCount)
            Emit(new UnreadMentionsCountSyncedEvent(_state.OwnerId, _state.ToPeer, unreadMentionsCount));
        if (_state.MentionRecountRequired) Emit(new MentionRecountCompletedEvent());
    }

    /// <summary>
    /// Clears the unread mention counter, called from messages.readMentions.
    /// </summary>
    public void ReadUnreadMentions()
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        var ownerUserId = _state.OwnerId;
        var unreadMentionsCount = 0;
        Emit(new UnreadMentionsReadEvent(ownerUserId, _state.ToPeer, unreadMentionsCount));
    }

    [DoNotInheritRequestCommand]
    public void ReceiveInboxMessage(
        RequestInfo requestInfo,
        int messageId,
        long ownerPeerId,
        Peer toPeer)
    {
        // IsNew tells subscribers this inbox message created the dialog, which is what
        // globalPrivacySettings.archive_and_mute_new_noncontact_peers keys off: only the
        // first message from a stranger triggers auto-archiving, later ones must not
        // re-archive a chat the user pulled out of the archive.
        var isNewDialog = IsNew;
        Emit(new InboxMessageReceivedEvent(
            requestInfo,
            messageId,
            ownerPeerId,
            toPeer,
            isNewDialog
        ));
    }

    /// <summary>
    /// Stores a draft. See https://corefork.telegram.org/api/drafts
    /// </summary>
    /// <remarks>
    /// The owner and the peer come from the command, not from the state: typing into a chat you have
    /// never messaged is a draft on a dialog that does not exist yet, and reading them from an empty
    /// state stored the draft under owner 0 with no peer — invisible to <c>messages.getAllDrafts</c>,
    /// which filters by owner.
    /// </remarks>
    public void SaveDraft(RequestInfo requestInfo,
        long ownerPeerId,
        Peer peer,
        Draft draft
    )
    {
        Emit(new DraftSavedEvent(requestInfo,
            ownerPeerId,
            peer,
            draft));
    }

    [DoNotInheritRequestCommand]
    public void SetOutboxTopMessage(
        //RequestInfo requestInfo,
        int messageId,
        long ownerPeerId,
        //int pts, 
        Peer toPeer,
        bool clearDraft)
    {
        Emit(new SetOutboxTopMessageSuccessEvent(
            //requestInfo,
            messageId,
            ownerPeerId,
            toPeer,
            clearDraft));
    }

    public void SetPinnedMsgId(int pinnedMsgId)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        Emit(new DialogMsgIdPinnedEvent(pinnedMsgId));
    }

    public void SetPinnedOrder(int order)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        Emit(new PinnedOrderChangedEvent(order));
    }

    public void ToggleDialogPinned(RequestInfo requestInfo,
        bool pinned)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        var ownerPeerId = _state.OwnerId;
        Emit(new DialogPinChangedEvent(requestInfo, ownerPeerId, pinned));
    }

    [DoNotInheritRequestCommand]
    public void UpdateReadChannelInbox(RequestInfo requestInfo, long messageSenderUserId, int maxId)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        var channelId = _state.ToPeer.PeerId;
        Emit(new UpdateReadChannelInboxEvent(requestInfo, messageSenderUserId, channelId, maxId));
    }

    [DoNotInheritRequestCommand]
    public void UpdateReadChannelOutbox(RequestInfo requestInfo, int maxId)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        var messageSenderUserId = _state.OwnerId;
        var channelId = _state.ToPeer.PeerId;
        Emit(new UpdateReadChannelOutboxEvent(requestInfo, messageSenderUserId, channelId, maxId));
    }

    public void UpdateReadInboxMaxId(RequestInfo requestInfo, int maxId, long senderUserId, int senderMessageId, int unreadCount)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        var readInboxMaxId = maxId;
        Emit(new ReadInboxMaxIdUpdatedEvent(requestInfo, _state.OwnerId, _state.ToPeer.PeerId, readInboxMaxId, senderUserId,
            senderMessageId,
            unreadCount
            ));
    }

    public void UpdateReadOutboxMaxId(RequestInfo requestInfo, int readOutboxMaxId)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        var ownerUserId = _state.OwnerId;
        Emit(new ReadOutboxMaxIdUpdatedEvent(requestInfo, ownerUserId, _state.ToPeer.PeerId, readOutboxMaxId));
    }

    public void UpdateDialogTopMessageId(int newTopMessageId)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        Emit(new TopMessageIdUpdatedEvent(_state.OwnerId, _state.ToPeer, newTopMessageId));
    }
    protected override Task<DialogSnapshot> CreateSnapshotAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(new DialogSnapshot(
            _state.OwnerId,
            _state.TopMessageId,
            _state.ReadInboxMaxId,
            _state.ReadOutboxMaxId,
            _state.UnreadCount,
            _state.ToPeer,
            _state.UnreadMark,
            _state.Pinned,
            _state.ChannelHistoryMinId,
            _state.Draft,
            _state.UnreadMentionsCount,
            _state.FolderId,
            _state.UnreadReactionsCount,
            _state.UnreadPollVotesCount,
            [.. _state.TopicDraftKeys],
            _state.HasAntiSpamMentionEffects,
            _state.AntiSpamMentionOperation,
            _state.AntiSpamMentionMessageId,
            _state.AntiSpamMentionConsumed,
            _state.MentionRecountRequired,
            _state.MentionReadOperation
        ));

    }

    protected override Task LoadSnapshotAsync(DialogSnapshot snapshot,
        ISnapshotMetadata metadata,
        CancellationToken cancellationToken)
    {
        _state.LoadSnapshot(snapshot);
        return Task.CompletedTask;
    }
}