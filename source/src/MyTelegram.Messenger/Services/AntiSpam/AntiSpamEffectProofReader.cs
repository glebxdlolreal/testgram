using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.EventFlow.MongoDB;

namespace MyTelegram.Messenger.Services.AntiSpam;

/// <summary>Indexed committed evidence; legacy pages are inspected before absence is authoritative.</summary>
public sealed class AntiSpamEffectProofReader(IMongoDatabase database) : IAntiSpamEffectProofReader
{
    private IMongoCollection<BsonDocument> DurableCollection(string name) => database.GetCollection<BsonDocument>(name)
        .WithReadPreference(ReadPreference.Primary).WithReadConcern(ReadConcern.Majority)
        .WithWriteConcern(WriteConcern.WMajority.With(journal: true));
    private IMongoCollection<BsonDocument> Events => DurableCollection("eventflow.events");
    private IMongoCollection<BsonDocument> Legacy => DurableCollection("antispam_legacy_effect_proofs");
    private IMongoCollection<BsonDocument> Coverage => DurableCollection("antispam_legacy_effect_coverage");

    public async Task<AntiSpamEffectReceipt?> FindReceiptAsync(string aggregateId, string operationKey, AntiSpamEffectKind kind,
        int throughVersion, CancellationToken cancellationToken)
    {
        await EnsureCoverageAsync(aggregateId, throughVersion, cancellationToken);
        var filter = Filter(aggregateId, throughVersion);
        filter["AntiSpamOperation"] = operationKey;
        filter["AntiSpamEventName"] = kind switch
        {
            AntiSpamEffectKind.MentionDeletion => "antispammentiondeletionapplied",
            AntiSpamEffectKind.ReplyDeletion => "antispamreplydeletionapplied",
            AntiSpamEffectKind.MentionReadIntent => "mentionreadintentapplied",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var rows = await FindAsync(filter, false, cancellationToken);
        return rows == null ? null : new AntiSpamEffectReceipt(rows["AggregateSequenceNumber"].AsInt32, operationKey, kind);
    }

    public async Task<MentionConsumptionProof> GetMentionConsumptionAsync(string aggregateId, int messageId, int throughVersion,
        CancellationToken cancellationToken)
    {
        await EnsureCoverageAsync(aggregateId, throughVersion, cancellationToken);
        var filter = Filter(aggregateId, throughVersion);
        filter["AntiSpamMessageId"] = messageId;
        filter["AntiSpamEventName"] = "mentioncreated";
        var created = await FindAsync(filter, true, cancellationToken);
        if (created == null) return new(throughVersion, null, null);
        var createdAt = created["AggregateSequenceNumber"].AsInt32;
        filter = Filter(aggregateId, throughVersion);
        filter["AggregateSequenceNumber"].AsBsonDocument["$gt"] = createdAt;
        filter["$or"] = new BsonArray {
            new BsonDocument { ["AntiSpamMessageId"] = messageId, ["AntiSpamEventName"] = new BsonDocument("$in", new BsonArray { "mentionread", "antispammentiondeletionapplied" }) },
            new BsonDocument("AntiSpamEventName", "unreadmentionsread") };
        var consumed = await FindAsync(filter, true, cancellationToken);
        return new(throughVersion, createdAt, consumed?["AggregateSequenceNumber"].AsInt32);
    }

    private static BsonDocument Filter(string id, int version) => new() { ["AggregateId"] = id,
        ["AggregateSequenceNumber"] = new BsonDocument("$lte", version) };

    private async Task<BsonDocument?> FindAsync(BsonDocument filter, bool descending, CancellationToken ct)
    {
        var sort = new BsonDocument("AggregateSequenceNumber", descending ? -1 : 1);
        var current = await Events.Find(filter).Sort(sort).FirstOrDefaultAsync(ct);
        var legacy = await Legacy.Find(filter).Sort(sort).FirstOrDefaultAsync(ct);
        if (current == null) return legacy;
        if (legacy == null) return current;
        var comparison = current["AggregateSequenceNumber"].AsInt32.CompareTo(legacy["AggregateSequenceNumber"].AsInt32);
        return (descending ? comparison >= 0 : comparison <= 0) ? current : legacy;
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        foreach (var collection in new[] { Events, Legacy })
            await collection.Indexes.CreateManyAsync([
                new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("AggregateId").Ascending("AntiSpamOperation").Ascending("AntiSpamEventName").Ascending("AggregateSequenceNumber")),
                new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("AggregateId").Ascending("AntiSpamMessageId").Ascending("AntiSpamEventName").Descending("AggregateSequenceNumber")),
                new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("AggregateId").Ascending("AntiSpamEventName").Descending("AggregateSequenceNumber"))], ct);
    }

    private async Task EnsureCoverageAsync(string id, int throughVersion, CancellationToken ct)
    {
        var coverage = await Coverage.Find(new BsonDocument("_id", id)).FirstOrDefaultAsync(ct);
        var covered = coverage?.GetValue("through", 0).AsInt32 ?? 0;
        if (covered >= throughVersion) return;
        // At most 256 source events per call. Page progress survives process loss, while readers
        // never accept an absent indexed proof until every legacy event through the version is seen.
        var docs = await Events.Find(new BsonDocument { ["AggregateId"] = id,
            ["AggregateSequenceNumber"] = new BsonDocument { ["$gt"] = covered, ["$lte"] = throughVersion } })
            .Sort(new BsonDocument("AggregateSequenceNumber", 1)).Limit(256).ToListAsync(ct);
        var expected = covered + 1;
        foreach (var doc in docs)
        {
            if (doc["AggregateSequenceNumber"].AsInt32 != expected)
                throw new InvalidOperationException("Anti-spam proof source contains a history gap");
            if (doc.TryGetValue("AntiSpamProofVersion", out var proofVersion) && (!proofVersion.IsInt32 || proofVersion.AsInt32 != 1))
                throw new InvalidOperationException("Unsupported anti-spam proof schema version");
            if (!doc.Contains("AntiSpamProofVersion"))
            {
                var proof = doc.DeepClone().AsBsonDocument;
                AntiSpamIndexedEventPersistence.AddProofFields(proof);
                proof["_id"] = $"{id}:{expected}";
                proof.Remove("Data"); proof.Remove("Metadata"); proof.Remove("BatchId");
                await Legacy.UpdateOneAsync(new BsonDocument("_id", proof["_id"]),
                    new BsonDocument("$setOnInsert", proof), new UpdateOptions { IsUpsert = true }, ct);
            }
            expected++;
        }
        var inspected = expected - 1;
        if (inspected > covered)
            await Coverage.UpdateOneAsync(new BsonDocument("_id", id),
                new BsonDocument("$max", new BsonDocument("through", inspected)), new UpdateOptions { IsUpsert = true }, ct);
        if (inspected < throughVersion)
            throw new InvalidOperationException("Anti-spam proof coverage is pending; retry after the persisted page");
    }
}
