using EventFlow;
using EventFlow.Aggregates;
using EventFlow.Queries;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using MyTelegram.Messenger.Services.AntiSpam;
using MyTelegram.Messenger.Tests.Stats;

namespace MyTelegram.Messenger.Tests.AntiSpam;

public class AntiSpamWorkerTests
{
    private static byte[] ActionBytes()
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        new MyTelegram.Schema.TChannelAdminLogEventActionDeleteMessage
        {
            Message = new MyTelegram.Schema.TMessageEmpty { Id = 30 }
        }.Serialize(buffer);
        return buffer.WrittenSpan.ToArray();
    }
    [RequiresMongoDbFact]
    public async Task Claims_are_exclusive_and_duplicate_enqueue_keeps_request_identity()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new AntiSpamJobStore(mongo.Database, Microsoft.Extensions.Options.Options.Create(new AntiSpamOptions()));
        await store.InitializeAsync(default);
        await store.EnqueueAsync(10, 20, 30, "hash", [], ActionBytes(), default);
        var collection = mongo.Database.GetCollection<BsonDocument>("antispam_jobs");
        var before = await collection.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        await store.EnqueueAsync(10, 20, 30, "hash", [], ActionBytes(), default);
        await collection.UpdateManyAsync(FilterDefinition<BsonDocument>.Empty, new BsonDocument("$set", new BsonDocument("due", DateTime.UtcNow.AddSeconds(-1))));
        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.ClaimAsync(default)));
        Assert.Single(claims.Where(x => x != null));
        Assert.Equal(before["request"], claims.Single(x => x != null)!["request"]);
    }

    [RequiresMongoDbFact]
    public async Task Confirmation_without_persisted_message_proof_cannot_log()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var options = Microsoft.Extensions.Options.Options.Create(new AntiSpamOptions());
        var store = new AntiSpamJobStore(mongo.Database, options);
        await store.EnqueueAsync(10, 20, 30, "hash", [], ActionBytes(), default);
        var jobs = mongo.Database.GetCollection<BsonDocument>("antispam_jobs");
        var job = await jobs.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        await store.ConfirmAsync(10, 30, Guid.NewGuid(), default);
        Assert.Equal("pending", (await jobs.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync())["state"].AsString);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteConfirmedLogAsync(job, default));
        await store.ConfirmAsync(10, 30, Guid.Parse(job["request"].AsString), default);
        await jobs.UpdateManyAsync(FilterDefinition<BsonDocument>.Empty, new BsonDocument("$set", new BsonDocument("due", DateTime.UtcNow.AddSeconds(-1))));
        job = (await store.ClaimAsync(default))!;
        var commands = new Mock<ICommandBus>(MockBehavior.Strict);
        var aggregates = new Mock<IAggregateStore>();
        aggregates.Setup(x => x.LoadAsync<MyTelegram.Domain.Aggregates.Messaging.MessageAggregate,
                MyTelegram.Domain.Aggregates.Messaging.MessageId>(It.IsAny<MyTelegram.Domain.Aggregates.Messaging.MessageId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MyTelegram.Domain.Aggregates.Messaging.MessageAggregate(
                MyTelegram.Domain.Aggregates.Messaging.MessageId.Create(10, 30)));
        var completion = new AntiSpamCompletionService(store,
            Mock.Of<MyTelegram.Messenger.Services.Pts.IChannelPtsAllocator>(), Mock.Of<MyTelegram.IIdGenerator>(),
            commands.Object, aggregates.Object, mongo.Database, Mock.Of<MyTelegram.Services.Services.IObjectMessageSender>(), Mock.Of<IQueryProcessor>(), null!, null!);
        var worker = new AntiSpamWorker(store, new AntiSpamClassifier(options), Mock.Of<IAntiSpamActorService>(),
            Mock.Of<IQueryProcessor>(), aggregates.Object, commands.Object, options, NullLogger<AntiSpamWorker>.Instance,
            Mock.Of<MyTelegram.Messenger.Converters.ConverterServices.IMessageConverterService>(), completion, null!, null!, null!);
        await worker.ProcessAsync(job, default);
        await worker.ProcessAsync(job, default);
        Assert.Equal(0, await mongo.Database.GetCollection<BsonDocument>("channel_admin_log").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        commands.Verify(x => x.PublishAsync(It.IsAny<MyTelegram.Domain.Aggregates.Temp.StartDeleteChannelMessagesCommand>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [RequiresMongoDbFact]
    public async Task Attempts_are_bounded_and_expired_leases_are_reclaimed()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new AntiSpamJobStore(mongo.Database, Microsoft.Extensions.Options.Options.Create(new AntiSpamOptions { MaxAttempts = 2 }));
        await store.EnqueueAsync(10, 20, 30, "hash", [], ActionBytes(), default);
        var jobs = mongo.Database.GetCollection<BsonDocument>("antispam_jobs");
        async Task Expire() => await jobs.UpdateManyAsync(FilterDefinition<BsonDocument>.Empty,
            new BsonDocument("$set", new BsonDocument("due", DateTime.UtcNow.AddSeconds(-1))));
        await Expire();
        var first = (await store.ClaimAsync(default))!;
        Assert.False(first.Contains("expires"));
        Assert.True(await store.TryBeginDispatchAsync(first, default));
        await Expire();
        var second = (await store.ClaimAsync(default))!;
        Assert.NotEqual(first["lease"], second["lease"]);
        Assert.False(await store.TryBeginDispatchAsync(first, default));
        Assert.True(await store.TryBeginDispatchAsync(second, default));
        await store.FinishAsync(first, "cancelled", default);
        Assert.Equal("pending", (await jobs.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync())["state"].AsString);
        await Expire();
        var exhausted = (await store.ClaimAsync(default))!;
        Assert.False(await store.TryBeginDispatchAsync(exhausted, default));
        await store.FinishAsync(exhausted, "recovery", default);
        for (var i = 0; i < 5; i++)
        {
            await Expire();
            var recovery = (await store.ClaimAsync(default))!;
            Assert.Equal("recovery", recovery["state"].AsString);
            Assert.False(recovery.Contains("expires"));
        }
        // Confirmation/log reconciliation is independent of the exhausted deletion budget.
        await store.ConfirmAsync(10, 30, Guid.Parse(first["request"].AsString), default);
        for (var i = 0; i < 5; i++)
        {
            await Expire();
            Assert.Equal("confirmed", (await store.ClaimAsync(default))!["state"].AsString);
        }
    }

    [RequiresMongoDbFact]
    public async Task Only_terminal_jobs_expire_and_inflight_cancellation_retains_evidence()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new AntiSpamJobStore(mongo.Database, Microsoft.Extensions.Options.Options.Create(new AntiSpamOptions()));
        var jobs = mongo.Database.GetCollection<BsonDocument>("antispam_jobs");
        async Task<BsonDocument> Claim(int id)
        {
            await store.EnqueueAsync(10, 20, id, "hash", [], ActionBytes(), default);
            await jobs.UpdateOneAsync(new BsonDocument("_id", AntiSpamJobStore.JobId(10, id)),
                new BsonDocument("$set", new BsonDocument("due", DateTime.UtcNow.AddSeconds(-1))));
            return (await store.ClaimAsync(default))!;
        }
        var terminal = await Claim(30);
        await store.FinishAsync(terminal, "cancelled", default);
        Assert.True((await jobs.Find(new BsonDocument("_id", terminal["_id"])).SingleAsync()).Contains("expires"));
        var inflight = await Claim(31);
        Assert.True(await store.TryBeginDispatchAsync(inflight, default));
        inflight["dispatched"] = true;
        await store.FinishAsync(inflight, "cancelled", default);
        var retained = await jobs.Find(new BsonDocument("_id", inflight["_id"])).SingleAsync();
        Assert.Equal("recovery", retained["state"].AsString);
        Assert.False(retained.Contains("expires"));
        await store.ConfirmAsync(10, 31, Guid.Parse(inflight["request"].AsString), default);
        Assert.False((await jobs.Find(new BsonDocument("_id", inflight["_id"])).SingleAsync()).Contains("expires"));
    }

    [RequiresMongoDbFact]
    public async Task Stale_lease_cannot_overwrite_confirmation()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new AntiSpamJobStore(mongo.Database, Microsoft.Extensions.Options.Options.Create(new AntiSpamOptions()));
        await store.EnqueueAsync(10, 20, 30, "hash", [], ActionBytes(), default);
        var jobs = mongo.Database.GetCollection<BsonDocument>("antispam_jobs");
        await jobs.UpdateManyAsync(FilterDefinition<BsonDocument>.Empty, new BsonDocument("$set", new BsonDocument("due", DateTime.UtcNow.AddSeconds(-1))));
        var job = (await store.ClaimAsync(default))!;
        await store.ConfirmAsync(10, 30, Guid.Parse(job["request"].AsString), default);
        await store.FinishAsync(job, "cancelled", default);
        Assert.Equal("confirmed", (await jobs.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync())["state"].AsString);
    }
}
