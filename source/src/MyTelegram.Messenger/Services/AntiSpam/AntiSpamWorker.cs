using Microsoft.Extensions.Hosting;
using MongoDB.Bson;

namespace MyTelegram.Messenger.Services.AntiSpam;

public sealed class AntiSpamWorker(AntiSpamJobStore store, AntiSpamClassifier classifier,
    IAntiSpamActorService actor, IQueryProcessor queries, IAggregateStore aggregates, ICommandBus commands,
    IOptions<AntiSpamOptions> options, ILogger<AntiSpamWorker> logger, IMessageConverterService converter,
    AntiSpamCompletionService completion, AntiSpamDeletionEffectsService effects,
    AntiSpamEffectProofReader proofReader, AntiSpamMentionAdmissionService admissions) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await store.InitializeAsync(stoppingToken);
                await proofReader.InitializeAsync(stoppingToken);
                break;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Anti-spam storage initialization failed");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await store.ClaimAsync(stoppingToken);
                if (job != null) await ProcessAsync(job, stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The lease expires durably. Bounded retries never infer success from a missing message.
                logger.LogError(e, "Anti-spam job failed; it will be retried after its lease expires");
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(options.Value.PollSeconds, 1, 30)), stoppingToken);
        }
    }

    public async Task ProcessAsync(BsonDocument job, CancellationToken ct)
    {
        if (job["state"] == "confirmed")
        {
            await completion.CompleteAsync(job, ct);
            return;
        }
        var channelId = job["channel"].ToInt64();
        var messageId = job["message"].AsInt32;
        var requestId = Guid.Parse(job["request"].AsString);
        // Durable event replay repairs the commit-before-subscriber crash gap.
        var aggregate = await aggregates.LoadAsync<MessageAggregate, MessageId>(MessageId.Create(channelId, messageId), ct);
        if (aggregate.IsChannelDeleted)
        {
            if (aggregate.ChannelDeletionRequestId == requestId)
                await store.ConfirmAsync(channelId, messageId, requestId, ct);
            else
                await AbortAsync(job, ct);
            return;
        }
        if (job["state"] == "aborting" || job["state"] == "recovery" ||
            job["attempts"].AsInt32 >= store.MaxAttempts || aggregate.AntiSpamDeletionCancelledRequestId.HasValue)
        {
            await AbortAsync(job, ct);
            return;
        }
        // Legacy in-flight jobs have no reservation provenance. Definitively cancel before
        // permitting a scalar recount; if deletion already won, normal confirmation wins instead.
        if (job.GetValue("dispatched", false).ToBoolean() && !job.Contains("admission_protocol"))
        {
            await AbortAsync(job, ct);
            return;
        }
        var expectedVersion = job.GetValue("aggregate_version", BsonNull.Value);
        if (expectedVersion.IsBsonNull || aggregate.Version != expectedVersion.AsInt32)
        {
            await AbortAsync(job, ct);
            return;
        }
        var channel = await queries.ProcessAsync(new GetChannelByIdQuery(channelId), ct);
        var sender = job["sender"].ToInt64();
        if (!options.Value.Enabled || !AntiSpamSubscriber.EligibleChannel(channel, sender))
        {
            await AbortAsync(job, ct);
            return;
        }
        ReadOnlyMemory<byte> originalBytes = job["original_message"].AsBsonBinaryData.Bytes;
        var original = originalBytes.Read<IMessage>() as TMessage;
        var message = await queries.ProcessAsync(new GetMessageByIdQuery(MessageId.Create(channelId, messageId).Value), ct);
        var text = message?.EncryptedData is { } encrypted
            ? converter.DecryptMessage(channelId, messageId, encrypted)
            : message?.Message;
        if (original == null || message == null || text != original.Message || message.SenderUserId != sender || message.SenderPeerId != sender ||
            message.Post || message.EditDate != null || message.SendAs != null || message.FwdHeader != null || message.MessageAction != null ||
            message.SendMessageType != SendMessageType.Text ||
            classifier.Fingerprint(text) != job["fingerprint"].AsString)
        {
            await AbortAsync(job, ct);
            return;
        }
        if (!job.Contains("effects") && !await store.SaveEffectsAsync(job, await effects.CaptureAsync(message, ct), ct))
            return;
        if (!await admissions.AcquireAsync(job, ct))
        {
            // Abort (including partial acquisition) is fenced before closing reservations.
            // A lost lease cannot authorize abort; the new owner will recover the same job.
            await AbortAsync(job, ct);
            return;
        }
        await actor.EnsureAvailableAsync(ct);
        var top = await queries.ProcessAsync(new GetTopMessageIdQuery(channelId, [messageId]), ct);
        var request = RequestInfo.Empty with { UserId = MyTelegramConsts.AntiSpamBotUserId, ReqMsgId = 0,
            RequestId = requestId, Date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), AddRequestIdToCache = false };
        if (!await store.TryBeginDispatchAsync(job, ct)) return;
        job["dispatched"] = true;
        await commands.PublishAsync(new DeleteChannelMessageCommand(MessageId.Create(channelId, messageId),
            request, expectedVersion.AsInt32), ct);
        // Do not mark success here: only the persisted message-deletion event authorizes the log.
        await store.FinishAsync(job, "leased", ct);
    }

    private async Task AbortAsync(BsonDocument job, CancellationToken ct)
    {
        if (!await store.BeginAbortAsync(job, ct)) return;
        var id = MessageId.Create(job["channel"].ToInt64(), job["message"].AsInt32);
        var requestId = Guid.Parse(job["request"].AsString);
        await commands.PublishAsync(new CancelAntiSpamDeletionCommand(id, requestId), ct);
        var message = await aggregates.LoadAsync<MessageAggregate, MessageId>(id, ct);
        if (message.IsChannelDeleted && message.ChannelDeletionRequestId == requestId)
        {
            await store.ConfirmAsync(job["channel"].ToInt64(), job["message"].AsInt32, requestId, ct);
            return;
        }
        if (!message.IsChannelDeleted && !message.AntiSpamDeletionCancelledRequestId.HasValue) return;
        await admissions.CloseAsync(job, ct);
        await store.CompleteAbortAsync(job, ct);
    }
}
