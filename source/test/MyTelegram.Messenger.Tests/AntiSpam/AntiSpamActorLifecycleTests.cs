extern alias DataSeeder;

using EventFlow;
using EventFlow.Aggregates;
using EventFlow.Extensions;
using EventFlow.EventStores;
using EventFlow.MongoDB.Extensions;
using EventFlow.Queries;
using EventFlow.Snapshots;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Domain.Aggregates.User;
using MyTelegram.Domain.Aggregates.UserName;
using MyTelegram.EventFlow.MongoDB;
using MyTelegram.EventFlow.MongoDB.Extensions;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.Queries;
using MyTelegram.QueryHandlers.MongoDB.User;
using MyTelegram.QueryHandlers.MongoDB.UserName;
using MyTelegram.ReadModel.MongoDB;
using AntiSpamActorDataSeeder = DataSeeder::MyTelegram.DataSeeder.DataSeeders.AntiSpamActorDataSeeder;

namespace MyTelegram.Messenger.Tests.AntiSpam;

/// <summary>
/// Exercises durable actor ownership against an isolated mongod. Commands, event persistence,
/// UserReadModel projection and query handlers are real; no typed user response is substituted.
/// Only user command handlers are registered, deliberately excluding unrelated signup sagas.
/// </summary>
public sealed class AntiSpamActorLifecycleTests
{
    private const long AccessHash = 731947123456L;

    [RequiresMongoDbFact]
    public async Task Fresh_seed_persists_creation_and_verification_and_survives_provider_restart()
    {
        using var mongo = EmbeddedMongoServer.Start();
        BsonDocument marker;
        using (var fixture = new Fixture(mongo))
        {
            await fixture.SeedAsync();
            marker = await fixture.MarkerAsync();
            await fixture.AssertReadyAsync(marker);
        }

        // A fresh service provider rules out in-memory aggregate/event caches satisfying the retry.
        using var restarted = new Fixture(mongo);
        await restarted.SeedAsync();
        (await restarted.MarkerAsync()).ShouldBe(marker);
        await restarted.AssertReadyAsync(marker);
    }

    [RequiresMongoDbFact]
    public async Task Partial_marker_without_events_resumes_the_original_identity()
    {
        using var mongo = EmbeddedMongoServer.Start();
        using var fixture = new Fixture(mongo);
        var marker = NewMarker();
        await fixture.Markers.InsertOneAsync(marker);

        await fixture.SeedAsync();

        var ready = await fixture.MarkerAsync();
        ready["access_hash"].ShouldBe(marker["access_hash"]);
        ready["request_id"].ShouldBe(marker["request_id"]);
        await fixture.AssertReadyAsync(ready);
    }

    [RequiresMongoDbFact]
    public async Task Persisted_creation_before_ready_is_resumed_without_a_second_creation()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var marker = NewMarker();
        using (var interrupted = new Fixture(mongo))
        {
            await interrupted.Markers.InsertOneAsync(marker);
            await interrupted.CreateAsync(marker);
            var events = await interrupted.EventsAsync();
            events.Count.ShouldBe(2);
            events.First().GetAggregateEvent().ShouldBeOfType<UserCreatedEvent>();
            (await interrupted.MarkerAsync())["ready"].AsBoolean.ShouldBeFalse();
            var user = await interrupted.Query.ProcessAsync(new GetUserByIdQuery(MyTelegramConsts.AntiSpamBotUserId), CancellationToken.None);
            user.ShouldBeOfType<UserReadModel>();
            user!.Verified.ShouldBeFalse();
        }

        using var resumed = new Fixture(mongo);
        await resumed.SeedAsync();
        await resumed.AssertReadyAsync(await resumed.MarkerAsync());
    }

    [RequiresMongoDbFact]
    public async Task Persisted_verification_before_ready_is_resumed_without_more_events()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var marker = NewMarker();
        using (var interrupted = new Fixture(mongo))
        {
            await interrupted.Markers.InsertOneAsync(marker);
            await interrupted.CreateAsync(marker);
            await interrupted.VerifyAsync();
            (await interrupted.EventsAsync()).Count.ShouldBe(3);
            (await interrupted.MarkerAsync())["ready"].AsBoolean.ShouldBeFalse();
            var user = await interrupted.Query.ProcessAsync(
                new GetUserByIdQuery(MyTelegramConsts.AntiSpamBotUserId), CancellationToken.None);
            user.ShouldBeOfType<UserReadModel>();
            user!.Verified.ShouldBeTrue();
        }

        using var resumed = new Fixture(mongo);
        await resumed.SeedAsync();
        var ready = await resumed.MarkerAsync();
        ready["request_id"].ShouldBe(marker["request_id"]);
        ready["access_hash"].ShouldBe(marker["access_hash"]);
        await resumed.AssertReadyAsync(ready);
    }

    [RequiresMongoDbTheory]
    [InlineData("ordinary-user")]
    [InlineData("wrong-hash")]
    [InlineData("wrong-name")]
    [InlineData("wrong-first-name")]
    [InlineData("wrong-id")]
    public async Task Persisted_identity_collision_is_not_adopted_or_verified(string defect)
    {
        using var mongo = EmbeddedMongoServer.Start();
        using var fixture = new Fixture(mongo);
        var marker = NewMarker();
        await fixture.Markers.InsertOneAsync(marker);
        await fixture.CreateAsync(marker, defect);
        var before = await fixture.EventsAsync();
        var rowsBefore = await fixture.Users.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();

        await Should.ThrowAsync<InvalidOperationException>(() => fixture.SeedAsync());

        (await fixture.MarkerAsync())["ready"].AsBoolean.ShouldBeFalse();
        (await fixture.EventsAsync()).Count.ShouldBe(before.Count);
        (await fixture.Users.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync()).ShouldBe(rowsBefore);
    }

    [RequiresMongoDbFact]
    public async Task Ordinary_account_without_marker_is_not_adopted()
    {
        using var mongo = EmbeddedMongoServer.Start();
        using var fixture = new Fixture(mongo);
        await fixture.CreateAsync(NewMarker(), "ordinary-user");

        await Should.ThrowAsync<InvalidOperationException>(() => fixture.SeedAsync());

        (await fixture.Markers.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty)).ShouldBe(0);
        (await fixture.EventsAsync()).Count.ShouldBe(2);
    }

    [RequiresMongoDbTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Duplicate_projection_identity_is_rejected_before_verification(bool differentUserId)
    {
        using var mongo = EmbeddedMongoServer.Start();
        using var fixture = new Fixture(mongo);
        var marker = NewMarker();
        await fixture.Markers.InsertOneAsync(marker);
        await fixture.CreateAsync(marker);
        var duplicate = (await fixture.Users.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync()).DeepClone().AsBsonDocument;
        duplicate["_id"] = "conflicting-user-projection";
        if (differentUserId)
            duplicate["UserId"] = MyTelegramConsts.AntiSpamBotUserId + 1;
        // Deliberately corrupt only this disposable database to model pre-existing projection collisions.
        await fixture.Users.InsertOneAsync(duplicate);

        await Should.ThrowAsync<InvalidOperationException>(() => fixture.SeedAsync());

        (await fixture.MarkerAsync())["ready"].AsBoolean.ShouldBeFalse();
        (await fixture.EventsAsync()).Count.ShouldBe(2);
        (await fixture.Users.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty)).ShouldBe(2);
    }

    [RequiresMongoDbFact]
    public async Task Missing_projection_does_not_advertise_ready_or_repeat_creation()
    {
        using var mongo = EmbeddedMongoServer.Start();
        using var fixture = new Fixture(mongo);
        var marker = NewMarker();
        await fixture.Markers.InsertOneAsync(marker);
        await fixture.CreateAsync(marker);
        var projection = await fixture.Users.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        await fixture.Users.DeleteManyAsync(FilterDefinition<BsonDocument>.Empty);

        await Should.ThrowAsync<InvalidOperationException>(() => fixture.SeedAsync());
        (await fixture.MarkerAsync())["ready"].AsBoolean.ShouldBeFalse();
        (await fixture.EventsAsync()).Count.ShouldBe(2);

        // Restore the actual projection emitted by CreateUser, not a fabricated typed response.
        await fixture.Users.InsertOneAsync(projection);
        await fixture.SeedAsync();
        await fixture.AssertReadyAsync(await fixture.MarkerAsync());
    }

    private static BsonDocument NewMarker() => new()
    {
        ["_id"] = "actor",
        ["user_id"] = MyTelegramConsts.AntiSpamBotUserId,
        ["access_hash"] = AccessHash,
        ["request_id"] = Guid.NewGuid().ToString("D"),
        ["ready"] = false
    };

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider _services;
        private readonly IMongoDatabase _database;
        public IQueryProcessor Query => _services.GetRequiredService<IQueryProcessor>();
        public IMongoCollection<BsonDocument> Users => _database.GetCollection<BsonDocument>("eventflow-userreadmodel");
        public IMongoCollection<BsonDocument> Markers => _database.GetCollection<BsonDocument>(MyTelegramConsts.AntiSpamActorCollection);

        public Fixture(EmbeddedMongoServer mongo)
        {
            _database = mongo.Database;
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IMongoDbContext>(new TestMongoDbContext(_database));
            services.AddReadModelMongoDbContext<TestMongoDbContext>();
            services.AddEventFlow(options =>
            {
                options.ConfigureMongoDb(mongo.Client, _database.DatabaseNamespace.DatabaseName);
                options.UseMongoDbEventStore();
                options.UseMongoDbSnapshotStore();
                // Mirror the data seeder: the whole domain, so the create-user and username sagas run.
                options.AddDefaults(typeof(UserAggregate).Assembly);
                // Use the production projection stores and query handlers, with a narrowly scoped locator.
                options.UseMongoDbReadModel<UserAggregate, UserId, UserReadModel>();
                options.UseMongoDbReadModel<UserNameAggregate, UserNameId, UserNameReadModel>();
                options.AddQueryHandlers(typeof(GetUserByIdQueryHandler), typeof(GetUserNameByNameQueryHandler));
            });
            _services = services.BuildServiceProvider();
        }

        public Task SeedAsync() => new AntiSpamActorDataSeeder(
            _services.GetRequiredService<ICommandBus>(),
            _services.GetRequiredService<IEventStore>(),
            _services.GetRequiredService<ISnapshotStore>(), Query, _database).SeedAsync();

        public async Task CreateAsync(BsonDocument marker, string? defect = null)
        {
            var id = MyTelegramConsts.AntiSpamBotUserId;
            var requestId = Guid.Parse(marker["request_id"].AsString);
            var commands = _services.GetRequiredService<ICommandBus>();
            // Same two steps as the seeder: request commands dedupe on ReqMsgId alone, so a username in
            // CreateUserCommand would collide with the saga's UpdateUserNameCommand2 on this aggregate.
            await commands.PublishAsync(new CreateUserCommand(
                UserId.Create(id), Request(requestId, 1), defect == "wrong-id" ? id + 1 : id,
                defect == "wrong-hash" ? AccessHash + 1 : marker["access_hash"].ToInt64(),
                defect == "ordinary-user" ? "15550000001" : string.Empty,
                defect == "wrong-first-name" ? "Unrelated Actor" : "Testgram Anti-Spam", null,
                null, defect != "ordinary-user"), CancellationToken.None);
            var name = defect == "wrong-name" ? "unrelated_actor" : MyTelegramConsts.AntiSpamBotUserName;
            await commands.PublishAsync(new UpdateUserNameCommand(UserNameId.Create(name.ToLower()), Request(requestId, 2),
                new Peer(PeerType.User, id), name, null), CancellationToken.None);
        }

        private static RequestInfo Request(Guid requestId, byte step) => RequestInfo.Empty with
        {
            RequestId = requestId,
            ReqMsgId = BitConverter.ToInt64(System.Security.Cryptography.SHA256.HashData([.. requestId.ToByteArray(), step])) | 1,
            Date = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        public async Task VerifyAsync() =>
            await _services.GetRequiredService<ICommandBus>().PublishAsync(
                new SetVerifiedCommand(UserId.Create(MyTelegramConsts.AntiSpamBotUserId), true),
                CancellationToken.None);

        public async Task<IReadOnlyCollection<IDomainEvent<UserAggregate, UserId>>> EventsAsync() =>
            await _services.GetRequiredService<IEventStore>().LoadEventsAsync<UserAggregate, UserId>(
                UserId.Create(MyTelegramConsts.AntiSpamBotUserId), CancellationToken.None);

        public Task<BsonDocument> MarkerAsync() => Markers.Find(new BsonDocument("_id", "actor")).SingleAsync();

        public async Task AssertReadyAsync(BsonDocument marker)
        {
            marker["ready"].AsBoolean.ShouldBeTrue();
            var events = await EventsAsync();
            events.Count.ShouldBe(3);
            var created = events.Select(e => e.GetAggregateEvent()).OfType<UserCreatedEvent>().Single();
            events.Select(e => e.GetAggregateEvent()).OfType<UserNameUpdatedEvent>().Single().UserItem.UserName
                .ShouldBe(MyTelegramConsts.AntiSpamBotUserName);
            created.AccessHash.ShouldBe(marker["access_hash"].ToInt64());
            created.RequestInfo.RequestId.ShouldBe(Guid.Parse(marker["request_id"].AsString));
            events.Select(e => e.GetAggregateEvent()).OfType<UserVerifiedHasSetEvent>().Single().Verified.ShouldBeTrue();
            var user = await Query.ProcessAsync(new GetUserByIdQuery(MyTelegramConsts.AntiSpamBotUserId), CancellationToken.None);
            user.ShouldBeOfType<UserReadModel>();
            user!.UserId.ShouldBe(MyTelegramConsts.AntiSpamBotUserId);
            user.Bot.ShouldBeTrue();
            user.Verified.ShouldBeTrue();
            user.AccessHash.ShouldBe(marker["access_hash"].ToInt64());
            user.FirstName.ShouldBe("Testgram Anti-Spam");
            user.UserName.ShouldBe(MyTelegramConsts.AntiSpamBotUserName);
            (await Users.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty)).ShouldBe(1);
            var raw = await Users.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
            raw["Verified"].AsBoolean.ShouldBeTrue();
            raw["AccessHash"].ToInt64().ShouldBe(user.AccessHash);
        }

        public void Dispose() => _services.Dispose();
    }

    private sealed class TestMongoDbContext(IMongoDatabase database) : IMongoDbContext
    {
        public IMongoDatabase GetDatabase() => database;
    }
}
