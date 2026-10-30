using global::EventFlow.Aggregates;
using global::EventFlow.EventStores;
using global::EventFlow.Exceptions;
using global::EventFlow.MongoDB.EventStore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MyTelegram.Domain.Aggregates.Messaging;
using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.EventFlow.MongoDB;
using MyTelegram.Messenger.Services.AntiSpam;
using MyTelegram.Messenger.Tests.Stats;

namespace MyTelegram.Messenger.Tests.AntiSpam;

public sealed class AntiSpamProofPersistenceTests
{
    [Fact]
    public void Unrelated_event_payload_is_not_parsed()
    {
        var doc = Event(1, "unrelated", "not-json");
        AntiSpamIndexedEventPersistence.AddProofFields(doc);
        Assert.False(doc.Contains("AntiSpamEventName"));
    }

    [Fact]
    public void Relevant_metadata_and_operation_are_extracted_without_changing_serialized_data()
    {
        var doc = Event(1, "anti-spam-reply-deletion-applied", "{\"OperationKey\":\"job\",\"Replies\":2}");
        var original = doc["Data"];
        AntiSpamIndexedEventPersistence.AddProofFields(doc);
        Assert.Equal("antispamreplydeletionapplied", doc["AntiSpamEventName"].AsString);
        Assert.Equal("job", doc["AntiSpamOperation"].AsString);
        Assert.Equal(original, doc["Data"]);
    }

    [RequiresMongoDbFact]
    public async Task Mixed_native_and_legacy_evidence_survives_provider_restart()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var events = mongo.Database.GetCollection<BsonDocument>("eventflow.events");
        var created = Event(1, "mention-created", "{\"MessageId\":10}");
        var applied = Event(2, "anti-spam-mention-deletion-applied", "{\"OperationKey\":\"job\",\"MessageId\":10}");
        AntiSpamIndexedEventPersistence.AddProofFields(applied);
        applied["AntiSpamProofVersion"] = 1;
        await events.InsertManyAsync([created, applied]);
        var provider = new AntiSpamEffectProofReader(mongo.Database);
        var receipt = await provider.FindReceiptAsync("dialog-test", "job", AntiSpamEffectKind.MentionDeletion, 2, default);
        Assert.Equal(2, receipt!.AggregateVersion);
        var proof = await new AntiSpamEffectProofReader(mongo.Database).GetMentionConsumptionAsync("dialog-test", 10, 2, default);
        Assert.Equal(1, proof.CreatedAtVersion);
        Assert.Equal(2, proof.ConsumedAtVersion);
        Assert.False((await events.Find(new BsonDocument("_id", 1)).SingleAsync()).Contains("AntiSpamProofVersion"));
    }

    [RequiresMongoDbFact]
    public async Task Source_gap_does_not_authorize_absent_receipt()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await mongo.Database.GetCollection<BsonDocument>("eventflow.events").InsertOneAsync(Event(2, "mention-created", "{\"MessageId\":10}"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AntiSpamEffectProofReader(mongo.Database)
            .FindReceiptAsync("dialog-test", "missing", AntiSpamEffectKind.MentionDeletion, 2, default));
        Assert.Equal(0, await mongo.Database.GetCollection<BsonDocument>("antispam_legacy_effect_coverage").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [RequiresMongoDbFact]
    public async Task Unknown_proof_schema_does_not_authorize_absent_receipt()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var doc = Event(1, "mention-created", "{\"MessageId\":10}");
        doc["AntiSpamProofVersion"] = 99;
        await mongo.Database.GetCollection<BsonDocument>("eventflow.events").InsertOneAsync(doc);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AntiSpamEffectProofReader(mongo.Database)
            .FindReceiptAsync("dialog-test", "missing", AntiSpamEffectKind.MentionDeletion, 1, default));
    }

    [RequiresMongoDbFact]
    public async Task Atomic_append_roundtrips_through_stock_reader_and_duplicate_is_concurrency_error()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var events = mongo.Database.GetCollection<BsonDocument>("eventflow.events");
        await events.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("AggregateId").Ascending("AggregateSequenceNumber"),
            new CreateIndexOptions { Unique = true }));
        var next = 0L;
        var sequences = new Mock<IMongoDbEventSequenceStore>();
        sequences.Setup(x => x.GetNextSequence(It.IsAny<string>())).Returns(() => ++next);
        var batch = Guid.NewGuid();
        var metadata = new Mock<IMetadata>();
        metadata.Setup(x => x["aggregate_name"]).Returns("message");
        metadata.Setup(x => x["batch_id"]).Returns(batch.ToString());
        var serialized = new SerializedEvent("{\"event_name\":\"anti-spam-reply-deletion-applied\"}",
            "{\"OperationKey\":\"job\",\"Replies\":2}", 1, metadata.Object);
        var id = MessageId.Create(100, 1);
        var writer = new AntiSpamIndexedEventPersistence(mongo.Database, sequences.Object);
        await writer.CommitEventsAsync(id, [serialized], default);
        var doc = await events.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        Assert.Equal("job", doc["AntiSpamOperation"].AsString);
        Assert.Equal(serialized.SerializedData, doc["Data"].AsString);
        Assert.Equal(batch, doc["BatchId"].AsBsonBinaryData.ToGuid());
        var stock = new MongoDbEventPersistence(NullLogger<MongoDbEventPersistence>.Instance, mongo.Database, sequences.Object);
        var loaded = await stock.LoadCommittedEventsAsync(id, 1, default);
        Assert.Single(loaded);
        Assert.Equal(serialized.SerializedData, loaded.Single().Data);
        await Assert.ThrowsAsync<OptimisticConcurrencyException>(() => writer.CommitEventsAsync(id, [serialized], default));
        Assert.Equal(1, await events.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    private static BsonDocument Event(int sequence, string name, string data) => new() {
        ["_id"] = sequence, ["AggregateId"] = "dialog-test", ["AggregateSequenceNumber"] = sequence,
        ["Metadata"] = $"{{\"event_name\":\"{name}\"}}", ["Data"] = data };
}
