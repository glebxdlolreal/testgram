using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.AntiSpam;

namespace MyTelegram.Messenger.Services.Mentions;

/// <summary>Durable read effects: gate, counter receipt, external marker, then gate release.</summary>
public interface IMentionReadIntentService
{
    Task ReadAsync(long userId, Peer peer, IReadOnlyCollection<int> messageIds, CancellationToken ct = default);
    Task ReadAllAsync(long userId, Peer peer, int readMaxId, CancellationToken ct = default);
}

public sealed class MentionReadIntentService(IMongoDatabase database, IAggregateStore aggregates,
    ICommandBus commands, IAntiSpamEffectProofReader proofs, IMentionReadStateService readState,
    IQueryProcessor queries, AntiSpamMentionRecountService recounts,
    ILogger<MentionReadIntentService> logger) : BackgroundService, IMentionReadIntentService
{
    private IMongoCollection<BsonDocument> Intents => database.GetCollection<BsonDocument>("mention_read_intents")
        .WithReadPreference(ReadPreference.Primary).WithReadConcern(ReadConcern.Majority)
        .WithWriteConcern(WriteConcern.WMajority);

    public async Task ReadAsync(long userId, Peer peer, IReadOnlyCollection<int> messageIds,
        CancellationToken ct = default)
    {
        foreach (var batch in messageIds.Where(x => x > 0).Distinct().Chunk(100))
            await WaitAsync(await EnqueueAsync(userId, peer, batch, "ids", 0, ct), ct);
    }

    public async Task ReadAllAsync(long userId, Peer peer, int readMaxId, CancellationToken ct = default)
        => await WaitAsync(await EnqueueAsync(userId, peer, [], "all", Math.Max(0, readMaxId), ct), ct);

    private async Task<string> EnqueueAsync(long userId, Peer peer, int[] ids, string mode, int maxId,
        CancellationToken ct)
    {
        var operation = Guid.NewGuid().ToString("N");
        await Intents.InsertOneAsync(new BsonDocument { ["_id"] = operation, ["user"] = userId,
            ["peer"] = peer.PeerId, ["peer_type"] = (int)peer.PeerType,
            ["ids"] = new BsonArray(ids), ["mode"] = mode, ["max_id"] = maxId,
            ["state"] = "pending", ["due"] = DateTime.UtcNow }, cancellationToken: ct);
        return operation;
    }

    private async Task WaitAsync(string operation, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(8))
        {
            ct.ThrowIfCancellationRequested();
            var job = await Intents.Find(new BsonDocument("_id", operation)).SingleAsync(ct);
            if (job["state"] == "done") return;
            try { await ProcessAsync(job, ct); }
            catch (InvalidOperationException e)
            {
                // Incomplete indexed proof coverage is retryable, never absence of consumption.
                logger.LogDebug(e, "Read intent {Operation} awaits committed proof", operation);
            }
            await Task.Delay(100, ct);
        }
        // The durable intent remains queued. Never tell the client an unfinished read succeeded.
        RpcErrors.RpcErrors_503.Timeout.ThrowRpcError();
    }

    public async Task<bool> ProcessAsync(BsonDocument job, CancellationToken ct)
    {
        var operation = job["_id"].AsString;
        var current = await Intents.Find(new BsonDocument("_id", operation)).SingleAsync(ct);
        if (current["state"] == "done") return true;
        job = current;
        var user = job["user"].ToInt64();
        var peer = new Peer((PeerType)job["peer_type"].AsInt32, job["peer"].ToInt64());
        if (job["mode"] == "all")
        {
            // The root intent persists the whole read-all request across crashes. Each child is
            // bounded; a crash may enqueue a duplicate child, whose per-message proof is idempotent.
            var state = await readState.GetAsync(user, peer);
            var maxId = job["max_id"].AsInt32;
            var page = await queries.ProcessAsync(new GetMessagesWithUnreadMentionsQuery(
                peer.PeerType == PeerType.Channel ? peer.PeerId : user, user, peer, null,
                state?.ReadMaxId ?? 0, state?.ReadIds ?? [], 0, 0, 100,
                maxId == int.MaxValue ? 0 : maxId + 1, 0), ct);
            var ids = page.Select(x => x.MessageId).Where(x => x <= maxId).Distinct().ToArray();
            if (ids.Length > 0)
            {
                var child = await EnqueueAsync(user, peer, ids, "ids", 0, ct);
                await ProcessAsync(await Intents.Find(new BsonDocument("_id", child)).SingleAsync(ct), ct);
                return false;
            }
            await Intents.UpdateOneAsync(new BsonDocument { ["_id"] = operation, ["mode"] = "all" },
                new BsonDocument("$set", new BsonDocument("mode", "watermark")), cancellationToken: ct);
            job["mode"] = "watermark";
        }

        var id = DialogId.Create(user, peer);
        var dialog = await aggregates.LoadAsync<DialogAggregate, DialogId>(id, ct);
        if (job["state"] == "pending")
        {
            if (!dialog.IsNew && dialog.MentionReadOperation != operation)
            {
                if (dialog.MentionReadOperation != null) return false;
                var version = dialog.Version;
                // Version capture MUST precede pending authorization. Completing closes admission.
                var authorized = await Intents.UpdateOneAsync(new BsonDocument { ["_id"] = operation,
                    ["state"] = "pending" }, new BsonDocument("$set",
                    new BsonDocument("authorized_at", DateTime.UtcNow)), cancellationToken: ct);
                if (authorized.MatchedCount != 1) return false;
                await commands.PublishAsync(new TryAdmitMentionReadIntentCommand(id, operation, version), ct);
                dialog = await aggregates.LoadAsync<DialogAggregate, DialogId>(id, ct);
                if (dialog.MentionReadOperation != operation) return false;
            }
            if (!dialog.IsNew)
            {
                await commands.PublishAsync(new ApplyMentionReadIntentCommand(id, operation,
                    job["ids"].AsBsonArray.Select(x => x.AsInt32).ToList(), false), ct);
                dialog = await aggregates.LoadAsync<DialogAggregate, DialogId>(id, ct);
                if (await proofs.FindReceiptAsync(id.Value, operation, AntiSpamEffectKind.MentionReadIntent,
                        dialog.Version, ct) == null) return false;
            }
            if (job["mode"] == "watermark")
                await readState.MarkAllReadAsync(user, peer, job["max_id"].AsInt32);
            else
                await readState.MarkReadAsync(user, peer, job["ids"].AsBsonArray.Select(x => x.AsInt32).ToArray());
            // Queue before closing so even a crash after release leaves durable reconciliation.
            await recounts.EnqueueAsync(user, peer, ct);
            await Intents.UpdateOneAsync(new BsonDocument { ["_id"] = operation, ["state"] = "pending" },
                new BsonDocument("$set", new BsonDocument("state", "completing")), cancellationToken: ct);
        }
        // No marker writes here: a delayed pending processor cannot start mutating after close.
        await commands.PublishAsync(new ReleaseMentionReadIntentCommand(id, operation), ct);
        await Intents.UpdateOneAsync(new BsonDocument { ["_id"] = operation, ["state"] = "completing" },
            new BsonDocument("$set", new BsonDocument("state", "done")), cancellationToken: ct);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var initialized = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!initialized)
                {
                    await Intents.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                        Builders<BsonDocument>.IndexKeys.Ascending("state").Ascending("due")),
                        cancellationToken: stoppingToken);
                    initialized = true;
                }
                var job = await Intents.FindOneAndUpdateAsync<BsonDocument>(new BsonDocument {
                        ["state"] = new BsonDocument("$ne", "done"),
                        ["due"] = new BsonDocument("$lte", DateTime.UtcNow) },
                    new BsonDocument("$set", new BsonDocument("due", DateTime.UtcNow.AddSeconds(10))),
                    new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, stoppingToken);
                if (job != null) await ProcessAsync(job, stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Mention read intent remains queued for recovery");
            }
            await Task.Delay(200, stoppingToken);
        }
    }
}
