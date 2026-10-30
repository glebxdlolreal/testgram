using EventFlow.Subscribers;

namespace MyTelegram.Messenger.Services.AntiSpam;

public sealed class AntiSpamSubscriber(AntiSpamClassifier classifier, AntiSpamBurstTracker tracker,
    AntiSpamJobStore store, AntiSpamWindowStore window, IQueryProcessor queries, IMessageConverterService converter, IAggregateStore aggregates,
    IOptions<AntiSpamOptions> options, ILogger<AntiSpamSubscriber> logger, TimeProvider? timeProvider = null)
    : ISubscribeSynchronousTo<MessageAggregate, MessageId, OutboxMessageCreatedEvent>,
      ISubscribeSynchronousTo<MessageAggregate, MessageId, OutboxMessageEditedEventV2>,
      ISubscribeSynchronousTo<MessageAggregate, MessageId, ChannelMessageDeletedEvent>
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Enabled supergroups of sufficient size only; never broadcast channels or gigagroups. The sender is the
    /// real user (sender_id), so creator, administrators and system accounts are exempt however they post.
    /// </summary>
    public static bool EligibleChannel(IChannelReadModel? channel, long sender, int minimumParticipants) =>
        channel is { MegaGroup: true, Broadcast: false, Gigagroup: false, AntiSpam: true, IsDeleted: false } &&
        (channel.ParticipantsCount ?? 0) >= minimumParticipants &&
        sender != channel.CreatorId && !channel.AdminList.Any(x => x.UserId == sender) &&
        sender > MyTelegramConsts.UserIdInitId && !MyTelegramConsts.IsReservedSystemBotUserId(sender) &&
        sender != MyTelegramConsts.BotFatherUserId && sender != MyTelegramConsts.ChatImporterBotUserId &&
        sender != MyTelegramConsts.RepliesServiceUserId && sender != MyTelegramConsts.AnonymousUserId;

    /// <summary>
    /// Ordinary text messages (or media captions) a participant posted in the supergroup itself, including forwards and
    /// messages sent as one of the participant's channels. Posting as the group itself (anonymous admin),
    /// channel posts and automatic linked-channel forwards are out of scope.
    /// </summary>
    public static bool IsSubject(MessageItem item) =>
        item.ToPeer.PeerType == PeerType.Channel && item.OwnerPeer.PeerType == PeerType.Channel &&
        item.OwnerPeer.PeerId == item.ToPeer.PeerId && item.SenderUserId > 0 &&
        item.SendAs?.PeerId != item.ToPeer.PeerId && !item.Post && !item.IsForwardFromChannelPost &&
        item.MessageAction == null && item.SendMessageType is SendMessageType.Text or SendMessageType.Media &&
        item.MessageSubType is MessageSubType.Normal or MessageSubType.ForwardMessage &&
        item.ScheduleDate == null && item.QuickReplyItem == null;

    public async Task HandleAsync(IDomainEvent<MessageAggregate, MessageId, OutboxMessageCreatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var e = domainEvent.AggregateEvent;
        var item = e.OutboxMessageItem;
        // A forward replayed on behalf of someone else would carry the original author as sender.
        if (!IsSubject(item) || (item.FwdHeader != null && e.RequestInfo.UserId > 0 && e.RequestInfo.UserId != item.SenderUserId))
            return;
        // No command is executed under the message event's aggregate lock.
        try
        {
            var keys = classifier.Analyze(Text(item), item.Entities);
            if (keys == null) return;
            var now = _time.GetUtcNow().ToUnixTimeSeconds();
            if (item.Date < now - classifier.WindowSeconds || item.Date > now + 5) return;
            var channel = await queries.ProcessAsync(new GetChannelByIdQuery(item.ToPeer.PeerId), cancellationToken);
            if (!EligibleChannel(channel, item.SenderUserId, classifier.MinimumParticipants)) return;
            await RestoreAsync(item.ToPeer.PeerId, item.SenderUserId, now, cancellationToken);
            var verdict = tracker.Observe(item.ToPeer.PeerId, item.SenderUserId, item.MessageId, item.Date, keys.Value);
            await PersistAsync(item, keys, cancellationToken);
            if (verdict != null)
                await EnqueueBurstAsync(item, domainEvent.AggregateSequenceNumber, verdict, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not enqueue local anti-spam observation for {ChannelId}/{MessageId}", item.ToPeer.PeerId, item.MessageId);
        }
    }

    public async Task HandleAsync(IDomainEvent<MessageAggregate, MessageId, OutboxMessageEditedEventV2> domainEvent,
        CancellationToken cancellationToken)
    {
        var item = domainEvent.AggregateEvent.NewMessageItem;
        if (!IsSubject(item)) return;
        try
        {
            var keys = classifier.Analyze(Text(item), item.Entities);
            var editDate = item.EditDate ?? _time.GetUtcNow().ToUnixTimeSeconds();
            if (keys == null)
            {
                tracker.Edit(item.ToPeer.PeerId, item.SenderUserId, item.MessageId, item.Date, editDate, null);
                await PersistAsync(item, null, cancellationToken);
                return;
            }
            var channel = await queries.ProcessAsync(new GetChannelByIdQuery(item.ToPeer.PeerId), cancellationToken);
            if (!EligibleChannel(channel, item.SenderUserId, classifier.MinimumParticipants)) return;
            await RestoreAsync(item.ToPeer.PeerId, item.SenderUserId, editDate, cancellationToken);
            var verdict = tracker.Edit(item.ToPeer.PeerId, item.SenderUserId, item.MessageId, item.Date, editDate, keys);
            await PersistAsync(item, keys, cancellationToken);
            if (verdict != null)
                await EnqueueBurstAsync(item, domainEvent.AggregateSequenceNumber, verdict, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not apply local anti-spam edit for {ChannelId}/{MessageId}", item.ToPeer.PeerId, item.MessageId);
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

    /// <summary>A cold window (first message after a restart) is reloaded before it is evaluated.</summary>
    private async Task RestoreAsync(long chat, long sender, long now, CancellationToken ct)
    {
        if (tracker.IsTracked(chat, sender)) return;
        try
        {
            tracker.Seed(chat, sender, now, await window.LoadAsync(chat, sender, now, ct));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Detection continues from memory; durability only protects bursts across restarts.
            logger.LogWarning(e, "Could not restore the anti-spam window of {ChannelId}/{SenderId}", chat, sender);
        }
    }

    private async Task PersistAsync(MessageItem item, AntiSpamKeys? keys, CancellationToken ct)
    {
        try
        {
            if (keys is { } k)
                await window.SaveAsync(item.ToPeer.PeerId, item.SenderUserId, item.MessageId, item.Date, k, ct);
            else
                await window.RemoveAsync(item.ToPeer.PeerId, item.MessageId, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Could not persist the anti-spam window entry {ChannelId}/{MessageId}", item.ToPeer.PeerId, item.MessageId);
        }
    }

    private string Text(MessageItem item) => item.EncryptedData is { } encrypted
        ? converter.DecryptMessage(item.OwnerPeer.PeerId, item.MessageId, encrypted)
        : item.Message;

    /// <summary>
    /// Queues the triggering message and, with DeleteBurst, every earlier matching message of the window.
    /// Earlier members are re-read and fenced at their current aggregate version; the worker rechecks all.
    /// </summary>
    private async Task EnqueueBurstAsync(MessageItem item, int version, AntiSpamVerdict verdict, CancellationToken ct)
    {
        var channelId = item.ToPeer.PeerId;
        var sender = await queries.ProcessAsync(new GetUserByIdQuery(item.SenderUserId), ct);
        if (sender == null || sender.Bot) return;
        var enqueued = new List<int>();
        var known = new List<int> { item.MessageId };
        if (await EnqueueAsync(channelId, item.MessageId, item.SenderUserId,
                converter.ToMessage(MyTelegramConsts.AntiSpamBotUserId, item), version, verdict, ct))
            enqueued.Add(item.MessageId);

        var others = options.Value.DeleteBurst ? verdict.NewMembers.Where(id => id != item.MessageId).ToList() : [];
        if (others.Count != 0)
        {
            var ids = others.Select(id => MessageId.Create(channelId, id).Value).ToList();
            var models = await queries.ProcessAsync(new GetMessagesByIdListQuery(ids), ct);
            foreach (var model in models)
            {
                if (model.SenderUserId != item.SenderUserId || model.OwnerPeerId != channelId) continue;
                var text = model.EncryptedData is { } encrypted
                    ? converter.DecryptMessage(channelId, model.MessageId, encrypted)
                    : model.Message;
                if (classifier.Analyze(text, model.Entities2)?.Hex(verdict.Mode) != verdict.Key) continue;
                var aggregate = await aggregates.LoadAsync<MessageAggregate, MessageId>(
                    MessageId.Create(channelId, model.MessageId), ct);
                known.Add(model.MessageId);
                if (aggregate.IsChannelDeleted || aggregate.AntiSpamDeletionCancelledRequestId.HasValue) continue;
                if (await EnqueueAsync(channelId, model.MessageId, item.SenderUserId,
                        converter.ToMessage(MyTelegramConsts.AntiSpamBotUserId, model), aggregate.Version, verdict, ct))
                    enqueued.Add(model.MessageId);
            }
        }
        // A job that already exists (or a definitive fence) needs no reload on the next trigger.
        tracker.MarkEnqueued(channelId, item.SenderUserId, known);
        if (enqueued.Count == 0) return;
        AntiSpamMetrics.MessagesEnqueued(enqueued.Count);
        if (verdict.NewBurst) AntiSpamMetrics.Triggered(verdict.Mode);
        logger.LogInformation("Anti-spam {Mode} burst {BurstId} in {ChannelId} from {SenderId}: queued {MessageIds}",
            verdict.Mode, verdict.BurstId, channelId, item.SenderUserId, enqueued);
    }

    private Task<bool> EnqueueAsync(long channelId, int messageId, long sender, IMessage message, int version,
        AntiSpamVerdict verdict, CancellationToken ct)
    {
        var original = new ArrayBufferWriter<byte>();
        message.Serialize(original);
        var action = new ArrayBufferWriter<byte>();
        new TChannelAdminLogEventActionDeleteMessage { Message = message }.Serialize(action);
        return store.EnqueueOrRefenceAsync(channelId, sender, messageId, verdict.Key,
            original.WrittenSpan.ToArray(), action.WrittenSpan.ToArray(), version, verdict.Mode, verdict.BurstId, ct);
    }
}
