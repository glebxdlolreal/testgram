using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Pts;

/// <summary>
/// Standalone MongoDB-safe Pts allocation. A counter contains at most one pending operation;
/// any writer completes its immutable journal handoff before another reservation can advance it.
/// Collections must not be independently restored, edited, or expired.
/// </summary>
public sealed class DurableChannelPtsAllocator : IChannelPtsAllocator
{
    public const string CounterCollection = "channel_pts_counters";
    public const string JournalCollection = "channel_pts_operations";
    public const string FenceCollection = "channel_pts_readiness";
    private const string FenceId = "hilo-cutover-v1";
    private readonly IMongoDatabase _database;
    private readonly IMongoCollection<BsonDocument> _counters;
    private readonly IMongoCollection<BsonDocument> _journal;
    private readonly IMongoCollection<BsonDocument> _fences;
    private readonly Func<string, Task>? _checkpoint;

    public DurableChannelPtsAllocator(IMongoDatabase database) : this(database, null) { }

    // Fault checkpoints are also useful to verify process-restart recovery against real MongoDB.
    public DurableChannelPtsAllocator(IMongoDatabase database, Func<string, Task>? checkpoint)
    {
        _database = database;
        _counters = Collection(database, CounterCollection);
        _journal = Collection(database, JournalCollection);
        _fences = Collection(database, FenceCollection);
        _checkpoint = checkpoint;
    }

    private static IMongoCollection<BsonDocument> Collection(IMongoDatabase db, string name) =>
        db.GetCollection<BsonDocument>(name).WithReadPreference(ReadPreference.Primary)
            .WithReadConcern(ReadConcern.Majority)
            .WithWriteConcern(WriteConcern.WMajority.With(journal: true));

    public async Task<int> ReserveAsync(long channelId, string operationId, int count = 1,
        CancellationToken cancellationToken = default) =>
        (await ReserveCoreAsync(channelId, operationId, count, false, false, cancellationToken))!.Value;

    public async Task<int> ReserveForPublicationAsync(long channelId, string operationId, int count = 1,
        CancellationToken cancellationToken = default) =>
        (await ReserveCoreAsync(channelId, operationId, count, true, false, cancellationToken))!.Value;

    public Task<int?> TryReserveForPublicationAsync(long channelId, string operationId, int count = 1,
        CancellationToken cancellationToken = default) => ReserveCoreAsync(channelId, operationId, count, true, true, cancellationToken);

    public async Task<int?> GetReadablePtsCeilingAsync(long channelId, CancellationToken cancellationToken = default)
    {
        var state = await _counters.Find(new BsonDocument("_id", channelId)).FirstOrDefaultAsync(cancellationToken);
        return state != null && state.TryGetValue("Publication", out var publication)
            ? publication.AsBsonDocument["PriorPts"].AsInt32 : null;
    }

    public async Task MarkPublishedAsync(long channelId, string operationId, int pts, CancellationToken cancellationToken = default)
    {
        var key = $"{channelId}:{operationId}";
        var entry = await _journal.Find(new BsonDocument("_id", key)).FirstOrDefaultAsync(cancellationToken);
        if (entry == null || entry["Pts"].AsInt32 != pts || !entry.GetValue("Publication", false).AsBoolean)
            throw new InvalidOperationException("Publication acknowledgement does not match a journaled publication reservation.");
        await CheckpointAsync("before-publication-ack");
        await _counters.UpdateOneAsync(new BsonDocument { { "_id", channelId }, { "Publication._id", key },
            { "Publication.Pts", pts } }, new BsonDocument("$unset", new BsonDocument("Publication", "")), cancellationToken: cancellationToken);
        await CheckpointAsync("after-publication-ack");
    }

    private async Task<int?> ReserveCoreAsync(long channelId, string operationId, int count, bool publicationRequired,
        bool deferIfBlocked, CancellationToken cancellationToken)
    {
        if (channelId < MyTelegramConsts.ChannelInitId) throw new ArgumentOutOfRangeException(nameof(channelId));
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 256)
            throw new ArgumentException("A stable operation ID of at most 256 characters is required.", nameof(operationId));
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        var fence = await _fences.Find(new BsonDocument("_id", FenceId)).FirstOrDefaultAsync(cancellationToken);
        if (fence == null)
            throw new InvalidOperationException("Channel Pts cutover is not ready. Quiesce all legacy writers and explicitly seed the complete HiLo inventory before enabling this authority.");

        var key = $"{channelId}:{operationId}";
        var channelFilter = new BsonDocument("_id", channelId);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = await _counters.Find(channelFilter).FirstOrDefaultAsync(cancellationToken);
            if (state == null)
            {
                // The fence certifies that every pre-cutover channel was seeded. Only new channels reach here.
                await InsertIfAbsentAsync(_counters, new BsonDocument { { "_id", channelId }, { "Pts", MyTelegramConsts.PtsInitId } }, cancellationToken);
                continue;
            }
            if (state.TryGetValue("Pending", out var pending))
            {
                await RecoverAsync(channelFilter, pending.AsBsonDocument, cancellationToken);
                continue;
            }

            // Read AFTER the counter snapshot. A racing reservation/journal/clear changes Pts and
            // invalidates our CAS, so an operation cannot get a second reservation after handoff.
            var existing = await _journal.Find(new BsonDocument("_id", key)).FirstOrDefaultAsync(cancellationToken);
            if (existing != null)
            {
                if (existing["Count"].AsInt32 != count || existing.GetValue("Publication", false).AsBoolean != publicationRequired)
                    throw new InvalidOperationException("An operation ID cannot be reused with a different Pts count or publication mode.");
                return existing["Pts"].AsInt32;
            }
            if (state.Contains("Publication"))
            {
                // Completion workers must yield their slot to the barrier owner rather than wait.
                if (deferIfBlocked) return null;
                // Do not turn an ordinary saga allocation into a terminal failure. Wait until the
                // owning durable completion reconciles; cancellation never removes the barrier.
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                continue;
            }
            var oldPts = state["Pts"].ToInt64();
            var next = checked((int)checked(oldPts + count));
            var reservation = new BsonDocument { { "_id", key }, { "Pts", next }, { "Count", count }, { "Publication", publicationRequired } };
            var cas = new BsonDocument { { "_id", channelId }, { "Pts", oldPts }, { "Pending", new BsonDocument("$exists", false) },
                { "Publication", new BsonDocument("$exists", false) } };
            var changes = new BsonDocument { { "Pts", next }, { "Pending", reservation } };
            if (publicationRequired)
                changes.Add("Publication", new BsonDocument { { "_id", key }, { "Pts", next }, { "PriorPts", checked((int)oldPts) } });
            await CheckpointAsync("before-reserve");
            var result = await _counters.UpdateOneAsync(cas,
                new BsonDocument("$set", changes), cancellationToken: cancellationToken);
            if (result.ModifiedCount == 0) continue;
            await CheckpointAsync("after-reserve");
            await RecoverAsync(channelFilter, reservation, cancellationToken);
        }
    }

    private async Task RecoverAsync(BsonDocument channelFilter, BsonDocument pending, CancellationToken ct)
    {
        await CheckpointAsync("before-journal");
        await InsertIfAbsentAsync(_journal, pending, ct);
        var recorded = await _journal.Find(new BsonDocument("_id", pending["_id"])).FirstOrDefaultAsync(ct);
        if (recorded == null || recorded["Pts"] != pending["Pts"] || recorded["Count"] != pending["Count"] ||
            recorded.GetValue("Publication", false) != pending.GetValue("Publication", false))
            throw new InvalidOperationException("Channel Pts journal conflicts with its pending reservation.");
        await CheckpointAsync("after-journal");
        var filter = channelFilter.DeepClone().AsBsonDocument;
        filter.Add("Pending._id", pending["_id"]);
        filter.Add("Pts", pending["Pts"]);
        await CheckpointAsync("before-clear");
        await _counters.UpdateOneAsync(filter, new BsonDocument("$unset", new BsonDocument("Pending", "")), cancellationToken: ct);
        await CheckpointAsync("after-clear");
    }

    private Task CheckpointAsync(string name) => _checkpoint?.Invoke(name) ?? Task.CompletedTask;

    private static async Task InsertIfAbsentAsync(IMongoCollection<BsonDocument> collection, BsonDocument document, CancellationToken ct)
    {
        try { await collection.InsertOneAsync(document, cancellationToken: ct); }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey) { }
    }

    /// <summary>
    /// Read-only migration inventory from the actual EventFlow 1.2.3 backing schema:
    /// eventflow.counter {_id: "Pts-{peerId}", seq: high}. Includes orphaned sequences
    /// and channels with no sequence yet, and uses both Pts/channel projections as a floor.
    /// No journal record has a TTL: stable retry evidence must be retained indefinitely.
    /// Ordinary random-ID reservations also grow the journal; archival requires a separately
    /// designed replay-safe protocol, not deletion by age.
    /// </summary>
    public async Task<IReadOnlyCollection<ChannelPtsLegacySeed>> DiscoverLegacyInventoryAsync(
        CancellationToken cancellationToken = default)
    {
        var inventory = new Dictionary<long, ChannelPtsLegacySeed>();
        var sequences = Collection(_database, "eventflow.counter");
        using (var cursor = await sequences.Find(new BsonDocument("_id", new BsonRegularExpression("^Pts-")))
                   .ToCursorAsync(cancellationToken))
        {
            while (await cursor.MoveNextAsync(cancellationToken))
            foreach (var row in cursor.Current)
            {
                var key = row["_id"].AsString;
                if (!long.TryParse(key.AsSpan(4), out var id))
                    throw new InvalidOperationException("Malformed legacy Pts sequence ID.");
                if (id < MyTelegramConsts.ChannelInitId) continue;
                var high = PersistedSequence(row);
                var seed = new ChannelPtsLegacySeed(id, high, 0);
                seed.HighWatermark();
                inventory.Add(id, seed);
            }
        }
        foreach (var (collection, idField) in new[] { ("eventflow-channelreadmodel", "ChannelId"), ("eventflow-ptsreadmodel", "PeerId") })
        {
            using var cursor = await Collection(_database, collection)
                .Find(new BsonDocument(idField, new BsonDocument("$gte", MyTelegramConsts.ChannelInitId)))
                .Project(new BsonDocument { { idField, 1 }, { "Pts", 1 } }).ToCursorAsync(cancellationToken);
            while (await cursor.MoveNextAsync(cancellationToken))
            foreach (var row in cursor.Current)
            {
                var id = ExactNonnegativeInteger(row, idField);
                var observed = row.Contains("Pts") ? ExactNonnegativeInteger(row, "Pts") : 0;
                inventory.TryGetValue(id, out var existing);
                var seed = new ChannelPtsLegacySeed(id, existing?.PersistedHigh ?? 0,
                    Math.Max(existing?.ObservedPts ?? 0, observed));
                seed.HighWatermark();
                inventory[id] = seed;
            }
        }
        return inventory.Values.OrderBy(x => x.ChannelId).ToArray();
    }

    /// <summary>
    /// EventFlow 1.2.3 persists the counter's high value under the lowercase <c>seq</c> field
    /// (<c>{_id: "Pts-{peerId}", seq: high}</c>), and BSON field lookup is case sensitive, so the
    /// uppercase spelling must not be assumed. It is still accepted for a database written by an
    /// older build; a row carrying neither is rejected rather than silently read as zero.
    /// </summary>
    private static long PersistedSequence(BsonDocument row)
    {
        if (row.Contains("seq")) return ExactNonnegativeInteger(row, "seq");
        if (row.Contains("Seq")) return ExactNonnegativeInteger(row, "Seq");
        throw new InvalidOperationException("Legacy sequence row carries no persisted high value.");
    }

    private static long ExactNonnegativeInteger(BsonDocument row, string field)
    {
        if (!row.TryGetValue(field, out var value) || !value.IsNumeric)
            throw new InvalidOperationException($"Legacy {field} must be a nonnegative integer.");
        var number = value.ToDecimal();
        if (number < 0 || number > long.MaxValue || decimal.Truncate(number) != number)
            throw new InvalidOperationException($"Legacy {field} must be a nonnegative integer.");
        return checked((long)number);
    }

    private async Task ValidateInventoryAsync(IReadOnlyCollection<ChannelPtsLegacySeed> supplied, CancellationToken ct)
    {
        var seeds = supplied.ToDictionary(x => x.ChannelId);
        foreach (var actual in await DiscoverLegacyInventoryAsync(ct))
            if (!seeds.TryGetValue(actual.ChannelId, out var seed) || seed.PersistedHigh != actual.PersistedHigh ||
                seed.ObservedPts < actual.ObservedPts)
                throw new InvalidOperationException("Bootstrap inventory omits or understates a legacy channel. Rediscover while all writers remain quiesced.");
    }

    /// <summary>
    /// Offline-only explicit bootstrap. Caller must stop ALL old and new writers, enumerate every
    /// legacy Pts sequence (including channels absent from projections), and supply persisted high
    /// values plus authoritative observed Pts maxima. Never invoke from application startup.
    /// Publishing the fence certifies inventory completeness; old binaries must never restart.
    /// </summary>
    public async Task BootstrapQuiescedAsync(IReadOnlyCollection<ChannelPtsLegacySeed> completeInventory,
        string operatorAttestation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operatorAttestation)) throw new ArgumentException("Explicit operator attestation is required.", nameof(operatorAttestation));
        if (await _fences.Find(new BsonDocument("_id", FenceId)).AnyAsync(cancellationToken))
            throw new InvalidOperationException("The channel Pts authority is already enabled; bootstrap cannot be rerun.");
        var planned = completeInventory.ToDictionary(x => x.ChannelId, x => x.HighWatermark());
        await ValidateInventoryAsync(completeInventory, cancellationToken);
        foreach (var (id, pts) in planned)
        {
            var seed = new BsonDocument { { "_id", id }, { "Pts", pts } };
            await InsertIfAbsentAsync(_counters, seed, cancellationToken);
            var actual = await _counters.Find(new BsonDocument("_id", id)).FirstOrDefaultAsync(cancellationToken);
            if (actual == null || actual["Pts"].ToInt64() != pts || actual.Contains("Pending"))
                throw new InvalidOperationException("Bootstrap conflicts with existing counter state.");
            await CheckpointAsync("bootstrap-after-seed");
        }
        await ValidateInventoryAsync(completeInventory, cancellationToken);
        await _fences.InsertOneAsync(new BsonDocument { { "_id", FenceId }, { "Attestation", operatorAttestation },
            { "SeededChannels", planned.Count }, { "CreatedAt", DateTime.UtcNow } }, cancellationToken: cancellationToken);
    }
}

/// <summary>Persisted HiLo high H reserves raw [(H-1)*1000+1, H*1000], inclusive.</summary>
public sealed record ChannelPtsLegacySeed(long ChannelId, long PersistedHigh, long ObservedPts)
{
    public int HighWatermark()
    {
        if (ChannelId < MyTelegramConsts.ChannelInitId || PersistedHigh < 0 || ObservedPts < 0)
            throw new ArgumentOutOfRangeException(nameof(PersistedHigh));
        return checked((int)Math.Max(ObservedPts, checked(PersistedHigh * 1000 + MyTelegramConsts.PtsInitId)));
    }
}
