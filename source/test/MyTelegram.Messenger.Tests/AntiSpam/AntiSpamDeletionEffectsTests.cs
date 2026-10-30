using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.AntiSpam;
using MyTelegram.Messenger.Tests.Stats;

namespace MyTelegram.Messenger.Tests.AntiSpam;

public sealed class AntiSpamDeletionEffectsTests
{
    [RequiresMongoDbFact]
    public async Task Captured_effect_identities_are_immutable_across_restart()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var options = Microsoft.Extensions.Options.Options.Create(new AntiSpamOptions());
        var store = new AntiSpamJobStore(mongo.Database, options);
        await store.EnqueueAsync(10, 20, 30, "fingerprint", [], [], default, 1);
        var jobs = mongo.Database.GetCollection<BsonDocument>("antispam_jobs");
        await jobs.UpdateOneAsync(FilterDefinition<BsonDocument>.Empty,
            new BsonDocument("$set", new BsonDocument("due", DateTime.UtcNow.AddMinutes(-1))));
        var job = (await store.ClaimAsync(default))!;
        var metadata = new BsonDocument { ["version"] = 1, ["thread_root"] = 12,
            ["mentions"] = new BsonArray { new BsonDocument { ["user"] = 21L, ["version"] = 7 } } };
        Assert.True(await store.SaveEffectsAsync(job, metadata, default));
        var restarted = new AntiSpamJobStore(mongo.Database, options);
        Assert.True(await restarted.SaveEffectsAsync(job, new BsonDocument { ["version"] = 1,
            ["thread_root"] = 99, ["mentions"] = new BsonArray() }, default));
        Assert.Equal(metadata, job["effects"].AsBsonDocument);
        Assert.Equal(metadata, (await jobs.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync())["effects"]);
    }

    [RequiresMongoDbFact]
    public async Task Capture_after_dispatch_is_rejected_instead_of_inventing_lost_recipients()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new AntiSpamJobStore(mongo.Database, Microsoft.Extensions.Options.Options.Create(new AntiSpamOptions()));
        await store.EnqueueAsync(10, 20, 30, "fingerprint", [], [], default, 1);
        var jobs = mongo.Database.GetCollection<BsonDocument>("antispam_jobs");
        await jobs.UpdateOneAsync(FilterDefinition<BsonDocument>.Empty,
            new BsonDocument("$set", new BsonDocument { ["due"] = DateTime.UtcNow.AddMinutes(-1), ["dispatched"] = true }));
        var job = (await store.ClaimAsync(default))!;
        Assert.False(await store.SaveEffectsAsync(job, new BsonDocument { ["version"] = 1,
            ["thread_root"] = 0, ["mentions"] = new BsonArray() }, default));
        Assert.False((await jobs.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync()).Contains("effects"));
    }
}
