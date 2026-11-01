using MyTelegram.Messenger.Services.Business;

namespace MyTelegram.Messenger.QueryServer.DomainEventHandlers;

/// <summary>
/// Feeds a <a href="https://corefork.telegram.org/api/bots/connected-business-bots">connected business bot</a>
/// with the private chats of its account: messages the account receives and sends (except the ones the bot sent
/// itself, which it gets as the result of its own request), edits and deletions.
/// </summary>
public class BusinessBotFeedHandler(IBusinessBotNotifier notifier) :
    ISubscribeSynchronousTo<SendMessageSaga, SendMessageSagaId, SendOutboxMessageCompletedSagaEvent>,
    ISubscribeSynchronousTo<SendMessageSaga, SendMessageSagaId, ReceiveInboxMessageCompletedSagaEvent>,
    ISubscribeSynchronousTo<EditMessageSaga, EditMessageSagaId, OutboxMessageEditCompletedSagaEvent>,
    ISubscribeSynchronousTo<EditMessageSaga, EditMessageSagaId, InboxMessageEditCompletedSagaEvent>,
    ISubscribeSynchronousTo<DeleteMessagesSaga4, DeleteMessagesSaga4Id, MessageDeletedSagaEvent>
{
    public Task HandleAsync(
        IDomainEvent<SendMessageSaga, SendMessageSagaId, SendOutboxMessageCompletedSagaEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var e = domainEvent.AggregateEvent;
        var items = e.MessageItems.Where(IsPrivateChatMessage).ToList();
        return items.Count == 0
            ? Task.CompletedTask
            : notifier.NotifyMessagesAsync(items[0].OwnerPeer.PeerId, items[0].ToPeer.PeerId, items, false,
                e.RequestInfo.BusinessBotId);
    }

    public Task HandleAsync(
        IDomainEvent<SendMessageSaga, SendMessageSagaId, ReceiveInboxMessageCompletedSagaEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var items = domainEvent.AggregateEvent.MessageItems.Where(IsPrivateChatMessage).ToList();
        return items.Count == 0
            ? Task.CompletedTask
            : notifier.NotifyMessagesAsync(items[0].OwnerPeer.PeerId, items[0].ToPeer.PeerId, items, false);
    }

    public Task HandleAsync(
        IDomainEvent<EditMessageSaga, EditMessageSagaId, OutboxMessageEditCompletedSagaEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var item = domainEvent.AggregateEvent.NewMessageItem;
        return IsPrivateChatMessage(item)
            ? notifier.NotifyMessagesAsync(item.OwnerPeer.PeerId, item.ToPeer.PeerId, [item], true,
                domainEvent.AggregateEvent.RequestInfo.BusinessBotId)
            : Task.CompletedTask;
    }

    public Task HandleAsync(
        IDomainEvent<EditMessageSaga, EditMessageSagaId, InboxMessageEditCompletedSagaEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var item = domainEvent.AggregateEvent.NewMessageItem;
        return IsPrivateChatMessage(item)
            ? notifier.NotifyMessagesAsync(item.OwnerPeer.PeerId, item.ToPeer.PeerId, [item], true)
            : Task.CompletedTask;
    }

    public Task HandleAsync(
        IDomainEvent<DeleteMessagesSaga4, DeleteMessagesSaga4Id, MessageDeletedSagaEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var item = domainEvent.AggregateEvent.MessageItem;
        return item.ToPeerType == PeerType.User && item.ToPeerId != item.OwnerUserId
            ? notifier.NotifyDeletedAsync(item.OwnerUserId, item.ToPeerId, [item.MessageId])
            : Task.CompletedTask;
    }

    private static bool IsPrivateChatMessage(MessageItem item) =>
        item.QuickReplyItem == null &&
        item.ScheduleDate == null &&
        item.OwnerPeer.PeerType == PeerType.User &&
        item.ToPeer.PeerType == PeerType.User &&
        item.ToPeer.PeerId != item.OwnerPeer.PeerId;
}
