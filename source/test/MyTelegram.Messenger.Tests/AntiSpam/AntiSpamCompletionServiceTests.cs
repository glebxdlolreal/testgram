using System.Reflection;
using EventFlow;
using EventFlow.Aggregates;
using EventFlow.EventStores;
using EventFlow.Extensions;
using EventFlow.MongoDB.Extensions;
using EventFlow.Queries;
using Moq;
using MyTelegram.Domain.Aggregates.Channel;
using MyTelegram.Domain.Aggregates.Messaging;
using MyTelegram.Services.Services;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Domain.Aggregates.AntiSpamCompletion;
using MyTelegram.Domain.Aggregates.Pts;
using MyTelegram.EventFlow.MongoDB;
using MyTelegram.EventFlow.MongoDB.Extensions;
using MyTelegram.Messenger.Services.AntiSpam;
using MyTelegram.Messenger.Services.Mentions;
using MyTelegram.Messenger.Services.Pts;
using MyTelegram.Messenger.Tests.Stats;
using UpdatesReadModel = MyTelegram.ReadModel.MongoDB.UpdatesReadModel;
using PtsReadModel = MyTelegram.ReadModel.MongoDB.PtsReadModel;
using MyTelegram.ReadModel.ReadModelLocators;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.AntiSpam;

public sealed class AntiSpamCompletionServiceTests
{
    private const long Channel = MyTelegramConsts.ChannelInitId + 873;
    private const int Message = 30;
    private const long GlobalSequence = 4_294_967_301L;

    [RequiresMongoDbFact]
    public async Task Persisted_completion_replays_after_provider_restart_without_changing_assignment_or_releasing_fence_early()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var options = Microsoft.Extensions.Options.Options.Create(new AntiSpamOptions());
        var jobs = new AntiSpamJobStore(mongo.Database, options);
        await jobs.EnqueueAsync(Channel, 20, Message, "fingerprint", [], [], default, 1);
        var job = await ReadJobAsync(mongo.Database);
        await jobs.ConfirmAsync(Channel, Message, Guid.Parse(job["request"].AsString), default);
        job = await ReadJobAsync(mongo.Database);
        var operation = $"antispamcompletion-{Channel}-{Message}-{Guid.Parse(job["request"].AsString):N}";
        var allocator = new DurableChannelPtsAllocator(mongo.Database);
        await allocator.BootstrapQuiescedAsync([new(Channel, 1, 7)], "isolated completion test inventory");
        var pts = await allocator.ReserveForPublicationAsync(Channel, operation);
        var assigned = await jobs.AssignCompletionAsync(job, pts, GlobalSequence, default, 29);

        using (var interrupted = new CompletionRuntime(mongo, project: false))
        {
            await interrupted.CreateAsync(operation, assigned);
            var events = await interrupted.EventsAsync(operation);
            var created = Assert.IsType<AntiSpamCompletionCreatedEvent>(Assert.Single(events).GetAggregateEvent());
            Assert.Equal(pts, created.Pts);
            Assert.Equal(GlobalSequence, created.GlobalSeqNo);
            Assert.False(await mongo.Database.GetCollection<BsonDocument>("eventflow-updatesreadmodel")
                .Find(new BsonDocument("_id", operation)).AnyAsync());
            Assert.Equal(pts - 1, await allocator.GetReadablePtsCeilingAsync(Channel));
        }

        // Actual EventFlow events, not mocked aggregate instances, recover in a fresh provider.
        using var recovered = new CompletionRuntime(mongo, project: true);
        var loaded = await recovered.Aggregates.LoadAsync<AntiSpamCompletionAggregate, AntiSpamCompletionId>(
            new AntiSpamCompletionId(operation), default);
        Assert.NotNull(loaded.Completion);
        Assert.Equal(GlobalSequence, loaded.Completion!.GlobalSeqNo);
        Assert.Equal(assigned["date"].AsInt32, loaded.Completion.Date);
        await recovered.CreateAsync(operation, assigned);
        var update = await mongo.Database.GetCollection<UpdatesReadModel>("eventflow-updatesreadmodel")
            .Find(new BsonDocument("_id", operation)).SingleAsync();
        Assert.Equal(pts, update.Pts);
        Assert.Equal(GlobalSequence, update.GlobalSeqNo);
        var deletion = Assert.IsType<TUpdateDeleteChannelMessages>(Assert.Single(update.Updates!));
        Assert.Equal(new[] { Message }, deletion.Messages);
        Assert.Equal(1, deletion.PtsCount);
        Assert.False(await mongo.Database.GetCollection<BsonDocument>("eventflow-ptsreadmodel")
            .Find(new BsonDocument("PeerId", Channel)).AnyAsync());
        Assert.Equal(pts - 1, await allocator.GetReadablePtsCeilingAsync(Channel));

        await recovered.PublishAsync(operation, assigned);
        var state = await mongo.Database.GetCollection<PtsReadModel>("eventflow-ptsreadmodel")
            .Find(new BsonDocument("PeerId", Channel)).SingleAsync();
        Assert.Equal(pts, state.Pts);
        // The channel Pts row advances through PtsAggregate like any other channel update; the global
        // sequence for difference delivery lives on the updates row asserted above.
        await new DurableChannelPtsAllocator(mongo.Database).MarkPublishedAsync(Channel, operation, pts);
        Assert.Null(await allocator.GetReadablePtsCeilingAsync(Channel));
        Assert.Equal(pts + 1, await allocator.ReserveAsync(Channel, "later-message"));

        await recovered.CreateAsync(operation, assigned);
        await recovered.PublishAsync(operation, assigned);
        var replayed = await mongo.Database.GetCollection<PtsReadModel>("eventflow-ptsreadmodel")
            .Find(new BsonDocument("PeerId", Channel)).SingleAsync();
        Assert.Equal(state.Seq, replayed.Seq);
        Assert.Equal(state.Pts, replayed.Pts);
        Assert.Equal(1, await mongo.Database.GetCollection<BsonDocument>("eventflow-updatesreadmodel")
            .CountDocumentsAsync(new BsonDocument("_id", operation)));
        Assert.Equal(assigned, (await ReadJobAsync(mongo.Database))["completion"].AsBsonDocument);
        var persisted = await recovered.EventsAsync(operation);
        Assert.All(persisted.Select(x => x.GetAggregateEvent()).OfType<AntiSpamCompletionCreatedEvent>(),
            e => { Assert.Equal(pts, e.Pts); Assert.Equal(GlobalSequence, e.GlobalSeqNo); });
    }

    [RequiresMongoDbFact]
    public async Task Service_integration_persisted_event_without_update_projection_does_not_publish_or_release_fence()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var jobs = new AntiSpamJobStore(mongo.Database, Microsoft.Extensions.Options.Options.Create(new AntiSpamOptions()));
        await jobs.EnqueueAsync(Channel, 20, Message, "fingerprint", [], [], default, 1);
        var job = await ReadJobAsync(mongo.Database);
        await jobs.ConfirmAsync(Channel, Message, Guid.Parse(job["request"].AsString), default);
        job = await ReadJobAsync(mongo.Database);
        var allocator = new DurableChannelPtsAllocator(mongo.Database);
        await allocator.BootstrapQuiescedAsync([new(Channel, 1, 7)], "isolated projection fault inventory");
        var pts = await allocator.ReserveForPublicationAsync(Channel, Operation(job));
        await jobs.AssignCompletionAsync(job, pts, GlobalSequence, default);
        job = await ReadJobAsync(mongo.Database);
        using var runtime = new CompletionRuntime(mongo, project: false);
        var sender = new Mock<IObjectMessageSender>(MockBehavior.Strict);
        using var services = await ServiceProviderAsync(mongo.Database, runtime.Commands, allocator, job, sender.Object);

        Assert.False(await ActivatorUtilities.CreateInstance<AntiSpamCompletionService>(services).CompleteAsync(job, default));

        var persisted = await runtime.EventsAsync(Operation(job));
        Assert.IsType<AntiSpamCompletionCreatedEvent>(Assert.Single(persisted).GetAggregateEvent());
        Assert.Equal(pts - 1, await allocator.GetReadablePtsCeilingAsync(Channel));
        VerifyNoPush(sender);
        Assert.False((await ReadJobAsync(mongo.Database)).Contains("transport_accepted"));
    }

    [RequiresMongoDbTheory]
    [InlineData("message-still-visible")]
    [InlineData("channel-not-projected")]
    public async Task Service_integration_keeps_fence_when_updates_and_pts_exist_but_deletion_projection_is_not_ready(string missing)
    {
        using var mongo = EmbeddedMongoServer.Start();
        var options = Microsoft.Extensions.Options.Options.Create(new AntiSpamOptions());
        var jobs = new AntiSpamJobStore(mongo.Database, options);
        await jobs.EnqueueAsync(Channel, 20, Message, "fingerprint", [], [], default, 1);
        var job = await ReadJobAsync(mongo.Database);
        await jobs.ConfirmAsync(Channel, Message, Guid.Parse(job["request"].AsString), default);
        job = await ReadJobAsync(mongo.Database);
        var operation = Operation(job);
        var allocator = new DurableChannelPtsAllocator(mongo.Database);
        await allocator.BootstrapQuiescedAsync([new(Channel, 1, 7)], "isolated readiness test inventory");
        var pts = await allocator.ReserveForPublicationAsync(Channel, operation);
        await jobs.AssignCompletionAsync(job, pts, GlobalSequence, default);
        job = await ReadJobAsync(mongo.Database);
        using var runtime = new CompletionRuntime(mongo, project: true);
        await runtime.CreateAsync(operation, job["completion"].AsBsonDocument);
        // Model a delayed independent projection, even if the Pts projection has already arrived.
        await runtime.PublishAsync(operation, job["completion"].AsBsonDocument);
        if (missing == "message-still-visible")
        {
            await mongo.Database.GetCollection<BsonDocument>("eventflow-messagereadmodel").InsertOneAsync(
                new BsonDocument { ["_id"] = MessageId.Create(Channel, Message).Value, ["OwnerPeerId"] = Channel, ["MessageId"] = Message });
            await mongo.Database.GetCollection<BsonDocument>("eventflow-channelreadmodel").InsertOneAsync(
                new BsonDocument { ["_id"] = ChannelId.Create(Channel).Value, ["ChannelId"] = Channel, ["TopMessageId"] = 0 });
        }
        var sender = new Mock<IObjectMessageSender>(MockBehavior.Strict);
        using var serviceProvider = await ServiceProviderAsync(mongo.Database, runtime.Commands, allocator, job, sender.Object);
        var service = ActivatorUtilities.CreateInstance<AntiSpamCompletionService>(serviceProvider);

        Assert.False(await service.CompleteAsync(job, default));

        Assert.Equal(pts - 1, await allocator.GetReadablePtsCeilingAsync(Channel));
        VerifyNoPush(sender);
        Assert.False((await ReadJobAsync(mongo.Database)).Contains("transport_accepted"));
        Assert.False((await ReadJobAsync(mongo.Database)).Contains("expires"));
        Assert.Equal("confirmed", (await ReadJobAsync(mongo.Database))["state"].AsString);
    }

    [RequiresMongoDbFact]
    public async Task Service_integration_foreign_fence_defers_B_so_single_worker_can_retry_A()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var jobs = new AntiSpamJobStore(mongo.Database, Microsoft.Extensions.Options.Options.Create(new AntiSpamOptions()));
        await jobs.EnqueueAsync(Channel, 20, Message, "fingerprint", [], [], default, 1);
        var job = await ReadJobAsync(mongo.Database);
        await jobs.ConfirmAsync(Channel, Message, Guid.Parse(job["request"].AsString), default);
        job = await ReadJobAsync(mongo.Database);
        var allocator = new DurableChannelPtsAllocator(mongo.Database);
        await allocator.BootstrapQuiescedAsync([new(Channel, 1, 7)], "isolated scheduling test inventory");
        var aPts = await allocator.ReserveForPublicationAsync(Channel, "earlier-A");
        var commands = new Mock<ICommandBus>(MockBehavior.Strict);
        var sender = new Mock<IObjectMessageSender>(MockBehavior.Strict);
        using var services = await ServiceProviderAsync(mongo.Database, commands.Object, allocator, job, sender.Object);
        var service = ActivatorUtilities.CreateInstance<AntiSpamCompletionService>(services);
        // A timeout fails the test rather than hanging the entire test process if B blocks.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.False(await service.CompleteAsync(job, deadline.Token));
        commands.Verify(x => x.PublishAsync(It.IsAny<CreateAntiSpamCompletionCommand>(), It.IsAny<CancellationToken>()), Times.Never);
        commands.Verify(x => x.PublishAsync(It.IsAny<PublishAntiSpamCompletionCommand>(), It.IsAny<CancellationToken>()), Times.Never);
        VerifyNoPush(sender);
        Assert.False((await ReadJobAsync(mongo.Database)).Contains("completion"));
        // The same sequential worker can now visit A again without a second consumer releasing it.
        Assert.Equal(aPts, await allocator.TryReserveForPublicationAsync(Channel, "earlier-A"));
        Assert.Equal(aPts - 1, await allocator.GetReadablePtsCeilingAsync(Channel));
        await allocator.MarkPublishedAsync(Channel, "earlier-A", aPts);
        Assert.Equal(aPts + 1, await allocator.TryReserveForPublicationAsync(Channel, Operation(job)));
    }

    [RequiresMongoDbTheory]
    [InlineData("transport")]
    [InlineData("before-publication-ack")]
    [InlineData("after-publication-ack")]
    public async Task Service_integration_retry_reuses_durable_update_and_releases_fence_only_after_projection(string failurePoint)
    {
        using var mongo = EmbeddedMongoServer.Start();
        var jobs = new AntiSpamJobStore(mongo.Database, Microsoft.Extensions.Options.Options.Create(new AntiSpamOptions()));
        var action = new System.Buffers.ArrayBufferWriter<byte>();
        new TChannelAdminLogEventActionDeleteMessage { Message = new TMessageEmpty { Id = Message } }.Serialize(action);
        await jobs.EnqueueAsync(Channel, 20, Message, "fingerprint", [], action.WrittenSpan.ToArray(), default, 1);
        var job = await ReadJobAsync(mongo.Database);
        await jobs.ConfirmAsync(Channel, Message, Guid.Parse(job["request"].AsString), default);
        job = await ReadJobAsync(mongo.Database);
        var allocator = new DurableChannelPtsAllocator(mongo.Database, point => point == failurePoint
            ? Task.FromException(new IOException("completion interrupted at " + point)) : Task.CompletedTask);
        await allocator.BootstrapQuiescedAsync([new(Channel, 1, 7)], "isolated transport test inventory");
        var operation = Operation(job);
        var pts = await allocator.ReserveForPublicationAsync(Channel, operation);
        var assignment = await jobs.AssignCompletionAsync(job, pts, GlobalSequence, default);
        job = await ClaimJobAsync(mongo.Database);
        await mongo.Database.GetCollection<BsonDocument>("eventflow-channelreadmodel").InsertOneAsync(
            new BsonDocument { ["_id"] = ChannelId.Create(Channel).Value, ["ChannelId"] = Channel, ["TopMessageId"] = 0 });
        var sent = new List<byte[]>();
        var sender = new Mock<IObjectMessageSender>(MockBehavior.Strict);
        sender.Setup(x => x.PushMessageToPeerAsync(It.IsAny<Peer>(), It.IsAny<TUpdates>(),
                null, null, null, null, pts, null, GlobalSequence, null, null))
            .Returns(async (Peer peer, TUpdates payload, long? excludeAuthKeyId, long? excludeUserId,
                long? onlySendToUserId, long? onlySendToThisAuthKeyId, int sentPts, int? qts,
                long globalSeqNo, MyTelegram.Core.PushData? pushData, List<long>? excludeUserIds) =>
            {
                // Observe publication order at the actual transport boundary, not after completion.
                Assert.Null(await allocator.GetReadablePtsCeilingAsync(Channel));
                Assert.True(await mongo.Database.GetCollection<BsonDocument>("eventflow-updatesreadmodel")
                    .Find(new BsonDocument { ["_id"] = operation, ["Pts"] = pts, ["GlobalSeqNo"] = GlobalSequence }).AnyAsync());
                Assert.True(await mongo.Database.GetCollection<BsonDocument>("eventflow-ptsreadmodel")
                    .Find(new BsonDocument { ["PeerId"] = Channel, ["Pts"] = pts }).AnyAsync());
                Assert.Equal(new Peer(PeerType.Channel, Channel), peer);
                Assert.Empty(payload.Users);
                Assert.Empty(payload.Chats);
                var deletion = Assert.IsType<TUpdateDeleteChannelMessages>(Assert.Single(payload.Updates));
                Assert.Equal(new[] { Message }, deletion.Messages);
                Assert.Equal(pts, deletion.Pts);
                var buffer = new System.Buffers.ArrayBufferWriter<byte>();
                payload.Serialize(buffer);
                sent.Add(buffer.WrittenSpan.ToArray());
                if (failurePoint == "transport" && sent.Count == 1)
                    throw new IOException("transport accepted bytes but acknowledgement was lost");
            });
        using (var runtime = new CompletionRuntime(mongo, project: true))
        using (var services = await ServiceProviderAsync(mongo.Database, runtime.Commands, allocator, job, sender.Object))
            await Assert.ThrowsAsync<IOException>(() => ActivatorUtilities.CreateInstance<AntiSpamCompletionService>(services)
                .CompleteAsync(job, default));
        Assert.Equal(failurePoint == "transport" ? 1 : 0, sent.Count);
        if (failurePoint == "before-publication-ack")
            Assert.Equal(pts - 1, await allocator.GetReadablePtsCeilingAsync(Channel));
        else
            Assert.Null(await allocator.GetReadablePtsCeilingAsync(Channel));
        Assert.Equal("confirmed", (await ReadJobAsync(mongo.Database))["state"].AsString);
        Assert.False((await ReadJobAsync(mongo.Database)).Contains("transport_accepted"));
        Assert.False((await ReadJobAsync(mongo.Database)).Contains("expires"));
        Assert.Equal(0, await mongo.Database.GetCollection<BsonDocument>("channel_admin_log").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));

        var restartedDatabase = new MongoClient(mongo.Database.Client.Settings).GetDatabase(mongo.Database.DatabaseNamespace.DatabaseName);
        using var restartedRuntime = new CompletionRuntime(mongo, project: true);
        var retryJob = await ClaimJobAsync(restartedDatabase);
        Assert.NotEqual(job["lease"], retryJob["lease"]);
        using var restarted = await ServiceProviderAsync(restartedDatabase, restartedRuntime.Commands,
            new DurableChannelPtsAllocator(restartedDatabase), retryJob, sender.Object);
        Assert.True(await ActivatorUtilities.CreateInstance<AntiSpamCompletionService>(restarted)
            .CompleteAsync(retryJob, default));
        Assert.Equal(failurePoint == "transport" ? 2 : 1, sent.Count);
        if (failurePoint == "transport") Assert.Equal(sent[0], sent[1]);
        var done = await ReadJobAsync(restartedDatabase);
        Assert.Equal(assignment, done["completion"].AsBsonDocument);
        Assert.True(done["transport_accepted"].AsBoolean);
        Assert.Equal("done", done["state"].AsString);
        Assert.Equal(1, await restartedDatabase.GetCollection<BsonDocument>("channel_admin_log").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    private static void VerifyNoPush(Mock<IObjectMessageSender> sender) =>
        sender.Verify(x => x.PushMessageToPeerAsync(It.IsAny<Peer>(), It.IsAny<TUpdates>(),
            It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<long?>(),
            It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<long>(), It.IsAny<MyTelegram.Core.PushData?>(),
            It.IsAny<List<long>?>()), Times.Never);

    private static string Operation(BsonDocument job) =>
        $"antispamcompletion-{Channel}-{Message}-{Guid.Parse(job["request"].AsString):N}";

    private static async Task<ServiceProvider> ServiceProviderAsync(IMongoDatabase database, ICommandBus commands,
        IChannelPtsAllocator allocator, BsonDocument job, IObjectMessageSender sender)
    {
        // This service integration fixture substitutes only message aggregate loading; durable
        // completion command/projection recovery is tested separately through real EventFlow above.
        var message = new MessageAggregate(MessageId.Create(Channel, Message));
        var snapshot = new MessageSnapshot(null!, [], Message, false, 0, false, 0, true,
            true, Guid.Parse(job["request"].AsString), 1);
        var load = typeof(MessageAggregate).GetMethod("LoadSnapshotAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)load.Invoke(message, [snapshot, null, CancellationToken.None])!;
        var aggregates = new Mock<IAggregateStore>(MockBehavior.Strict);
        aggregates.Setup(x => x.LoadAsync<MessageAggregate, MessageId>(It.IsAny<MessageId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(message);
        // The fixture explicitly captures no thread/mention side effects; absence is not proof.
        job["effects"] = new BsonDocument { ["version"] = 1, ["thread_root"] = 0, ["mentions"] = new BsonArray() };
        await database.GetCollection<BsonDocument>("antispam_jobs").UpdateOneAsync(new BsonDocument("_id", job["_id"]),
            new BsonDocument("$set", new BsonDocument("effects", job["effects"])));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(database);
        services.AddSingleton(commands);
        services.AddSingleton(allocator);
        services.AddSingleton(sender);
        services.AddSingleton(aggregates.Object);
        services.AddSingleton(Mock.Of<IIdGenerator>());
        services.AddSingleton(Mock.Of<IQueryProcessor>());
        services.AddSingleton(new Mock<IAntiSpamEffectProofReader>(MockBehavior.Strict).Object);
        services.AddSingleton(new Mock<IEventPersistence>(MockBehavior.Strict).Object);
        services.AddSingleton(new Mock<IEventJsonSerializer>(MockBehavior.Strict).Object);
        services.AddSingleton<AntiSpamLinkedPostPairValidator>();
        services.AddSingleton(new Mock<IMentionReadStateService>(MockBehavior.Strict).Object);
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new AntiSpamOptions()));
        services.AddTransient<AntiSpamJobStore>();
        services.AddSingleton<AntiSpamMentionRecountService>();
        services.AddSingleton<AntiSpamMentionAdmissionService>();
        services.AddTransient<AntiSpamDeletionEffectsService>();
        return services.BuildServiceProvider();
    }

    private static async Task<BsonDocument> ClaimJobAsync(IMongoDatabase database)
    {
        await database.GetCollection<BsonDocument>("antispam_jobs").UpdateOneAsync(
            new BsonDocument("_id", AntiSpamJobStore.JobId(Channel, Message)),
            new BsonDocument("$set", new BsonDocument("due", DateTime.UtcNow.AddSeconds(-1))));
        var store = new AntiSpamJobStore(database, Microsoft.Extensions.Options.Options.Create(new AntiSpamOptions()));
        var claimed = await store.ClaimAsync(default);
        Assert.NotNull(claimed);
        return claimed!;
    }

    private static Task<BsonDocument> ReadJobAsync(IMongoDatabase database) =>
        database.GetCollection<BsonDocument>("antispam_jobs").Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();

    private sealed class CompletionRuntime : IDisposable
    {
        private readonly ServiceProvider _services;
        public ICommandBus Commands => _services.GetRequiredService<ICommandBus>();
        public IAggregateStore Aggregates => _services.GetRequiredService<IAggregateStore>();

        public CompletionRuntime(EmbeddedMongoServer mongo, bool project)
        {
            MongoDbTestSerializers.EnsureRegistered();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IMongoDbContext>(new TestMongoDbContext(mongo.Database));
            services.AddReadModelMongoDbContext<TestMongoDbContext>();
            services.AddSingleton<AntiSpamCompletionUpdatesReadModelLocator>();
            services.AddEventFlow(options =>
            {
                options.ConfigureMongoDb(mongo.Client, mongo.Database.DatabaseNamespace.DatabaseName);
                options.UseMongoDbEventStore();
                options.AddEvents(typeof(AntiSpamCompletionAggregate).Assembly);
                options.AddCommandHandlers(typeof(CreateAntiSpamCompletionCommandHandler), typeof(PublishAntiSpamCompletionCommandHandler),
                    typeof(MyTelegram.Domain.Aggregates.Pts.UpdatePtsCommandHandler));
                if (project)
                {
                    // The same registrations as the command server, which commits these events.
                    MyTelegram.EventFlow.MongoDB.Extensions.MyMongoDbOptionsExtensions
                        .UseMongoDbReadModel<UpdatesReadModel, AntiSpamCompletionUpdatesReadModelLocator>(options);
                    options.UseMongoDbReadModel<PtsAggregate, PtsId, PtsReadModel>();
                }
            });
            _services = services.BuildServiceProvider();
        }

        public async Task CreateAsync(string operation, BsonDocument assignment) =>
            await Commands.PublishAsync(new CreateAntiSpamCompletionCommand(new AntiSpamCompletionId(operation),
                operation, Channel, Message, assignment["pts"].AsInt32, assignment["global_seq"].ToInt64(),
                assignment["date"].AsInt32, assignment["new_top"].AsInt32), default);

        /// <summary>The publication step of AntiSpamCompletionService: the completion, then the channel Pts.</summary>
        public async Task PublishAsync(string operation, BsonDocument assignment)
        {
            await Commands.PublishAsync(new PublishAntiSpamCompletionCommand(new AntiSpamCompletionId(operation)), default);
            await Commands.PublishAsync(new UpdatePtsCommand(PtsId.Create(Channel), Channel, 0, assignment["pts"].AsInt32,
                assignment["global_seq"].ToInt64(), 0, Message), default);
        }

        public async Task<IReadOnlyCollection<IDomainEvent<AntiSpamCompletionAggregate, AntiSpamCompletionId>>> EventsAsync(string operation) =>
            await _services.GetRequiredService<IEventStore>().LoadEventsAsync<AntiSpamCompletionAggregate, AntiSpamCompletionId>(
                new AntiSpamCompletionId(operation), default);

        public void Dispose() => _services.Dispose();
    }

    private sealed class TestMongoDbContext(IMongoDatabase database) : IMongoDbContext
    {
        public IMongoDatabase GetDatabase() => database;
    }
}
