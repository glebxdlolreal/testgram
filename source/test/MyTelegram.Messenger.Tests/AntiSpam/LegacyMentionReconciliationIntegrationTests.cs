using EventFlow;
using EventFlow.Aggregates;
using EventFlow.EventStores;
using EventFlow.Extensions;
using EventFlow.MongoDB.EventStore;
using EventFlow.MongoDB.Extensions;
using EventFlow.Queries;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Domain.Aggregates.Dialog;
using MyTelegram.Domain.Aggregates.Messaging;
using MyTelegram.EventFlow.MongoDB;
using MyTelegram.EventFlow.MongoDB.Extensions;
using MyTelegram.Messenger.NativeAot;
using MyTelegram.Messenger.Services.AntiSpam;
using MyTelegram.Messenger.Services.Mentions;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.QueryHandlers.MongoDB.Messaging;
using MessageReadModel = MyTelegram.ReadModel.MongoDB.MessageReadModel;

namespace MyTelegram.Messenger.Tests.AntiSpam;

/// <summary>
/// End-to-end coverage of <see cref="AntiSpamMentionAdmissionService.ReconcileLegacyAsync"/> against a
/// real MongoDB and a real EventFlow event store.
///
/// <para>The unit tests in <see cref="LegacyMentionReconciliationServiceTests"/> mock the aggregate and the
/// proof reader, so they cannot show that the recovery path actually closes. This fixture builds the
/// state the way production does: a genuine <see cref="DialogAggregate"/> is moved through create →
/// mention → admission by publishing commands, the index-backed proof is written by
/// <see cref="AntiSpamIndexedEventPersistence"/>, and the unread count the reconcile installs is the
/// answer of the production <c>GetUnreadMentionsCountQuery</c> handler over a projected
/// <see cref="MessageReadModel"/>.</para>
/// </summary>
public sealed class LegacyMentionReconciliationIntegrationTests
{
    private const long Channel = MyTelegramConsts.ChannelInitId + 977;
    private const long User = 2_000_977L;
    private const int Msg = 42;

    private static readonly Peer ChannelPeer = new(PeerType.Channel, Channel);

    /// <summary>
    /// A job document exactly as a pre-admission build wrote it: no <c>admission_protocol</c> marker,
    /// an <c>effects.mentions</c> array, and nothing that records a proof receipt.
    /// </summary>
    private static BsonDocument LegacyJob() => new()
    {
        ["_id"] = AntiSpamJobStore.JobId(Channel, Msg),
        ["channel"] = Channel,
        ["sender"] = 5_000_977L,
        ["message"] = Msg,
        ["fingerprint"] = "legacy-fingerprint",
        ["request"] = Guid.NewGuid().ToString(),
        ["state"] = "confirmed",
        ["attempts"] = 1,
        ["effects"] = new BsonDocument
        {
            ["version"] = 1,
            ["thread_root"] = 0,
            ["mentions"] = new BsonArray { new BsonDocument { ["user"] = User } }
        }
    };

    [RequiresMongoDbFact]
    public async Task Legacy_job_reconciles_through_a_real_aggregate_and_repairs_a_stale_mention_scalar()
    {
        using var mongo = EmbeddedMongoServer.Start();
        using var runtime = new DialogRuntime(mongo);

        var dialogId = DialogId.Create(User, ChannelPeer);
        var job = LegacyJob();
        var operation = AntiSpamMentionAdmissionService.Operation(job);
        await runtime.StoreJobAsync(job);

        // Drive the aggregate the way the handlers do: create, mention, then admit the moderation
        // reservation. Every step persists a real event into the real event store.
        await runtime.CreateDialogAsync(dialogId);
        await runtime.CreateMentionAsync(dialogId, Msg);
        var version = (await runtime.LoadAsync(dialogId)).Version;
        await runtime.AdmitAsync(dialogId, operation, Msg, version);

        // The message carries the mention, and the reader has already consumed it. The dialog's
        // committed scalar still says 1, which is exactly the drift a legacy job leaves behind.
        await runtime.ProjectMentionAsync(Msg);
        await runtime.MarkAllMentionsReadAsync(Msg);

        var reconciled = await runtime.Admission.ReconcileLegacyAsync(job, default);

        Assert.True(reconciled, "a legacy job with a matching admission must reconcile");
        var dialog = await runtime.LoadAsync(dialogId);
        Assert.Equal(Msg, dialog.AntiSpamMentionMessageId);
        Assert.Equal(operation, dialog.AntiSpamMentionOperation);
        var applied = Assert.Single((await runtime.EventsAsync(dialogId))
            .Select(x => x.GetAggregateEvent()).OfType<AntiSpamMentionDeletionAppliedEvent>());
        Assert.Equal(operation, applied.OperationKey);
        Assert.Equal(Msg, applied.MessageId);
        Assert.Equal(0, applied.UnreadMentionsCount);

        // The evidence is durable, not merely in-memory: a fresh reader sees the committed receipt.
        var receipt = await new AntiSpamEffectProofReader(mongo.Database).FindReceiptAsync(
            dialogId.Value, operation, AntiSpamEffectKind.MentionDeletion, dialog.Version, default);
        Assert.NotNull(receipt);
        Assert.Equal(dialog.Version, receipt!.AggregateVersion);

        // The production job store is what the worker reads back, so assert against its shape too.
        var stored = await mongo.Database.GetCollection<BsonDocument>("antispam_jobs")
            .Find(new BsonDocument("_id", job["_id"])).SingleAsync();
        Assert.False(stored.Contains("admission_protocol"));
        Assert.Equal(job["request"].AsString, stored["request"].AsString);
    }

    [RequiresMongoDbFact]
    public async Task Second_reconcile_is_a_no_op_because_the_receipt_is_already_durable()
    {
        using var mongo = EmbeddedMongoServer.Start();
        using var runtime = new DialogRuntime(mongo);

        var dialogId = DialogId.Create(User, ChannelPeer);
        var job = LegacyJob();
        var operation = AntiSpamMentionAdmissionService.Operation(job);
        await runtime.CreateDialogAsync(dialogId);
        await runtime.CreateMentionAsync(dialogId, Msg);
        await runtime.AdmitAsync(dialogId, operation, Msg, (await runtime.LoadAsync(dialogId)).Version);
        await runtime.MarkAllMentionsReadAsync(Msg);

        Assert.True(await runtime.Admission.ReconcileLegacyAsync(job, default));
        var afterFirst = (await runtime.LoadAsync(dialogId)).Version;

        Assert.True(await runtime.Admission.ReconcileLegacyAsync(job, default));
        var afterSecond = await runtime.LoadAsync(dialogId);
        Assert.Equal(afterFirst, afterSecond.Version);
        // Exactly one deletion event exists across both passes: the second saw the durable receipt.
        Assert.Single((await runtime.EventsAsync(dialogId))
            .Select(x => x.GetAggregateEvent()).OfType<AntiSpamMentionDeletionAppliedEvent>());
    }

    /// <summary>A real EventFlow runtime over a throwaway mongod, wired the way the command server wires it.</summary>
    private sealed class DialogRuntime : IDisposable
    {
        private readonly ServiceProvider _services;
        private readonly IMongoDatabase _database;

        public ICommandBus Commands => _services.GetRequiredService<ICommandBus>();
        public AntiSpamMentionAdmissionService Admission => _services.GetRequiredService<AntiSpamMentionAdmissionService>();

        public DialogRuntime(EmbeddedMongoServer mongo)
        {
            MongoDbTestSerializers.EnsureRegistered();
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
                options.AddEvents(typeof(DialogAggregate).Assembly);
                options.AddCommandHandlers(typeof(CreateDialogCommandHandler), typeof(CreateMentionCommandHandler),
                    typeof(TryAdmitAntiSpamMentionCommandHandler), typeof(ReconcileAntiSpamMentionDeletionCommandHandler),
                    typeof(CreateOutboxMessageCommandHandler));
                // The real projection and the real count handler: the reconciled scalar is the answer of
                // production code, not a stubbed number.
                options.UseMongoDbReadModel<MessageAggregate, MessageId, MessageReadModel>();
                options.AddQueryHandlers(typeof(GetUnreadMentionsCountQueryHandler));
            });
            // Production swaps the stock persistence for the one that stores the proof fields atomically.
            Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.Replace(
                services, ServiceDescriptor.Transient<IEventPersistence, AntiSpamIndexedEventPersistence>());
            services.AddSingleton<AntiSpamEffectProofReader>();
            services.AddSingleton<IAntiSpamEffectProofReader>(sp => sp.GetRequiredService<AntiSpamEffectProofReader>());
            services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new AntiSpamOptions()));
            services.AddSingleton<IMentionReadStateService, MentionReadStateService>();
            services.AddSingleton<AntiSpamJobStore>();
            services.AddSingleton<AntiSpamMentionRecountService>();
            services.AddSingleton<AntiSpamMentionAdmissionService>();
            _services = services.BuildServiceProvider();
        }

        private static RequestInfo Request(string id) => RequestInfo.Empty with
        {
            RequestId = Guid.Parse(id), ReqMsgId = Random.Shared.NextInt64(1, long.MaxValue),
            UserId = User, Date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), AddRequestIdToCache = false
        };

        public Task CreateDialogAsync(DialogId id) => Commands.PublishAsync(new CreateDialogCommand(id,
            Request(Guid.NewGuid().ToString("N")), User, ChannelPeer, 0, Msg), default);

        public Task CreateMentionAsync(DialogId id, int messageId) =>
            Commands.PublishAsync(new CreateMentionCommand(id, messageId), default);

        public Task AdmitAsync(DialogId id, string operation, int messageId, int version) =>
            Commands.PublishAsync(new TryAdmitAntiSpamMentionCommand(id, operation, messageId, version, true), default);

        /// <summary>Writes the legacy document the way a pre-admission worker left it behind.</summary>
        public Task StoreJobAsync(BsonDocument job) =>
            _database.GetCollection<BsonDocument>("antispam_jobs").InsertOneAsync(job);

        public Task<DialogAggregate> LoadAsync(DialogId id) =>
            _services.GetRequiredService<IAggregateStore>().LoadAsync<DialogAggregate, DialogId>(id, default);

        public async Task<IReadOnlyCollection<IDomainEvent<DialogAggregate, DialogId>>> EventsAsync(DialogId id) =>
            await _services.GetRequiredService<IEventStore>().LoadEventsAsync<DialogAggregate, DialogId>(id, default);

        /// <summary>
        /// Writes the mention through the production projection so the count query reads a real read
        /// model. Published rather than inserted as BSON, so the field names are the aggregate's, not
        /// this test's guess at them.
        /// </summary>
        public Task ProjectMentionAsync(int messageId) =>
            Commands.PublishAsync(new CreateOutboxMessageCommand(MessageId.Create(Channel, messageId),
                Request(Guid.NewGuid().ToString("N")), new MessageItem(
                    OwnerPeer: ChannelPeer,
                    ToPeer: new Peer(PeerType.User, User),
                    SenderPeer: new Peer(PeerType.Channel, Channel),
                    SenderUserId: User,
                    MessageId: messageId,
                    Message: $"hello @{User}",
                    Date: (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    RandomId: Random.Shared.NextInt64(1, long.MaxValue),
                    IsOut: false,
                    Post: true),
                [User]), default);

        /// <summary>Marks every mention up to <paramref name="readMaxId"/> read in the durable read state.</summary>
        public Task MarkAllMentionsReadAsync(int readMaxId) =>
            _services.GetRequiredService<IMentionReadStateService>()
                .MarkAllReadAsync(User, ChannelPeer, readMaxId);

        public void Dispose() => _services.Dispose();
    }

    private sealed class TestMongoDbContext(IMongoDatabase database) : IMongoDbContext
    {
        public IMongoDatabase GetDatabase() => database;
    }
}
