using System.Collections.Concurrent;

namespace MyTelegram.Domain.Aggregates.Messaging;

public class MessageState : AggregateState<MessageAggregate, MessageId, MessageState>,
    IApply<OutboxMessageCreatedEvent>,
    IApply<InboxMessageCreatedEvent>,
    IApply<InboxMessageIdAddedToOutboxMessageEvent>,
    //IApply<MessageDeletedEvent>,
    //IApply<OutboxMessageEditedEvent>,
    //IApply<InboxMessageEditedEvent>,
    IApply<MessageForwardedEvent>,
    IApply<InboxMessageHasReadEvent>,
    //IApply<ReplyToMessageEvent>,
    IApply<MessageViewsIncrementedEvent>,
    IApply<MessageViewsIncrementedEvent2>,
    //IApply<UpdatePinnedMessageStartedEvent>,
    IApply<InboxMessagePinnedUpdatedEvent>,
    IApply<OutboxMessagePinnedUpdatedEvent>,
    IApply<OtherPartyMessageDeletedEvent>,
    //IApply<SelfMessageDeletedEvent>,
    //IApply<OutboxMessageDeletedEvent>,
    IApply<InboxMessageDeletedEvent>,
    IApply<InboxItemsAddedToOutboxMessageEvent>,
    IApply<MessageDeleted4Event>,
    IApply<QuickReplyMessageDeletedEvent>,
    IApply<ReplyChannelMessageCompletedEvent>,
    IApply<ChannelMessagePinnedEvent>,
    IApply<ChannelMessageDeletedEvent>,
    IApply<ChannelMessageDeletionAcknowledgedEvent>,
    IApply<AntiSpamDeletionCancelledEvent>,
    IApply<MessageReplyUpdatedEvent>,
    IApply<AntiSpamReplyDeletionAppliedEvent>,
    IApply<MessageReplyCountDecrementedEvent>,
    IApply<MessageUnpinnedEvent>,
    IApply<MessagePinnedUpdatedEvent>,
    IApply<OutboxMessageEditedEventV2>,
    IApply<InboxMessageEditedEventV2>,
    IApply<MessageReactionsUpdatedEvent>,
    IApply<MessageTodoUpdatedEvent>,
    IApply<MessageInvoiceReceiptUpdatedEvent>
{
    public int EditDate { get; private set; }
    //public bool EditHide { get; private set; }
    public bool Edited { get; private set; }

    public List<InboxItem> InboxItems { get; private set; } = new();
    public MessageItem MessageItem { get; private set; } = null!;
    public bool Pinned { get; private set; }
    public bool PmOneSide { get; private set; }
    public int Pts { get; private set; }
    public ConcurrentDictionary<long, ReactionCount> ReactionCounts { get; private set; } = new();
    public List<Reaction> RecentReactions { get; private set; } = new();

    public int SenderMessageId { get; private set; }
    public ConcurrentDictionary<long, List<Reaction>> UserReactions { get; } = new();

    public bool IsDeleted { get; private set; }
    public bool IsChannelDeleted { get; private set; }
    public Guid? ChannelDeletionRequestId { get; private set; }
    public Guid? AntiSpamDeletionCancelledRequestId { get; private set; }

    public void Apply(AntiSpamDeletionCancelledEvent aggregateEvent)
    {
        if (!IsDeleted)
        {
            AntiSpamDeletionCancelledRequestId ??= aggregateEvent.RequestId;
        }
    }

    public void Apply(ChannelMessageDeletedEvent aggregateEvent)
    {
        // Preserve the first identity even when replaying historical duplicate deletion events.
        if (!IsChannelDeleted)
        {
            ChannelDeletionRequestId = aggregateEvent.RequestInfo.RequestId;
        }
        IsChannelDeleted = true;
        IsDeleted = true;
    }

    public void Apply(ChannelMessageDeletionAcknowledgedEvent aggregateEvent)
    {
        // An acknowledgement is not another deletion and must not replace its owner.
    }

    public void Apply(ChannelMessagePinnedEvent aggregateEvent)
    {
        Pinned = true;
    }

    public void Apply(InboxItemsAddedToOutboxMessageEvent aggregateEvent)
    {
        InboxItems = aggregateEvent.InboxItems;
        MessageItem = MessageItem with
        {
            InboxItems = InboxItems
        };
    }

    public void Apply(InboxMessageCreatedEvent aggregateEvent)
    {
        MessageItem = aggregateEvent.InboxMessageItem;
        SenderMessageId = aggregateEvent.SenderMessageId;
    }

    public void Apply(InboxMessageDeletedEvent aggregateEvent)
    {
        IsDeleted = true;
    }

    //public void Apply(InboxMessageEditedEvent aggregateEvent)
    //{
    //    EditDate = aggregateEvent.EditDate;
    //    Edited = true;
    //}

    public void Apply(InboxMessageHasReadEvent aggregateEvent)
    {
    }

    public void Apply(InboxMessageIdAddedToOutboxMessageEvent aggregateEvent)
    {
        InboxItems.Add(aggregateEvent.InboxItem);
    }

    public void Apply(InboxMessagePinnedUpdatedEvent aggregateEvent)
    {
        Pinned = aggregateEvent.Pinned;
    }

    public void Apply(MessageDeleted4Event aggregateEvent)
    {
        IsDeleted = true;
    }

    public void Apply(QuickReplyMessageDeletedEvent aggregateEvent)
    {
        IsDeleted = true;
    }

    //public void Apply(MessageDeletedEvent aggregateEvent)
    //{
    //    IsDeleted = true;
    //}

    public void Apply(MessageForwardedEvent aggregateEvent)
    {
    }

    public void Apply(MessagePinnedUpdatedEvent aggregateEvent)
    {
        Pinned = aggregateEvent.Pinned;
    }

    public void Apply(MessageReplyUpdatedEvent aggregateEvent)
    {
        if (MessageItem.Reply != null)
        {
            MessageItem.Reply.ChannelId = aggregateEvent.ChannelId;
        }
    }

    public void Apply(AntiSpamReplyDeletionAppliedEvent aggregateEvent)
    {
        if (MessageItem.Reply != null)
        {
            MessageItem.Reply.Replies = aggregateEvent.Replies;
            MessageItem.Reply.RepliesPts = aggregateEvent.Pts;
        }
    }

    public void Apply(MessageReplyCountDecrementedEvent aggregateEvent)
    {
        if (MessageItem.Reply != null)
        {
            MessageItem.Reply.Replies = aggregateEvent.Replies;
            MessageItem.Reply.RepliesPts = aggregateEvent.Pts;
        }
    }

    public void Apply(MessageUnpinnedEvent aggregateEvent)
    {
        Pinned = false;
    }

    public void Apply(MessageViewsIncrementedEvent aggregateEvent)
    {
        MessageItem = MessageItem with { Views = aggregateEvent.Views };
    }

    public void Apply(MessageViewsIncrementedEvent2 aggregateEvent)
    {
        MessageItem = MessageItem with { Views = aggregateEvent.Views };
    }

    public void Apply(OtherPartyMessageDeletedEvent aggregateEvent)
    {
        IsDeleted = true;
    }

    public void Apply(OutboxMessageCreatedEvent aggregateEvent)
    {
        MessageItem = aggregateEvent.OutboxMessageItem;
        SenderMessageId = aggregateEvent.OutboxMessageItem.MessageId;
    }

    //public void Apply(OutboxMessageEditedEvent aggregateEvent)
    //{
    //    EditDate = aggregateEvent.EditDate;
    //    Edited = true;
    //}

    public void Apply(OutboxMessagePinnedUpdatedEvent aggregateEvent)
    {
        Pinned = aggregateEvent.Pinned;
    }

    public void Apply(ReplyChannelMessageCompletedEvent aggregateEvent)
    {
        MessageItem = MessageItem with { Reply = aggregateEvent.Reply };
    }

    //public void Apply(ReplyToMessageEvent aggregateEvent)
    //{
    //}

    //public void Apply(SelfMessageDeletedEvent aggregateEvent)
    //{
    //    IsDeleted = true;
    //}

    //public void Apply(UpdatePinnedMessageStartedEvent aggregateEvent)
    //{
    //    Pinned = aggregateEvent.Pinned;
    //    PmOneSide = aggregateEvent.PmOneSide;
    //}

    public void LoadSnapshot(MessageSnapshot snapshot)
    {
        MessageItem = snapshot.MessageItem;
        InboxItems = snapshot.InboxItems;
        SenderMessageId = snapshot.SenderMessageId;
        Pinned = snapshot.Pinned;
        EditDate = snapshot.EditDate;
        Edited = snapshot.Edited;
        Pts = snapshot.Pts;
        IsDeleted = snapshot.IsDeleted;
        IsChannelDeleted = snapshot.IsChannelDeleted;
        ChannelDeletionRequestId = snapshot.ChannelDeletionRequestId;
        AntiSpamDeletionCancelledRequestId = snapshot.AntiSpamDeletionCancelledRequestId;
    }

    public void Apply(OutboxMessageEditedEventV2 aggregateEvent)
    {
        EditDate = aggregateEvent.NewMessageItem.EditDate ?? 0;
        Edited = true;

        MessageItem = aggregateEvent.NewMessageItem;
    }

    public void Apply(InboxMessageEditedEventV2 aggregateEvent)
    {
        EditDate = aggregateEvent.NewMessageItem.EditDate ?? 0;
        Edited = true;

        MessageItem = aggregateEvent.NewMessageItem;
    }

    public void Apply(MessageReactionsUpdatedEvent aggregateEvent)
    {
        RecentReactions = aggregateEvent.Reactions;
        ReactionCounts.Clear();
        foreach (var r in aggregateEvent.Reactions)
        {
            var id = r.GetReactionId();
            if (ReactionCounts.TryGetValue(id, out var existing))
                existing.Count++;
            else
                ReactionCounts[id] = new ReactionCount(
                    r.IsPaid ? new TReactionPaid()
                        : string.IsNullOrEmpty(r.Emoticon)
                        ? new TReactionCustomEmoji { DocumentId = r.CustomEmojiDocumentId!.Value }
                        : (IReaction)new TReactionEmoji { Emoticon = r.Emoticon },
                    1, r.Emoticon, r.CustomEmojiDocumentId);
        }
    }

    public void Apply(MessageTodoUpdatedEvent aggregateEvent)
    {
        MessageItem = MessageItem with
        {
            Media = TodoMediaFactory.Create(aggregateEvent.Todo, aggregateEvent.Completions)
        };
    }

    public void Apply(MessageInvoiceReceiptUpdatedEvent aggregateEvent)
    {
        MessageItem = MessageItem with
        {
            Media = InvoiceMediaFactory.WithReceipt(MessageItem.Media, aggregateEvent.ReceiptMsgId)
        };
    }
}
