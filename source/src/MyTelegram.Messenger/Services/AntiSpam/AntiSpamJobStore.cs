using MongoDB.Bson;
using MongoDB.Driver;
using Microsoft.Extensions.Options;

namespace MyTelegram.Messenger.Services.AntiSpam;

/// <summary>CAS-leased deletion jobs and confirmed-only idempotent admin logs.</summary>
public sealed class AntiSpamJobStore(IMongoDatabase database, IOptions<AntiSpamOptions> options)
{
    private IMongoCollection<BsonDocument> Jobs => database.GetCollection<BsonDocument>("antispam_jobs")
        .WithWriteConcern(WriteConcern.WMajority);
    public static string JobId(long channelId, int messageId) => $"antispam-{channelId}-{messageId}";
    public int MaxAttempts => Math.Clamp(options.Value.MaxAttempts, 1, 20);

    public async Task InitializeAsync(CancellationToken ct)
    {
        await Jobs.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("expires"),
            new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }), cancellationToken: ct);
        await Jobs.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("state").Ascending("due")), cancellationToken: ct);
    }

    public Task EnqueueAsync(long channel, long sender, int message, string fingerprint, byte[] originalMessage,
        byte[] action, CancellationToken ct, int? expectedAggregateVersion = null, string? mode = null, string? burst = null) =>
        InsertOnceAsync(Jobs, NewJob(channel, sender, message, fingerprint, originalMessage, action, expectedAggregateVersion, mode, burst), ct);

    /// <summary>
    /// Queues a deletion, or re-fences a queued one at a new aggregate version after an edit that still
    /// matches. Captured effects describe the old version, so they are dropped and recaptured. Rotating the
    /// lease makes a worker mid-way through the old version lose every later CAS step (effects, admission,
    /// dispatch, abort). Once mention admission or dispatch began, the existing fencing protocol owns the
    /// job and it is left as is (false); the worker then cancels on the version mismatch.
    /// </summary>
    public async Task<bool> EnqueueOrRefenceAsync(long channel, long sender, int message, string fingerprint,
        byte[] originalMessage, byte[] action, int aggregateVersion, string mode, string burst, CancellationToken ct)
    {
        try
        {
            await Jobs.InsertOneAsync(NewJob(channel, sender, message, fingerprint, originalMessage, action,
                aggregateVersion, mode, burst), cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey) { }
        var result = await Jobs.UpdateOneAsync(new BsonDocument { ["_id"] = JobId(channel, message), ["sender"] = sender,
                ["state"] = "pending", ["aggregate_version"] = new BsonDocument("$lt", aggregateVersion),
                ["dispatched"] = new BsonDocument("$ne", true), ["admission_protocol"] = new BsonDocument("$exists", false) },
            new BsonDocument { ["$set"] = new BsonDocument { ["aggregate_version"] = aggregateVersion,
                ["fingerprint"] = fingerprint, ["mode"] = mode, ["original_message"] = originalMessage, ["action"] = action,
                ["lease"] = Guid.NewGuid().ToString(), ["due"] = DateTime.UtcNow.AddSeconds(Math.Clamp(options.Value.DelaySeconds, 2, 60)) },
                ["$unset"] = new BsonDocument("effects", 1) },
            cancellationToken: ct);
        return result.ModifiedCount == 1;
    }

    private BsonDocument NewJob(long channel, long sender, int message, string fingerprint, byte[] originalMessage,
        byte[] action, int? expectedAggregateVersion, string? mode, string? burst)
    {
        var job = new BsonDocument
        {
            ["_id"] = JobId(channel, message), ["channel"] = channel, ["sender"] = sender, ["message"] = message,
            ["fingerprint"] = fingerprint, ["original_message"] = originalMessage, ["action"] = action,
            ["aggregate_version"] = expectedAggregateVersion.HasValue ? new BsonInt32(expectedAggregateVersion.Value) : BsonNull.Value,
            ["request"] = Guid.NewGuid().ToString(), ["state"] = "pending", ["attempts"] = 0,
            ["due"] = DateTime.UtcNow.AddSeconds(Math.Clamp(options.Value.DelaySeconds, 2, 60))
        };
        if (mode != null) job["mode"] = mode;
        if (burst != null) job["burst"] = burst;
        return job;
    }

    private static async Task InsertOnceAsync(IMongoCollection<BsonDocument> collection, BsonDocument doc, CancellationToken ct)
    {
        try { await collection.InsertOneAsync(doc, cancellationToken: ct); }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey) { }
    }

    public Task<BsonDocument?> ClaimAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return Jobs.FindOneAndUpdateAsync<BsonDocument>(new BsonDocument {
            ["state"] = new BsonDocument("$in", new BsonArray { "pending", "leased", "confirmed", "recovery", "aborting" }),
            ["due"] = new BsonDocument("$lte", now) },
            new BsonDocument { ["$set"] = new BsonDocument { ["lease"] = Guid.NewGuid().ToString(),
                ["due"] = now.AddSeconds(Math.Clamp(options.Value.LeaseSeconds, 30, 600)) } },
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After, Sort = new BsonDocument("due", 1) }, ct)!;
    }

    public async Task<bool> SaveEffectsAsync(BsonDocument job, BsonDocument effects, CancellationToken ct)
    {
        var result = await Jobs.UpdateOneAsync(new BsonDocument { ["_id"] = job["_id"], ["lease"] = job["lease"],
                ["request"] = job["request"], ["effects"] = new BsonDocument("$exists", false),
                ["dispatched"] = new BsonDocument("$ne", true) },
            new BsonDocument("$set", new BsonDocument("effects", effects)), cancellationToken: ct);
        if (result.ModifiedCount == 1) { job["effects"] = effects; return true; }
        var saved = await Jobs.Find(new BsonDocument { ["_id"] = job["_id"], ["lease"] = job["lease"] }).FirstOrDefaultAsync(ct);
        if (saved == null || !saved.Contains("effects")) return false;
        job["effects"] = saved["effects"];
        return true;
    }

    // Authorize only AFTER the caller captured the dialog version/proof. A durable abort
    // changes state before closing dialogs, fencing every previously authorized admission.
    public async Task<bool> AuthorizeAdmissionAsync(BsonDocument job, CancellationToken ct)
    {
        var result = await Jobs.UpdateOneAsync(new BsonDocument { ["_id"] = job["_id"],
                ["request"] = job["request"], ["lease"] = job["lease"],
                ["state"] = new BsonDocument("$in", new BsonArray { "pending", "leased" }),
                ["due"] = new BsonDocument("$gt", DateTime.UtcNow) },
            new BsonDocument("$set", new BsonDocument("admission_protocol", 1)), cancellationToken: ct);
        if (result.MatchedCount == 1) job["admission_protocol"] = 1;
        return result.MatchedCount == 1;
    }

    public async Task<bool> BeginAbortAsync(BsonDocument job, CancellationToken ct)
    {
        var result = await Jobs.UpdateOneAsync(new BsonDocument { ["_id"] = job["_id"],
                ["request"] = job["request"], ["lease"] = job["lease"],
                ["state"] = new BsonDocument("$nin", new BsonArray { "confirmed", "done", "cancelled" }) },
            new BsonDocument { ["$set"] = new BsonDocument("state", "aborting"),
                ["$unset"] = new BsonDocument("expires", 1) }, cancellationToken: ct);
        if (result.MatchedCount == 1) job["state"] = "aborting";
        return result.MatchedCount == 1;
    }

    public Task CompleteAbortAsync(BsonDocument job, CancellationToken ct) => Jobs.UpdateOneAsync(
        new BsonDocument { ["_id"] = job["_id"], ["request"] = job["request"],
            ["lease"] = job["lease"], ["state"] = "aborting" },
        new BsonDocument("$set", new BsonDocument { ["state"] = "cancelled",
            ["expires"] = DateTime.UtcNow.AddDays(30) }), cancellationToken: ct);

    public async Task<bool> TryBeginDispatchAsync(BsonDocument job, CancellationToken ct)
    {
        var result = await Jobs.UpdateOneAsync(new BsonDocument { ["_id"] = job["_id"], ["lease"] = job["lease"],
                ["state"] = job["state"], ["attempts"] = new BsonDocument("$lt", MaxAttempts),
                ["due"] = new BsonDocument("$gt", DateTime.UtcNow) },
            new BsonDocument { ["$inc"] = new BsonDocument("attempts", 1),
                ["$set"] = new BsonDocument("dispatched", true) }, cancellationToken: ct);
        return result.ModifiedCount == 1;
    }

    public Task FinishAsync(BsonDocument job, string state, CancellationToken ct)
    {
        // A previously dispatched intent remains recoverable even if eligibility changes while its
        // saga is in flight. Never expire its evidence before a definitive outcome is observed.
        if (state == "cancelled" && job.GetValue("dispatched", false).ToBoolean()) state = "recovery";
        var fields = new BsonDocument { ["state"] = state,
            ["due"] = DateTime.UtcNow.AddSeconds(state == "recovery" ? 300 : Math.Clamp(options.Value.LeaseSeconds, 30, 600)) };
        if (state is "done" or "cancelled") fields["expires"] = DateTime.UtcNow.AddDays(30);
        return Jobs.UpdateOneAsync(new BsonDocument { ["_id"] = job["_id"], ["lease"] = job["lease"], ["state"] = job["state"] },
            new BsonDocument("$set", fields), cancellationToken: ct);
    }

    public Task ConfirmAsync(long channel, int message, Guid requestId, CancellationToken ct) => Jobs.UpdateOneAsync(
        new BsonDocument { ["_id"] = JobId(channel, message), ["request"] = requestId.ToString(),
            ["state"] = new BsonDocument("$nin", new BsonArray { "done", "confirmed" }) },
        new BsonDocument { ["$set"] = new BsonDocument { ["state"] = "confirmed",
            ["confirmed_date"] = DateTime.UtcNow, ["due"] = DateTime.UtcNow.AddSeconds(2) },
            ["$unset"] = new BsonDocument("expires", 1) }, cancellationToken: ct);

    public async Task<BsonDocument> AssignCompletionAsync(BsonDocument job, int pts, long globalSeqNo, CancellationToken ct, int newTopMessageId = 0)
    {
        var completion = new BsonDocument { ["pts"] = pts, ["global_seq"] = globalSeqNo,
            ["date"] = checked((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ["new_top"] = newTopMessageId };
        await Jobs.UpdateOneAsync(new BsonDocument { ["_id"] = job["_id"], ["request"] = job["request"],
                ["state"] = "confirmed", ["completion"] = new BsonDocument("$exists", false) },
            new BsonDocument("$set", new BsonDocument("completion", completion)), cancellationToken: ct);
        var saved = await Jobs.Find(new BsonDocument("_id", job["_id"])).SingleAsync(ct);
        if (!saved.Contains("completion")) throw new InvalidOperationException("Completion is not confirmed");
        return saved["completion"].AsBsonDocument;
    }

    public Task RetrySoonAsync(BsonDocument job, CancellationToken ct) => Jobs.UpdateOneAsync(
        new BsonDocument { ["_id"] = job["_id"], ["lease"] = job["lease"], ["state"] = "confirmed" },
        new BsonDocument("$set", new BsonDocument("due", DateTime.UtcNow.AddSeconds(Math.Clamp(options.Value.PollSeconds, 1, 30)))),
        cancellationToken: ct);

    public Task MarkTransportAcceptedAsync(BsonDocument job, CancellationToken ct) => Jobs.UpdateOneAsync(
        new BsonDocument { ["_id"] = job["_id"], ["request"] = job["request"], ["state"] = "confirmed" },
        new BsonDocument("$set", new BsonDocument("transport_accepted", true)), cancellationToken: ct);

    public async Task WriteConfirmedLogAsync(BsonDocument job, CancellationToken ct)
    {
        if (job["state"] != "confirmed") throw new InvalidOperationException("Deletion is not confirmed");
        var channel = job["channel"].ToInt64();
        ReadOnlyMemory<byte> bytes = job["action"].AsBsonBinaryData.Bytes;
        var action = bytes.Read<IChannelAdminLogEventAction>();
        // Like Telegram, every antispam deletion is its own channelAdminLogEventActionDeleteMessage, so each
        // deleted message of a burst can be reviewed and reported with reportAntiSpamFalsePositive.
        var counter = await database.GetCollection<BsonDocument>("counters").FindOneAndUpdateAsync(
            new BsonDocument("_id", $"adminlog_event_id_{channel}"), new BsonDocument("$inc", new BsonDocument("seq", 1L)),
            new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After }, ct);
        var doc = new BsonDocument { ["event_id"] = counter["seq"].ToInt64(), ["channel_id"] = channel,
            ["user_id"] = MyTelegramConsts.AntiSpamBotUserId, ["date"] = job["confirmed_date"],
            ["filters"] = new BsonArray(AdminLogMetadata.Filters(action)), ["search_text"] = AdminLogMetadata.SearchText(action),
            ["related_user_ids"] = new BsonArray(AdminLogMetadata.RelatedUserIds(action)),
            ["related_channel_ids"] = new BsonArray(AdminLogMetadata.RelatedChannelIds(action)), ["message_id"] = job["message"], ["antispam_job_id"] = job["_id"],
            ["action"] = new BsonDocument { ["type"] = nameof(TChannelAdminLogEventActionDeleteMessage), ["data"] = job["action"] } };
        await database.GetCollection<BsonDocument>(AdminLogCollection.Name).UpdateOneAsync(new BsonDocument("_id", job["_id"]),
            new BsonDocument("$setOnInsert", doc), new UpdateOptions { IsUpsert = true }, ct);
        await FinishAsync(job, "done", ct);
    }
}
