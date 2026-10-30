namespace MyTelegram.Domain.Aggregates.Messaging;

[EnableAutoGeneration]
public class MessageAggregate : SnapshotAggregateRoot<MessageAggregate, MessageId, MessageSnapshot>
{
    private readonly MessageState _state = new();
    private int _channelDeletionProofVersion = 1;
    public bool HasChannelDeletionProof => _channelDeletionProofVersion == 1;

    public bool IsChannelDeleted => _state.IsChannelDeleted;
    public Guid? ChannelDeletionRequestId => _state.ChannelDeletionRequestId;
    public Guid? AntiSpamDeletionCancelledRequestId => _state.AntiSpamDeletionCancelledRequestId;

    public MessageAggregate(MessageId id) : base(id, SnapshotEveryFewVersionsStrategy.Default)
    {
        Register(_state);
    }

    [DoNotInheritRequestCommand]
    public void UpdateMessagePinned(RequestInfo requestInfo, bool pinned)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        var ownerPeerId = _state.MessageItem.OwnerPeer.PeerId;
        Emit(new MessagePinnedUpdatedEvent(requestInfo, ownerPeerId, _state.MessageItem.MessageId, pinned, _state.MessageItem.ToPeer, _state.MessageItem.Post));
    }

    [DoNotInheritRequestCommand]
    public void UnpinMessage(RequestInfo requestInfo)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        var ownerPeerId = _state.MessageItem.OwnerPeer.PeerId;
        Emit(new MessageUnpinnedEvent(requestInfo, ownerPeerId, _state.MessageItem.MessageId));
    }

    [DoNotInheritRequestCommand]
    public void UpdateMessageReactions(RequestInfo requestInfo, long senderUserId, List<Reaction> senderReactions)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        // The previous set is carried in the event so subscribers can report old_reactions to bots.
        var oldReactions = _state.RecentReactions.ToList();

        // Merge against authoritative state instead of trusting a full list precomputed by the caller
        // from the (eventually consistent) read model. Two users reacting to the same message would
        // otherwise each rebuild the list without the other's reaction and the second write would clobber
        // the first. Keep everyone else's reactions untouched and replace only this sender's set; on an
        // optimistic-concurrency retry EventFlow reloads state and re-runs this, so the merge always sees
        // the latest reactions.
        var reactions = _state.RecentReactions.Where(r => r.UserId != senderUserId).ToList();
        reactions.AddRange(senderReactions);

        Emit(new MessageReactionsUpdatedEvent(requestInfo, _state.MessageItem, reactions, oldReactions));
    }

    /// <summary>
    /// Update the <a href="https://corefork.telegram.org/api/todo">todo list »</a> attached to this
    /// message (append items / toggle completions).
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT go through <see cref="EditOutboxMessage"/>: that path requires
    /// <c>IsOut</c> and enforces <see cref="MyTelegramConsts.EditTimeLimit"/>, which would make it
    /// impossible for the recipient of a checklist to ever tick an item (and would kill the list
    /// after 48 hours). Collaborative editing is authorized by the <c>others_can_append</c> /
    /// <c>others_can_complete</c> flags, checked by the request handlers — same reasoning as
    /// <see cref="UpdateMessageReactions"/>.
    /// </remarks>
    [DoNotInheritRequestCommand]
    public void UpdateTodoList(RequestInfo requestInfo, ITodoList todo, List<TodoCompletionItem> completions)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        Emit(new MessageTodoUpdatedEvent(requestInfo, _state.MessageItem, todo, completions));
    }

    /// <summary>
    /// Records the receipt message on an invoice once it has been paid.
    /// </summary>
    /// <remarks>
    /// Deliberately not routed through <see cref="EditOutboxMessage"/>: this is not the author editing
    /// their message but the server recording that the invoice was settled. Going through the edit
    /// path would bump <c>edit_date</c> — every client would then label the bot's invoice "edited" —
    /// and would refuse outright once <see cref="MyTelegramConsts.EditTimeLimit"/> has passed, which
    /// an invoice paid a day later routinely does. Same reasoning as <see cref="UpdateTodoList"/>.
    /// See https://corefork.telegram.org/api/payments#5-checkout
    /// </remarks>
    [DoNotInheritRequestCommand]
    public void UpdateInvoiceReceipt(RequestInfo requestInfo, int receiptMsgId)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        Emit(new MessageInvoiceReceiptUpdatedEvent(requestInfo, _state.MessageItem, receiptMsgId));
    }

    public void AddInboxItemsToOutboxMessage(List<InboxItem> inboxItems)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        Emit(new InboxItemsAddedToOutboxMessageEvent(inboxItems));
    }

    /// <summary>
    ///     Sender's message id and receiver's message id are independent,add receiver's message id to sender,delete messages
    ///     and pin messages need this
    /// </summary>
    /// <param name="inboxOwnerPeerId"></param>
    /// <param name="inboxMessageId"></param>
    public void AddInboxMessageIdToOutboxMessage(long inboxOwnerPeerId,
        int inboxMessageId)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        var inboxItem = new InboxItem(inboxOwnerPeerId, inboxMessageId);
        Emit(new InboxMessageIdAddedToOutboxMessageEvent(inboxItem));
    }

    public void CreateInboxMessage(
        RequestInfo requestInfo,
        MessageItem inboxMessageItem,
        int senderMessageId)
    {
        Specs.AggregateIsNew.ThrowDomainErrorIfNotSatisfied(this);
        if (inboxMessageItem.EncryptedData != null)
        {
            inboxMessageItem = inboxMessageItem with { Message = string.Empty };
        }
        Emit(new InboxMessageCreatedEvent(requestInfo, inboxMessageItem, senderMessageId));
    }

    public void CreateOutboxMessage(RequestInfo requestInfo,
        MessageItem outboxMessageItem,
        List<long>? mentionedUserIds = null,
        List<ReplyToMsgItem>? replyToMsgItems = null,
        //IReplyTo? replyTo,
        //IInputReplyTo? inputReplyTo, 
        bool clearDraft = true,
        int groupItemCount = 1,
        long? linkedChannelId = null,
        List<long>? chatMembers = null)
    {
        Specs.AggregateIsNew.ThrowDomainErrorIfNotSatisfied(this);
        if (outboxMessageItem.Post)
        {
            var reply = new MessageReply(linkedChannelId, 0, 0, null, null);
            outboxMessageItem = outboxMessageItem with { Views = 1, Reply = reply };
        }
        if (!outboxMessageItem.BatchId.HasValue)
        {
            outboxMessageItem = outboxMessageItem with { BatchId = SequentialGuid.Create() };
        }

        if (outboxMessageItem.EncryptedData != null)
        {
            outboxMessageItem = outboxMessageItem with { Message = string.Empty };
        }

        Emit(new OutboxMessageCreatedEvent(requestInfo,
            outboxMessageItem,
            mentionedUserIds,
            replyToMsgItems,
            clearDraft,
            groupItemCount,
            linkedChannelId,
            chatMembers
        ));
    }

    /// <summary>
    /// Permanently fences native moderation on this message, serialized with deletion by aggregate OCC.
    /// Keep the first request identity even if a terminal job expires and is later recreated.
    /// </summary>
    [DisableAutoGeneration]
    public void CancelAntiSpamDeletion(Guid requestId)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        if (_state.IsDeleted || _state.AntiSpamDeletionCancelledRequestId.HasValue)
        {
            return;
        }

        Emit(new AntiSpamDeletionCancelledEvent(requestId));
    }

    [DoNotInheritRequestCommand]
    public void DeleteChannelMessage(RequestInfo requestInfo, int? expectedAggregateVersion = null)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        if (_state.IsChannelDeleted ||
            (requestInfo.UserId == MyTelegramConsts.AntiSpamBotUserId &&
             (_state.AntiSpamDeletionCancelledRequestId.HasValue || !HasChannelDeletionProof ||
              !expectedAggregateVersion.HasValue || Version != expectedAggregateVersion.Value)))
        {
            AcknowledgeChannelDeletion(requestInfo);
            return;
        }
        var channelId = _state.MessageItem.OwnerPeer.PeerId;
        var postChannelId = _state.MessageItem.FwdHeader?.SavedFromPeer?.PeerId;
        var postMessageId = _state.MessageItem.FwdHeader?.SavedFromMsgId;

        // Root of the thread this message belongs to, so the reply counter of that root can be
        // decremented once the message is gone. See https://corefork.telegram.org/api/threads
        var threadRootMessageId = MessageThreadHelper.GetThreadRootMessageId(_state.MessageItem);
        Emit(new ChannelMessageDeletedEvent(requestInfo,
            channelId,
            _state.MessageItem.MessageId,
            _state.MessageItem.IsForwardFromChannelPost,
            postChannelId,
            postMessageId,
            threadRootMessageId));
    }

    [DisableAutoGeneration]
    private void AcknowledgeChannelDeletion(RequestInfo requestInfo)
    {
        Emit(new ChannelMessageDeletionAcknowledgedEvent(requestInfo,
            _state.MessageItem.OwnerPeer.PeerId, _state.MessageItem.MessageId,
            _state.ChannelDeletionRequestId));
    }

    [DoNotInheritRequestCommand]
    public void DeleteInboxMessage(RequestInfo requestInfo)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        Emit(new InboxMessageDeletedEvent(
            requestInfo,
            _state.MessageItem.OwnerPeer.PeerId,
            _state.MessageItem.MessageId,
            _state.SenderMessageId
        ));
    }

    public void DeleteMessage(RequestInfo requestInfo)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        var ownerPeerId = _state.MessageItem.OwnerPeer.PeerId;
        var senderPeerId = _state.MessageItem.SenderPeer.PeerId;
        Emit(new MessageDeleted4Event(
            requestInfo,
            _state.MessageItem.ToPeer,
            ownerPeerId,
            _state.MessageItem.MessageId,
            _state.MessageItem.IsOut,
            senderPeerId,
            _state.SenderMessageId));
    }

    public void DeleteOtherPartyMessage(RequestInfo requestInfo)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        var ownerPeerId = _state.MessageItem.OwnerPeer.PeerId;
        Emit(new OtherPartyMessageDeletedEvent(
            requestInfo,
            ownerPeerId,
            _state.MessageItem.MessageId));
    }

    public void EditInboxMessage(
        RequestInfo requestInfo,
        int messageId,
        string newMessage,
        int editDate,
        TVector<IMessageEntity>? entities,
        IMessageMedia? media,
        IReplyMarkup? replyMarkup,
        bool invertMedia,
        List<string>? hashtags,
        ReadOnlyMemory<byte>? encryptedData = null)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);

        if (string.IsNullOrEmpty(newMessage))
        {
            newMessage = _state.MessageItem.Message;
        }

        var newEncryptedData = _state.MessageItem.EncryptedData;
        if (encryptedData != null)
        {
            newEncryptedData = encryptedData;
            newMessage = string.Empty;
        }

        var oldMessageItem = _state.MessageItem;
        var editHide = oldMessageItem.SenderUserId == MyTelegramConsts.BotFatherUserId && (replyMarkup != null || oldMessageItem.ReplyMarkup != null);
        media ??= oldMessageItem.Media;

        var newMessageItem = oldMessageItem with
        {
            Message = newMessage,
            Entities = entities,
            Media = media,
            ReplyMarkup = replyMarkup,
            EditDate = editDate,
            EditHide = editHide,
            InvertMedia = invertMedia,
            Hashtags = hashtags,
            EncryptedData = newEncryptedData
        };

        Emit(new InboxMessageEditedEventV2(
            requestInfo,
            oldMessageItem,
            newMessageItem));
    }

    public void EditOutboxMessage(RequestInfo requestInfo,
        int messageId,
        string newMessage,
        int editDate,
        TVector<IMessageEntity>? entities,
        IMessageMedia? media,
        IReplyMarkup? replyMarkup,
        bool invertMedia,
        List<string>? hashtags,
        ReadOnlyMemory<byte>? encryptedData = null,
        ReadOnlyMemory<byte>? inboxMessageEncryptedData = null
        )
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        if (_state.MessageItem.Date + MyTelegramConsts.EditTimeLimit < DateTime.UtcNow.ToTimestamp())
        {
            RpcErrors.RpcErrors400.MessageEditTimeExpired.ThrowRpcError();
        }

        if (!_state.MessageItem.IsOut)
        {
            RpcErrors.RpcErrors403.MessageAuthorRequired.ThrowRpcError();
        }

        if (string.IsNullOrEmpty(newMessage))
        {
            newMessage = _state.MessageItem.Message;
        }
        var newEncryptedData = _state.MessageItem.EncryptedData;
        var newInboxMessageEncryptedData = _state.MessageItem.InboxMessageEncryptedData;
        if (encryptedData != null)
        {
            newEncryptedData = encryptedData;
            newMessage = string.Empty;
        }

        if (inboxMessageEncryptedData != null)
        {
            newInboxMessageEncryptedData = inboxMessageEncryptedData;
        }

        var oldMessageItem = _state.MessageItem;
        media ??= oldMessageItem.Media;

        var newMessageItem = _state.MessageItem with
        {
            Message = newMessage,
            Entities = entities,
            Media = media,
            ReplyMarkup = replyMarkup,
            EditDate = editDate,
            InvertMedia = invertMedia,
            Hashtags = hashtags,
            EncryptedData = newEncryptedData,
            InboxMessageEncryptedData = newInboxMessageEncryptedData
        };

        Emit(new OutboxMessageEditedEventV2(requestInfo,
            oldMessageItem,
            newMessageItem
        ));
    }

    [DoNotInheritRequestCommand]
    public void ForwardMessage(
        RequestInfo requestInfo,
        long randomId)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        var originalMessageItem = _state.MessageItem;
        Emit(new MessageForwardedEvent(requestInfo, randomId, originalMessageItem));
    }

    public void IncrementViews()
    {
        //Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        if (!IsNew)
        {
            var views = (_state.MessageItem.Views ?? 0) + 1;
            Emit(new MessageViewsIncrementedEvent(_state.MessageItem.MessageId, views));
        }
    }

    /// <summary>
    /// Successor of <see cref="IncrementViews"/> whose event also carries the owning peer, so per-view
    /// increments can be attributed to a channel (stats ingestion). The V1 event lacks the owner and its
    /// historical instances stay applied via the old handler; no upgrader is needed for a new event name.
    /// </summary>
    public void IncrementViews2()
    {
        if (!IsNew)
        {
            var views = (_state.MessageItem.Views ?? 0) + 1;
            Emit(new MessageViewsIncrementedEvent2(_state.MessageItem.MessageId, views, _state.MessageItem.OwnerPeer));
        }
    }

    [DoNotInheritRequestCommand]
    public void PinChannelMessage(RequestInfo requestInfo)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        var channelId = _state.MessageItem.ToPeer.PeerId;
        Emit(new ChannelMessagePinnedEvent(requestInfo, channelId, _state.MessageItem.MessageId));
    }

    [DoNotInheritRequestCommand]
    public void ReadInboxHistory(RequestInfo requestInfo,
        long readerUid)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        var isOut = _state.MessageItem.SenderPeer.PeerId == readerUid;
        Emit(new InboxMessageHasReadEvent(requestInfo,
            readerUid,
            _state.MessageItem.MessageId,
            _state.MessageItem.SenderPeer.PeerId,
            _state.SenderMessageId,
            _state.MessageItem.ToPeer,
            isOut
        ));
    }

    [DoNotInheritRequestCommand]
    public void ReplyToMessage(RequestInfo requestInfo, Peer replierPeer, int repliesPts, int messageId)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        var reply = _state.MessageItem.Reply ?? new MessageReply(null, 0, repliesPts, messageId, new List<Peer>());
        reply.Replies++;

        // messageReplies.max_id is the id of the latest message in the comment section and
        // replies_pts the pts that goes with it, so both must follow every new reply — not just
        // the first one. See https://corefork.telegram.org/api/discussion
        reply.MaxId = messageId;
        reply.RepliesPts = repliesPts;

        var recentRepliers = reply.RecentRepliers ?? new List<Peer>();
        var peer = recentRepliers.FirstOrDefault(p => p.PeerId == replierPeer.PeerId);
        if (peer != null)
        {
            recentRepliers.Remove(peer);
        }

        recentRepliers.Insert(0, replierPeer);
        while (recentRepliers.Count > MyTelegramConsts.MaxRecentRepliersCount)
        {
            recentRepliers.RemoveAt(recentRepliers.Count - 1);
        }

        long? postChannelId = null;
        int? postMessageId = null;
        if (_state.MessageItem.FwdHeader?.ForwardFromLinkedChannel ?? false)
        {
            postChannelId = _state.MessageItem.PostChannelId;
            postMessageId = _state.MessageItem.PostMessageId;
        }

        reply.RecentRepliers = recentRepliers;
        var channelId = _state.MessageItem.ToPeer.PeerId;
        var replyToMessageId = _state.MessageItem.MessageId;
        Emit(new ReplyChannelMessageCompletedEvent(requestInfo, channelId,
            replyToMessageId, reply, postChannelId, postMessageId));
    }

    [DoNotInheritRequestCommand]
    public void UpdateInboxMessagePinned(
        RequestInfo requestInfo,
        bool pinned,
        bool pmOneSide,
        bool silent,
        int date)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        var item = _state.MessageItem;
        var ownerPeerId = item.OwnerPeer.PeerId;
        Emit(new InboxMessagePinnedUpdatedEvent(
            requestInfo,
            ownerPeerId,
            item.MessageId,
            pinned,
            pmOneSide,
            silent,
            date,
            item.ToPeer,
            _state.Pts));
    }

    /// <summary>
    /// Decrements the reply counter of a thread root after one of its replies was deleted, so
    /// <c>messageReplies.replies</c> keeps matching the number of messages actually left in the
    /// thread. See https://corefork.telegram.org/api/threads
    /// </summary>
    internal void ApplyAntiSpamReplyDeletion(string operationKey, int pts, bool alreadyApplied,
        long? postChannelId, int? postMessageId, string? expectedStaleSourceId = null)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        var current = _state.MessageItem.Reply;
        var replies = Math.Max(0, (current?.Replies ?? 0) - (alreadyApplied ? 0 : 1));
        var repliesPts = alreadyApplied ? current?.RepliesPts ?? 0 : pts;
        var reply = current == null ? null : new MessageReply(current.ChannelId, replies, repliesPts,
            current.MaxId, current.RecentRepliers == null ? null : [.. current.RecentRepliers]);
        Emit(new AntiSpamReplyDeletionAppliedEvent(operationKey, replies, repliesPts,
            postChannelId, postMessageId, reply, expectedStaleSourceId));
    }

    public void DecrementMessageReply(int pts)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        var reply = _state.MessageItem.Reply;
        if (reply == null || reply.Replies <= 0)
        {
            return;
        }

        // When the thread root is the auto-forwarded copy of a channel post, the comment counter
        // shown on the post itself must follow the same way it does when a comment is added
        // (see ReplyToMessage); otherwise deleting a comment leaves the post counting it forever.
        long? postChannelId = null;
        int? postMessageId = null;
        if (_state.MessageItem.FwdHeader?.ForwardFromLinkedChannel ?? false)
        {
            postChannelId = _state.MessageItem.PostChannelId;
            postMessageId = _state.MessageItem.PostMessageId;
        }

        var ownerChannelId = _state.MessageItem.OwnerPeer.PeerId;
        var replies = reply.Replies - 1;
        Emit(new MessageReplyCountDecrementedEvent(ownerChannelId,
            _state.MessageItem.MessageId,
            replies,
            pts,
            postChannelId,
            postMessageId));
    }

    public void UpdateMessageRely(int pts)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        var ownerChannelId = _state.MessageItem.OwnerPeer.PeerId;
        var channelId = MyTelegramConsts.DeletedChannelIdForChannelPost;
        Emit(new MessageReplyUpdatedEvent(ownerChannelId,
            channelId, _state.MessageItem.MessageId, pts));
    }

    [DoNotInheritRequestCommand]
    public void UpdateOutboxMessagePinned(
        RequestInfo requestInfo,
        bool pinned,
        bool pmOneSide,
        bool silent,
        int date)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        var item = _state.MessageItem;
        var ownerPeerId = item.OwnerPeer.PeerId;
        var senderPeerId = item.SenderPeer.PeerId;
        Emit(new OutboxMessagePinnedUpdatedEvent(
            requestInfo,
            ownerPeerId,
            item.MessageId,
            pinned,
            pmOneSide,
            silent,
            date,
            _state.InboxItems,
            senderPeerId,
            _state.SenderMessageId,
            item.ToPeer,
            _state.Pts,
            _state.MessageItem.Post
        ));
    }

    protected override Task<MessageSnapshot> CreateSnapshotAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(new MessageSnapshot(_state.MessageItem,
            _state.InboxItems,
            _state.SenderMessageId,
            _state.Pinned,
            _state.EditDate,
            //_state.EditHide,
            _state.Edited,
            _state.Pts,
            _state.IsDeleted,
            _state.IsChannelDeleted,
            _state.ChannelDeletionRequestId,
            _channelDeletionProofVersion,
            _state.AntiSpamDeletionCancelledRequestId
        ));
    }

    protected override Task LoadSnapshotAsync(MessageSnapshot snapshot,
      ISnapshotMetadata metadata,
      CancellationToken cancellationToken)
    {
        _state.LoadSnapshot(snapshot);
        _channelDeletionProofVersion = snapshot.ChannelDeletionProofVersion;
        return Task.CompletedTask;
    }
}

public class ChannelMessageDeletionAcknowledgedEvent(RequestInfo requestInfo, long channelId,
    int messageId, Guid? originalDeletionRequestId)
    : RequestAggregateEvent2<MessageAggregate, MessageId>(requestInfo)
{
    public long ChannelId { get; } = channelId;
    public int MessageId { get; } = messageId;
    public Guid? OriginalDeletionRequestId { get; } = originalDeletionRequestId;
}
