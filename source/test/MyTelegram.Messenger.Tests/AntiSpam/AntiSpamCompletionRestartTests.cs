using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.AntiSpam;
using MyTelegram.Messenger.Tests.Stats;

namespace MyTelegram.Messenger.Tests.AntiSpam;

public sealed class AntiSpamCompletionRestartTests
{
    [RequiresMongoDbFact]
    public async Task Completion_assignment_survives_service_restart_and_concurrent_candidates()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var options = Microsoft.Extensions.Options.Options.Create(new AntiSpamOptions());
        var store = new AntiSpamJobStore(mongo.Database, options);
        await store.EnqueueAsync(10, 20, 30, "fingerprint", [], [], default, 1);
        var jobs = mongo.Database.GetCollection<BsonDocument>("antispam_jobs");
        var job = await jobs.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        await store.ConfirmAsync(10, 30, Guid.Parse(job["request"].AsString), default);
        job = await jobs.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        var results = await Task.WhenAll(Enumerable.Range(1, 16).Select(i =>
            new AntiSpamJobStore(mongo.Database, options).AssignCompletionAsync(job, 501, i, default)));
        Assert.Single(results.Select(x => x.ToJson()).Distinct());

        // Discard every service instance. A new Mongo client reads the same persisted assignment.
        var restartedDatabase = new MongoClient(mongo.Database.Client.Settings).GetDatabase(mongo.Database.DatabaseNamespace.DatabaseName);
        var restarted = new AntiSpamJobStore(restartedDatabase, options);
        var recovered = await restarted.AssignCompletionAsync(job, 501, 999999, default);
        Assert.Equal(results[0], recovered);
        Assert.False((await jobs.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync()).Contains("expires"));
        Assert.False((await jobs.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync()).Contains("transport_accepted"));
    }

    [RequiresMongoDbFact]
    public async Task Crash_after_transport_acceptance_preserves_recoverable_confirmed_job()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var options = Microsoft.Extensions.Options.Options.Create(new AntiSpamOptions());
        var store = new AntiSpamJobStore(mongo.Database, options);
        await store.EnqueueAsync(10, 20, 30, "fingerprint", [], [], default, 1);
        var jobs = mongo.Database.GetCollection<BsonDocument>("antispam_jobs");
        var job = await jobs.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        await store.ConfirmAsync(10, 30, Guid.Parse(job["request"].AsString), default);
        job = await jobs.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        var assignment = await store.AssignCompletionAsync(job, 501, 6001, default);
        await store.MarkTransportAcceptedAsync(job, default);
        // Simulate process loss before the admin-log stage; the persisted job remains retryable.
        var restarted = new AntiSpamJobStore(mongo.Database, options);
        await jobs.UpdateOneAsync(new BsonDocument("_id", job["_id"]),
            new BsonDocument("$set", new BsonDocument("due", DateTime.UtcNow.AddSeconds(-1))));
        var recovered = await restarted.ClaimAsync(default);
        Assert.NotNull(recovered);
        Assert.Equal("confirmed", recovered!["state"].AsString);
        Assert.True(recovered["transport_accepted"].AsBoolean);
        Assert.Equal(assignment, recovered["completion"].AsBsonDocument);
        Assert.False(recovered.Contains("expires"));
    }
}
