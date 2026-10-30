using global::EventFlow.Core;
using global::EventFlow.EventStores;
using global::EventFlow.Exceptions;
using global::EventFlow.MongoDB.EventStore;
using global::EventFlow.MongoDB.ValueObjects;
using global::EventFlow.ValueObjects;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using System.Text.Json;

namespace MyTelegram.EventFlow.MongoDB;

/// <summary>Opt-in stock-compatible append with atomically stored searchable moderation evidence.</summary>
public sealed class AntiSpamIndexedEventPersistence(IMongoDatabase database, IMongoDbEventSequenceStore sequences) : IEventPersistence
{
    // The extra proof fields written by this class must not crash the stock MongoDbEventPersistence
    // reader when it deserializes documents from the same collection using BsonClassMapSerializer.
    static AntiSpamIndexedEventPersistence()
    {
        if (!BsonClassMap.IsClassMapRegistered(typeof(MongoDbEventDataModel)))
            BsonClassMap.RegisterClassMap<MongoDbEventDataModel>(cm => { cm.AutoMap(); cm.SetIgnoreExtraElements(true); });
    }

    private IMongoCollection<BsonDocument> Events => database.GetCollection<BsonDocument>(MongoDbEventPersistence.CollectionName);

    private IMongoCollection<BsonDocument> Proofs => database.GetCollection<BsonDocument>("antispam_legacy_effect_proofs")
        .WithWriteConcern(WriteConcern.WMajority.With(journal: true));

    public async Task<IReadOnlyCollection<ICommittedDomainEvent>> CommitEventsAsync(IIdentity id,
        IReadOnlyCollection<SerializedEvent> serializedEvents, CancellationToken cancellationToken)
    {
        if (serializedEvents.Count == 0) return [];
        var documents = serializedEvents.Select(e =>
        {
            var doc = new BsonDocument { ["_id"] = sequences.GetNextSequence(MongoDbEventPersistence.CollectionName),
                ["AggregateId"] = id.Value, ["AggregateName"] = e.Metadata["aggregate_name"],
                ["BatchId"] = new BsonBinaryData(Guid.Parse(e.Metadata["batch_id"]), GuidRepresentation.Standard),
                ["Data"] = e.SerializedData, ["Metadata"] = e.SerializedMetadata,
                ["AggregateSequenceNumber"] = e.AggregateSequenceNumber };
            return doc;
        }).OrderBy(x => x["AggregateSequenceNumber"].AsInt32).ToList();
        try
        {
            foreach (var doc in documents) AddProofFields(doc);
            await Events.InsertManyAsync(documents, cancellationToken: cancellationToken);
            var proofDocuments = documents.Where(x => x.Contains("AntiSpamEventName"))
                .Select(x => { var p = x.DeepClone().AsBsonDocument; p["_id"] = $"{id.Value}:{p["AggregateSequenceNumber"].AsInt32}"; p["AntiSpamProofVersion"] = 1; return p; }).ToList();
            if (proofDocuments.Count > 0)
                await Proofs.BulkWriteAsync(
                    proofDocuments.Select(x => new ReplaceOneModel<BsonDocument>(new BsonDocument("_id", x["_id"]), x) { IsUpsert = true }),
                    cancellationToken: cancellationToken);
        }
        catch (MongoBulkWriteException e) { throw new OptimisticConcurrencyException(e.Message, e); }
        return documents.Select(ToCommitted).ToList();
    }

    public static void AddProofFields(BsonDocument doc)
    {
        using var metadata = JsonDocument.Parse(doc["Metadata"].AsString);
        var root = metadata.RootElement;
        if (!root.TryGetProperty("event_name", out var name)) return;
        var eventName = name.GetString() ?? string.Empty;
        var normalized = eventName.Replace("-", string.Empty).Replace("_", string.Empty).ToLowerInvariant().Replace("event", string.Empty);
        if (normalized is not ("mentioncreated" or "mentionread" or "unreadmentionsread" or
            "antispammentiondeletionapplied" or "antispamreplydeletionapplied" or "mentionreadintentapplied")) return;
        doc["AntiSpamEventName"] = normalized;
        using var data = JsonDocument.Parse(doc["Data"].AsString);
        foreach (var property in data.RootElement.EnumerateObject())
        {
            if (property.Name.Equals("operationKey", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                doc["AntiSpamOperation"] = property.Value.GetString();
            if (property.Name.Equals("messageId", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var messageId))
                doc["AntiSpamMessageId"] = messageId;
        }
    }

    public async Task<IReadOnlyCollection<ICommittedDomainEvent>> LoadCommittedEventsAsync(IIdentity id, int fromEventSequenceNumber, CancellationToken cancellationToken) =>
        await LoadCommittedEventsAsync(id, fromEventSequenceNumber, int.MaxValue, cancellationToken);

    public async Task<IReadOnlyCollection<ICommittedDomainEvent>> LoadCommittedEventsAsync(IIdentity id, int fromEventSequenceNumber,
        int toEventSequenceNumber, CancellationToken cancellationToken)
    {
        var docs = await Events.Find(new BsonDocument { ["AggregateId"] = id.Value,
            ["AggregateSequenceNumber"] = new BsonDocument { ["$gte"] = fromEventSequenceNumber, ["$lte"] = toEventSequenceNumber } })
            .Sort(new BsonDocument("AggregateSequenceNumber", 1)).ToListAsync(cancellationToken);
        return docs.Select(ToCommitted).ToList();
    }

    public async Task<AllCommittedEventsPage> LoadAllCommittedEvents(GlobalPosition globalPosition, int pageSize, CancellationToken cancellationToken)
    {
        var start = globalPosition.IsStart ? 0 : long.Parse(globalPosition.Value);
        var docs = await Events.Find(new BsonDocument("_id", new BsonDocument("$gte", start)))
            .Sort(new BsonDocument("_id", 1)).Limit(pageSize).ToListAsync(cancellationToken);
        return new AllCommittedEventsPage(new GlobalPosition((docs.Count == 0 ? start : docs.Max(x => x["_id"].ToInt64()) + 1).ToString()),
            docs.Select(ToCommitted).ToList());
    }

    public async Task DeleteEventsAsync(IIdentity id, CancellationToken cancellationToken) =>
        await Events.DeleteManyAsync(new BsonDocument("AggregateId", id.Value), cancellationToken);

    private static ICommittedDomainEvent ToCommitted(BsonDocument doc) => new MongoDbEventDataModel {
        _id = doc["_id"].ToInt64(), AggregateId = doc["AggregateId"].AsString,
        AggregateName = doc["AggregateName"].AsString, AggregateSequenceNumber = doc["AggregateSequenceNumber"].AsInt32,
        BatchId = doc["BatchId"].AsBsonBinaryData.ToGuid(), Data = doc["Data"].AsString, Metadata = doc["Metadata"].AsString };
}
