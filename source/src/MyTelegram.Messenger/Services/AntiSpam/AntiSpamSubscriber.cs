using EventFlow.Subscribers;

namespace MyTelegram.Messenger.Services.AntiSpam;

public sealed class AntiSpamSubscriber(AntiSpamClassifier classifier, AntiSpamJobStore store,
    IQueryProcessor queries, IMessageConverterService converter, ILogger<AntiSpamSubscriber> logger)
    : ISubscribeSynchronousTo<MessageAggregate, MessageId, OutboxMessageCreatedEvent>,
      ISubscribeSynchronousTo<MessageAggregate, MessageId, ChannelMessageDeletedEvent>
{
    public static bool EligibleChannel(IChannelReadModel? channel, long sender) =>
        channel is { MegaGroup: true, Broadcast: false, Gigagroup: false, AntiSpam: true, IsDeleted: false } &&
        sender != channel.CreatorId && !channel.AdminList.Any(x => x.UserId == sender) &&
        sender > MyTelegramConsts.UserIdInitId && !MyTelegramConsts.IsReservedSystemBotUserId(sender) &&
        sender != MyTelegramConsts.BotFatherUserId && sender != MyTelegramConsts.ChatImporterBotUserId &&
        sender != MyTelegramConsts.RepliesServiceUserId && sender != MyTelegramConsts.AnonymousUserId;

    public async Task HandleAsync(IDomainEvent<MessageAggregate, MessageId, OutboxMessageCreatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var item = domainEvent.AggregateEvent.OutboxMessageItem;
        if (item.ToPeer.PeerType != PeerType.Channel || item.OwnerPeer.PeerType != PeerType.Channel ||
            item.OwnerPeer.PeerId != item.ToPeer.PeerId || item.SenderPeer.PeerType != PeerType.User ||
            item.SenderPeer.PeerId != item.SenderUserId || item.SendAs != null || item.Post || item.MessageAction != null ||
            item.SendMessageType != SendMessageType.Text || item.MessageSubType != MessageSubType.Normal ||
            item.FwdHeader != null) return;
        // No command is executed under the message event's aggregate lock.
        try
        {
            var text = item.EncryptedData is { } encrypted
                ? converter.DecryptMessage(item.OwnerPeer.PeerId, item.MessageId, encrypted)
                : item.Message;
            var fingerprint = classifier.Fingerprint(text);
            if (fingerprint == null) return;
            var channel = await queries.ProcessAsync(new GetChannelByIdQuery(item.ToPeer.PeerId), cancellationToken);
            if (!EligibleChannel(channel, item.SenderUserId)) return;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (item.Date < now - classifier.WindowSeconds || item.Date > now + 5) return;
            var recent = await store.ObserveAsync(item.ToPeer.PeerId, item.SenderUserId, item.MessageId,
                item.Date, fingerprint, cancellationToken);
            if (!classifier.IsSpam(fingerprint, recent, item.Date)) return;
            var message = converter.ToMessage(MyTelegramConsts.AntiSpamBotUserId, item);
            var original = new ArrayBufferWriter<byte>();
            message.Serialize(original);
            var action = new ArrayBufferWriter<byte>();
            new TChannelAdminLogEventActionDeleteMessage { Message = message }.Serialize(action);
            await store.EnqueueAsync(item.ToPeer.PeerId, item.SenderUserId, item.MessageId, fingerprint,
                original.WrittenSpan.ToArray(), action.WrittenSpan.ToArray(), cancellationToken, domainEvent.AggregateSequenceNumber);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Could not enqueue local anti-spam observation for {ChannelId}/{MessageId}", item.ToPeer.PeerId, item.MessageId);
        }
    }

    public Task HandleAsync(IDomainEvent<MessageAggregate, MessageId, ChannelMessageDeletedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var e = domainEvent.AggregateEvent;
        return e.RequestInfo.UserId == MyTelegramConsts.AntiSpamBotUserId
            ? store.ConfirmAsync(e.ChannelId, e.MessageId, e.RequestInfo.RequestId, cancellationToken)
            : Task.CompletedTask;
    }
}
