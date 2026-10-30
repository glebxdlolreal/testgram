namespace MyTelegram.ReadModel.ReadModelLocators;

public class MessageIdLocator : IMessageIdLocator, ITransientDependency
{
    public IEnumerable<string> GetReadModelIds(IDomainEvent domainEvent)
    {
        var aggregateEvent = domainEvent.GetAggregateEvent();

        if (domainEvent.AggregateType == typeof(MessageAggregate))
        {
            if (aggregateEvent is ReplyChannelMessageCompletedEvent replyChannelMessageCompletedEvent)
            {
                if (replyChannelMessageCompletedEvent is { PostChannelId: not null, PostMessageId: not null })
                {
                    yield return MessageId.Create(replyChannelMessageCompletedEvent.PostChannelId.Value,
                        replyChannelMessageCompletedEvent.PostMessageId.Value).Value;
                }
            }

            // A deleted comment must disappear from the counter on the channel post as well, the same
            // way a new comment reaches it above. See https://corefork.telegram.org/api/threads
            if (aggregateEvent is MessageReplyCountDecrementedEvent
                { PostChannelId: not null, PostMessageId: not null } messageReplyCountDecrementedEvent)
            {
                yield return MessageId.Create(messageReplyCountDecrementedEvent.PostChannelId.Value,
                    messageReplyCountDecrementedEvent.PostMessageId.Value).Value;
            }

            if (aggregateEvent is AntiSpamReplyDeletionAppliedEvent { PostChannelId: not null, PostMessageId: not null } effect)
                yield return MessageId.Create(effect.PostChannelId.Value, effect.PostMessageId.Value).Value;

            yield return domainEvent.GetIdentity().Value;
        }
        else if (aggregateEvent is MyTelegram.Domain.Aggregates.AntiSpamCompletion.AntiSpamCompletionCreatedEvent completion)
        {
            yield return MessageId.Create(completion.ChannelId, completion.MessageId).Value;
        }
        else if (domainEvent.AggregateType == typeof(SendMessageSaga))
        {
            switch (aggregateEvent)
            {
                case PostChannelIdUpdatedSagaEvent postChannelIdUpdatedEvent:
                    yield return MessageId.Create(postChannelIdUpdatedEvent.ChannelId,
                        postChannelIdUpdatedEvent.MessageId).Value;
                    break;
                case SendOutboxMessageCompletedSagaEvent sendOutboxMessageSuccessEvent:
                    //yield return MessageId.Create(sendOutboxMessageSuccessEvent.MessageItem.OwnerPeer.PeerId,
                    //    sendOutboxMessageSuccessEvent.MessageItem.MessageId, sendOutboxMessageSuccessEvent.MessageItem.QuickReplyItem != null).Value;
                    foreach (var item in sendOutboxMessageSuccessEvent.MessageItems)
                    {
                        yield return MessageId
                            .Create(item.OwnerPeer.PeerId, item.MessageId, item.QuickReplyItem != null).Value;
                    }

                    break;
                case ReceiveInboxMessageCompletedSagaEvent receiveInboxMessageSuccessEvent:
                    //yield return MessageId.Create(receiveInboxMessageSuccessEvent.MessageItem.OwnerPeer.PeerId,
                    //    receiveInboxMessageSuccessEvent.MessageItem.MessageId).Value;
                    foreach (var item in receiveInboxMessageSuccessEvent.MessageItems)
                    {
                        yield return MessageId
                            .Create(item.OwnerPeer.PeerId, item.MessageId, item.QuickReplyItem != null).Value;
                    }
                    break;
            }
        }
    }
}