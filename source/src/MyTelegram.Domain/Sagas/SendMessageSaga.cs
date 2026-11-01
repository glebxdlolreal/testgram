namespace MyTelegram.Domain.Sagas;

public class ReplyBroadcastChannelCompletedSagaEvent(long channelId, int messageId, MessageReply reply)
    : AggregateEvent<SendMessageSaga, SendMessageSagaId>
{
    public long ChannelId { get; } = channelId;
    public int MessageId { get; } = messageId;
    public MessageReply Reply { get; } = reply;
}

public class PostChannelIdUpdatedSagaEvent(long channelId, int messageId, long postChannelId, int postMessageId)
    : AggregateEvent<SendMessageSaga, SendMessageSagaId>
{
    public long ChannelId { get; } = channelId;
    public int MessageId { get; } = messageId;
    public long PostChannelId { get; } = postChannelId;
    public int PostMessageId { get; } = postMessageId;
}

public class SendMessageSaga : MyInMemoryAggregateSaga<SendMessageSaga, SendMessageSagaId, SendMessageSagaLocator>,
    //ISagaIsStartedBy<MessageAggregate, MessageId, OutboxMessageCreatedEvent>,
    ISagaIsStartedBy<TempAggregate, TempId, SendMessageStartedEvent>,
    ISagaHandles<MessageAggregate, MessageId, OutboxMessageCreatedEvent>,
    ISagaHandles<MessageAggregate, MessageId, InboxMessageCreatedEvent>,
    ISagaHandles<MessageAggregate, MessageId, ReplyChannelMessageCompletedEvent>
{
    private readonly IIdGenerator _idGenerator;
    private readonly SendMessageSagaState _state = new();
    public SendMessageSaga(SendMessageSagaId id, IEventStore eventStore, IIdGenerator idGenerator) : base(id, eventStore)
    {
        _idGenerator = idGenerator;
        Register(_state);
    }

    public Task HandleAsync(IDomainEvent<TempAggregate, TempId, SendMessageStartedEvent> domainEvent, ISagaContext sagaContext, CancellationToken cancellationToken)
    {
        // Idempotency: skip if already started (RabbitMQ redelivery)
        if (_state.SendMessageItems.Count > 0) return Task.CompletedTask;
        return StartSendMessageAsync(domainEvent.AggregateEvent.RequestInfo,
            domainEvent.AggregateEvent.SendMessageItems,
            domainEvent.AggregateEvent.ClearDraft,
            domainEvent.AggregateEvent.IsSendQuickReplyMessages,
            domainEvent.AggregateEvent.IsSendGroupedMessages
        );
    }

    private async Task StartSendMessageAsync(RequestInfo requestInfo, List<SendMessageItem> sendMessageItems,
        bool clearDraft,
        bool isSendQuickReplyMessages,
        bool isSendGroupedMessages
        )
    {
        var newItems = new List<SendMessageItem>();
        foreach (var sendMessageItem in sendMessageItems)
        {
            var messageItem = sendMessageItem.MessageItem;

            var messageId = messageItem.MessageId;

            if (messageId == 0)
            {
                var idType = IdType.MessageId;
                if (messageItem.ScheduleDate != null)
                {
                    idType = IdType.ScheduleMessageId;
                }
                messageId = await _idGenerator.NextIdAsync(idType, messageItem.OwnerPeer.PeerId);
            }

            // A quick reply message is stored outside every dialog: it never enters the update sequence
            // of its owner, so it takes no pts. See https://corefork.telegram.org/api/business#quick-reply-shortcuts
            if (messageItem.Pts == 0 && messageItem.QuickReplyItem == null)
            {
                var pts = await _idGenerator.NextIdAsync(IdType.Pts, messageItem.OwnerPeer.PeerId);

                messageItem = messageItem with { Pts = pts, MessageId = messageId };
                var newItem = sendMessageItem with { MessageItem = messageItem };
                newItems.Add(newItem);
            }
            else
            {
                messageItem = messageItem with { MessageId = messageId };
                var newItem = sendMessageItem with { MessageItem = messageItem };
                newItems.Add(newItem);
            }
        }

        Emit(new SendMessageStartedSagaEvent(requestInfo,
            newItems,
            [],
            clearDraft,
            isSendQuickReplyMessages,
            isSendGroupedMessages
        ));

        foreach (var item in newItems)
        {
            var messageItem = item.MessageItem;
            var command = new CreateOutboxMessageCommand(
                MessageId.Create(messageItem.OwnerPeer.PeerId,
                    messageItem.MessageId,
                    messageItem.QuickReplyItem != null
                ),
                requestInfo,
                messageItem with { InboxMessageEncryptedData = null },
                item.MentionedUserIds,
                messageItem.ReplyToMsgItems,
                item.ClearDraft,
                linkedChannelId: messageItem.LinkedChannelId,
                chatMembers: item.ChatMembers
            );
            Publish(command);
        }
    }

    public async Task HandleAsync(IDomainEvent<MessageAggregate, MessageId, OutboxMessageCreatedEvent> domainEvent, ISagaContext sagaContext, CancellationToken cancellationToken)
    {
        Emit(new OutboxMessageCreatedSagaEvent(domainEvent.AggregateEvent.RequestInfo,
            domainEvent.AggregateEvent.OutboxMessageItem,
            domainEvent.AggregateEvent.MentionedUserIds,
            domainEvent.AggregateEvent.ReplyToMsgItems,
            domainEvent.AggregateEvent.ChatMembers
            ));
        await HandleSendOutboxMessageCompletedAsync(domainEvent.AggregateEvent.OutboxMessageItem);

        await CreateInboxMessageAsync(domainEvent.AggregateEvent);

        CreateMentions(domainEvent.AggregateEvent);
        ClearDraft(domainEvent.AggregateEvent);
    }

    private void ClearDraft(OutboxMessageCreatedEvent aggregateEvent)
    {
        if (!aggregateEvent.ClearDraft || aggregateEvent.OutboxMessageItem.QuickReplyItem != null)
        {
            return;
        }

        // The draft belongs to the user who typed it, not to the box the message was written into: for a
        // channel or a group the outbox item is owned by the peer itself, and addressing the dialog by
        // that owner cleared a dialog nobody holds a draft in.
        var ownerUserId = aggregateEvent.RequestInfo.UserId;
        if (ownerUserId == 0)
        {
            return;
        }

        var item = aggregateEvent.OutboxMessageItem;

        // Sending into a topic clears the draft of that topic, not the draft of the chat.
        var command = new ClearDraftsCommand(DialogId.Create(ownerUserId, item.ToPeer),
            aggregateEvent.RequestInfo,
            ownerUserId,
            item.ToPeer,
            [new DraftTopic(item.TopMsgId, item.SavedPeerId)]);
        Publish(command);
    }

    /// <summary>
    /// Bumps the @ badge of every mentioned member, see https://corefork.telegram.org/api/mentions.
    /// Mentions are only resolved for channel/supergroup members
    /// (<see cref="MyTelegram.Messenger"/> MessageAppService.ProcessMessageEntityMentionAsync),
    /// so the mentioned user always already has a dialog with the peer.
    /// </summary>
    private void CreateMentions(OutboxMessageCreatedEvent aggregateEvent)
    {
        var mentionedUserIds = aggregateEvent.MentionedUserIds;
        if (!(mentionedUserIds?.Count > 0))
        {
            return;
        }

        var item = aggregateEvent.OutboxMessageItem;
        if (item.ToPeer.PeerType != PeerType.Channel)
        {
            return;
        }

        foreach (var mentionedUserId in mentionedUserIds.Distinct())
        {
            // Mentioning yourself must not light up your own badge.
            if (mentionedUserId == item.SenderPeer.PeerId)
            {
                continue;
            }

            Publish(new CreateMentionCommand(DialogId.Create(mentionedUserId, item.ToPeer), item.MessageId));
        }
    }

    public Task HandleAsync(IDomainEvent<MessageAggregate, MessageId, InboxMessageCreatedEvent> domainEvent, ISagaContext sagaContext, CancellationToken cancellationToken)
    {
        var item = domainEvent.AggregateEvent.InboxMessageItem;

        var command = new ReceiveInboxMessageCommand(
            DialogId.Create(domainEvent.AggregateEvent.InboxMessageItem.OwnerPeer.PeerId,
                domainEvent.AggregateEvent.InboxMessageItem.ToPeer),
            domainEvent.AggregateEvent.RequestInfo,
            domainEvent.AggregateEvent.InboxMessageItem.MessageId,
            domainEvent.AggregateEvent.InboxMessageItem.OwnerPeer.PeerId,
            domainEvent.AggregateEvent.InboxMessageItem.ToPeer);
        Publish(command);

        // Published right after the command that creates the dialog, so the mention lands on an
        // aggregate that already exists. See https://corefork.telegram.org/api/mentions
        if (item.MentionedUserIds?.Contains(item.OwnerPeer.PeerId) ?? false)
        {
            Publish(new CreateMentionCommand(DialogId.Create(item.OwnerPeer.PeerId, item.ToPeer), item.MessageId));
        }

        return HandleReceiveInboxMessageCompletedAsync(item, domainEvent.AggregateEvent.SenderMessageId);
    }

    private async Task CreateInboxMessageAsync(OutboxMessageCreatedEvent aggregateEvent)
    {
        switch (aggregateEvent.OutboxMessageItem.ToPeer.PeerType)
        {
            case PeerType.User:
                // Messages to Saved Messages are already represented by the
                // outbox row owned by the current user. Creating an inbox copy
                // for the same owner makes self-directed service actions (gift
                // sends/upgrades in particular) appear multiple times.
                if (aggregateEvent.OutboxMessageItem.ToPeer.PeerId != aggregateEvent.OutboxMessageItem.OwnerPeer.PeerId)
                {
                    await CreateInboxMessageForUserAsync(aggregateEvent.OutboxMessageItem, aggregateEvent.OutboxMessageItem.ToPeer.PeerId);
                }
                break;
            case PeerType.Chat:
                if (aggregateEvent.ChatMembers?.Count > 0)
                {
                    foreach (var chatMemberUserId in aggregateEvent.ChatMembers)
                    {
                        if (chatMemberUserId == aggregateEvent.RequestInfo.UserId)
                        {
                            continue;
                        }

                        await CreateInboxMessageForUserAsync(aggregateEvent.OutboxMessageItem, chatMemberUserId);
                    }
                }

                break;
        }
    }

    private async Task HandleReceiveInboxMessageCompletedAsync(MessageItem inboxMessageItem, int senderMessageId)
    {
        var pts = inboxMessageItem.Pts;// await _idGenerator.NextIdAsync(IdType.Pts, inboxMessageItem.OwnerPeer.PeerId);
        var newInboxMessageItem = inboxMessageItem with { Pts = pts };
        Emit(new InboxMessageCreatedSagaEvent(_state.RequestInfo, newInboxMessageItem));

        _state.UserInboxItems.TryGetValue(inboxMessageItem.BatchId ?? Guid.Empty, out var inboxItems);

        var command = new AddInboxItemsToOutboxMessageCommand(
            MessageId.Create(inboxMessageItem.SenderPeer.PeerId,
                senderMessageId, inboxMessageItem.QuickReplyItem != null),
            //_state.RequestInfo,
            inboxItems ?? []
        );
        Publish(command);

        if (_state.IsCreateInboxMessagesCompleted())
        {
            Emit(new ReceiveInboxMessageCompletedSagaEvent(_state.InboxMessageItems, _state.IsSendQuickReplyMessages, _state.IsSendGroupedMessages));

            await CompleteAsync();
        }
    }

    private async Task HandleSendOutboxMessageCompletedAsync(MessageItem outboxMessageItem)
    {
        var pts = outboxMessageItem.Pts;// await _idGenerator.NextIdAsync(IdType.Pts, _state.MessageItem.OwnerPeer.PeerId);
        //var linkedChannelId = _state.LinkedChannelId;
        //var globalSeqNo = _state.MessageItem.ToPeer.PeerType == PeerType.Channel ? await _idGenerator.NextLongIdAsync(IdType.GlobalSeqNo) : 0;

        if (_state.IsSendOutboxMessageCompleted)
        {
            Emit(new SendOutboxMessageCompletedSagaEvent(_state.RequestInfo,
                _state.SendMessageItems.Select(p => p.MessageItem).ToList(),
                //_state.MentionedUserIds,
                outboxMessageItem.MentionedUserIds,
                _state.IsSendQuickReplyMessages,
                _state.IsSendGroupedMessages,
                []
                ));
        }

        if (outboxMessageItem.QuickReplyItem != null)
        {
            // Not part of any dialog: Saved Messages must not pick it up as its top message.
            if (_state.IsSendOutboxMessageCompleted)
            {
                await CompleteAsync();
            }

            return;
        }

        var defaultHistoryTtl = outboxMessageItem.IsTtlFromDefaultSetting ? outboxMessageItem.TtlPeriod : null;
        var command = new UpdateDialogCommand(
            DialogId.Create(outboxMessageItem.SenderUserId, outboxMessageItem.ToPeer),
            _state.RequestInfo with { RequestId = Guid.NewGuid() },
            outboxMessageItem.SenderUserId,
            outboxMessageItem.ToPeer,
            outboxMessageItem.MessageId,
            pts,
            defaultHistoryTtl,
            false
        );
        Publish(command);

        if (outboxMessageItem.ToPeer.PeerType == PeerType.Channel)
        {
            SetChannelPts(outboxMessageItem);

            if (_state.FirstMessageItem.MessageItem.LinkedChannelId.HasValue && outboxMessageItem.SendMessageType != SendMessageType.MessageService)
            {
                ForwardBroadcastMessageToLinkedChannel(outboxMessageItem);
            }

            // handle reply discussion message
            if (!HandleReplyDiscussionMessage(outboxMessageItem))
            {
                await CompleteAsync();
            }
        }
    }


    private bool HandleReplyDiscussionMessage(MessageItem outboxMessageItem)
    {
        if (outboxMessageItem is { InputReplyTo: not null, ToPeer.PeerType: PeerType.Channel })
        {
            switch (outboxMessageItem.InputReplyTo)
            {
                case TInputReplyToMessage inputReplyToMessage:
                    // The reply counter belongs to the root of the thread, not to the message that was
                    // directly answered: answering a comment must grow the counter of the thread
                    // starter (and, for a comment section, of the channel post behind it), the same
                    // root MessageAggregate.DeleteChannelMessage decrements when a comment is deleted.
                    var threadRootMessageId =
                        MessageThreadHelper.GetThreadRootMessageId(outboxMessageItem)
                        ?? inputReplyToMessage.ReplyToMsgId;
                    ReplyToMessage(outboxMessageItem, threadRootMessageId);
                    return true;
                case TInputReplyToMonoForum:
                    // Sending to a monoforum topic is not a thread/discussion reply:
                    // the target topic is carried by SavedPeerId/MonoforumPeerId instead.
                    return false;
                case TInputReplyToStory:
                    // Replying to a story is not a thread/discussion reply either: the story reference
                    // travels on the message as messageReplyStoryHeader (see
                    // Extension.ToMessageReplyHeader), so no reply-to-message id must be assigned here.
                    return false;
                default:
                    return false;
            }
        }

        return false;
    }
    private void ForwardBroadcastMessageToLinkedChannel(MessageItem outboxMessageItem)
    {
        if (outboxMessageItem.LinkedChannelId == null)
        {
            return;
        }
        var fromPeer = outboxMessageItem.ToPeer;
        var toPeer = new Peer(PeerType.Channel, outboxMessageItem.LinkedChannelId!.Value);
        var command = new StartForwardMessagesCommand(TempId.New,
            _state.RequestInfo,
            false,
            false,
            false,
            false,
            false,
            false,
            fromPeer,
            toPeer,
            [outboxMessageItem.MessageId],
            [Random.Shared.NextInt64()],
            null,
            outboxMessageItem.SendAs,
            true,
            false,
            // Auto-forwarding a broadcast post to its linked discussion group is attributed to
            // the channel, not to a user whose forward privacy could apply.
            null
        );
        Publish(command);
    }

    private void ReplyToMessage(MessageItem outboxMessageItem, int replyToMessageId)
    {
        // long ownerPeerId, int messageId, int repliesPts, int maxMessageId
        // ReplyToMessage(outboxMessageItem.ToPeer.PeerId, inputReplyToMessage.ReplyToMsgId, outboxMessageItem.Pts, outboxMessageItem.MessageId);
        var replierPeer = outboxMessageItem.SendAs ?? _state.RequestInfo.UserId.ToUserPeer();
        var command = new ReplyToMessageCommand(MessageId.Create(outboxMessageItem.ToPeer.PeerId, replyToMessageId),
            _state.RequestInfo, replierPeer, outboxMessageItem.Pts, outboxMessageItem.MessageId);
        Publish(command);
    }

    private void SetChannelPts(MessageItem outboxMessageItem)
    {
        //long channelId, int pts, int messageId
        var command = new SetChannelPtsCommand(ChannelId.Create(outboxMessageItem.ToPeer.PeerId),
            outboxMessageItem.SenderPeer.PeerId,
            outboxMessageItem.Pts,
            outboxMessageItem.MessageId,
            outboxMessageItem.Date);
        Publish(command);
    }
    private async Task CreateInboxMessageForUserAsync(MessageItem outboxMessageItem, long inboxOwnerUserId)
    {
        var outMessageItem = outboxMessageItem;
        var toPeer = outMessageItem.ToPeer.PeerType == PeerType.Chat ? outMessageItem.ToPeer : outMessageItem.OwnerPeer;

        var replyTo = outMessageItem.InputReplyTo;
        var replyToMsgItems = outboxMessageItem.ReplyToMsgItems?.ToDictionary(k => k.UserId, v => v.MessageId) ?? new();
        if (replyToMsgItems.TryGetValue(inboxOwnerUserId, out var replyToMsgId))
        {
            switch (replyTo)
            {
                // A reply that names another peer (reply_to_peer_id) points at a message living in
                // that peer, so its id must stay as it is - remapping it into this dialog's id space
                // would make it point at an unrelated message.
                case TInputReplyToMessage { ReplyToPeerId: null } inputReplyToMessage:
                    inputReplyToMessage.ReplyToMsgId = replyToMsgId;
                    break;
                // A story reply carries the story id, which is the same for every recipient; it must not
                // be rewritten to the per-recipient inbox message id.
            }
        }

        var inboxMessageId = await _idGenerator.NextIdAsync(IdType.MessageId, inboxOwnerUserId);
        var pts = await _idGenerator.NextIdAsync(IdType.Pts, inboxOwnerUserId);
        var aggregateId = MessageId.Create(inboxOwnerUserId, inboxMessageId);
        ReadOnlyMemory<byte>? encryptedData = null;
        if (_state.OutboxMessageItems.TryGetValue(outboxMessageItem.MessageId, out var item))
        {
            encryptedData = item.MessageItem.InboxMessageEncryptedData;
        }
        var inboxMessageItem = outMessageItem with
        {
            OwnerPeer = new Peer(PeerType.User, inboxOwnerUserId),
            ToPeer = toPeer,
            MessageId = inboxMessageId,
            IsOut = false,
            InputReplyTo = replyTo,
            Pts = pts,
            EncryptedData = encryptedData
        };

        var command = new CreateInboxMessageCommand(aggregateId, _state.RequestInfo, inboxMessageItem, outMessageItem.MessageId);
        Publish(command);
    }

    public Task HandleAsync(IDomainEvent<MessageAggregate, MessageId, ReplyChannelMessageCompletedEvent> domainEvent, ISagaContext sagaContext, CancellationToken cancellationToken)
    {
        if (domainEvent.AggregateEvent.PostChannelId.HasValue && domainEvent.AggregateEvent.PostMessageId.HasValue)
        {
            Emit(new ReplyBroadcastChannelCompletedSagaEvent(domainEvent.AggregateEvent.PostChannelId.Value, domainEvent.AggregateEvent.PostMessageId.Value, domainEvent.AggregateEvent.Reply));
            Emit(new PostChannelIdUpdatedSagaEvent(_state.FirstMessageItem.MessageItem.OwnerPeer.PeerId, _state.FirstMessageItem.MessageItem.MessageId, domainEvent.AggregateEvent.PostChannelId.Value, domainEvent.AggregateEvent.PostMessageId.Value));
        }

        return CompleteAsync(cancellationToken);
    }


}
