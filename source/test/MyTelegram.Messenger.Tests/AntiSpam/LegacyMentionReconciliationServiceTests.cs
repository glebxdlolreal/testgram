using EventFlow;
using EventFlow.Aggregates;
using EventFlow.Aggregates.ExecutionResults;
using EventFlow.Queries;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using Moq;
using MyTelegram.Domain.Aggregates.Dialog;
using MyTelegram.Messenger.Services.AntiSpam;
using MyTelegram.Messenger.Services.Mentions;
using MyTelegram.Queries;
using MyTelegram.TestBase;

namespace MyTelegram.Messenger.Tests.AntiSpam;

/// <summary>
/// Unit-level coverage for <see cref="AntiSpamMentionAdmissionService.ReconcileLegacyAsync"/>.
/// Uses mocked aggregates and proof reader so these run without a MongoDB instance.
/// </summary>
public sealed class LegacyMentionReconciliationServiceTests
{
    private const long Channel = MyTelegramConsts.ChannelInitId + 500;
    private const long User = 12345L;
    private const int Msg = 42;

    private static BsonDocument LegacyJob(string? id = null, string? request = null) =>
        new BsonDocument
        {
            ["_id"] = id ?? Guid.NewGuid().ToString("N"),
            ["channel"] = Channel,
            ["message"] = Msg,
            ["request"] = request ?? Guid.NewGuid().ToString("N"),
            ["effects"] = new BsonDocument
            {
                ["mentions"] = new BsonArray { new BsonDocument { ["user"] = User } }
            }
        };

    private static string Op(BsonDocument job) =>
        $"{job["_id"].AsString}-{job["request"].AsString}";

    // Seeds a DialogAggregate with the correct antispam admission state by replaying events.
    // We call DialogState.Apply() directly through the aggregate's public event-replay path.
    private static DialogAggregate DialogWithAdmission(DialogId id, string operation, bool readOpActive = false)
    {
        var peer = new Peer(PeerType.Channel, Channel);
        var agg = new DialogAggregate(id);
        // Apply events using the TestBase helper to produce IDomainEvent instances.
        var tb = new ConcreteTestBase();
        agg.ApplyEvents([tb.Build<DialogAggregate, DialogId, MentionCreatedEvent>(
            id, new MentionCreatedEvent(User, peer, Msg, 1), 1)]);
        agg.ApplyEvents([tb.Build<DialogAggregate, DialogId, AntiSpamMentionAdmittedEvent>(
            id, new AntiSpamMentionAdmittedEvent(operation, Msg, false), 2)]);
        if (readOpActive)
            agg.ApplyEvents([tb.Build<DialogAggregate, DialogId, MentionReadIntentAdmittedEvent>(
                id, new MentionReadIntentAdmittedEvent("read-op"), 3)]);
        return agg;
    }

    private static AntiSpamMentionAdmissionService BuildSvc(
        IAggregateStore aggregates,
        ICommandBus commands,
        IAntiSpamEffectProofReader proofs)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(aggregates);
        services.AddSingleton(commands);
        services.AddSingleton(proofs);
        var readState = new Mock<IMentionReadStateService>();
        readState.Setup(x => x.GetAsync(It.IsAny<long>(), It.IsAny<Peer>()))
                 .ReturnsAsync((MentionReadStateDocument?)null);
        services.AddSingleton(readState.Object);
        var queries = new Mock<IQueryProcessor>();
        queries.Setup(x => x.ProcessAsync(It.IsAny<GetUnreadMentionsCountQuery>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(2);
        services.AddSingleton(queries.Object);
        services.AddSingleton(new Mock<MyTelegram.Services.Services.IObjectMessageSender>().Object);
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new AntiSpamOptions()));
        var db = new Mock<MongoDB.Driver.IMongoDatabase>(MockBehavior.Loose);
        services.AddSingleton(db.Object);
        services.AddTransient<AntiSpamJobStore>();
        services.AddSingleton<AntiSpamMentionRecountService>();
        services.AddSingleton<AntiSpamMentionAdmissionService>();
        return services.BuildServiceProvider().GetRequiredService<AntiSpamMentionAdmissionService>();
    }

    [Fact]
    public async Task Happy_path_legacy_job_publishes_reconcile_and_returns_true()
    {
        var job = LegacyJob();
        var operation = Op(job);
        var peer = new Peer(PeerType.Channel, Channel);
        var id = DialogId.Create(User, peer);
        var dialog = DialogWithAdmission(id, operation);

        var aggregates = new Mock<IAggregateStore>(MockBehavior.Strict);
        aggregates.SetupSequence(x => x.LoadAsync<DialogAggregate, DialogId>(id, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(dialog)
                  .ReturnsAsync(dialog);

        var published = new List<ReconcileAntiSpamMentionDeletionCommand>();
        var commands = new Mock<ICommandBus>(MockBehavior.Loose);
        commands.Setup(x => x.PublishAsync(It.IsAny<ReconcileAntiSpamMentionDeletionCommand>(),
                    It.IsAny<CancellationToken>()))
                .Callback<global::EventFlow.Commands.ICommand<DialogAggregate, DialogId, IExecutionResult>, CancellationToken>(
                    (c, _) => published.Add((ReconcileAntiSpamMentionDeletionCommand)c))
                .ReturnsAsync(ExecutionResult.Success());

        var proofs = new Mock<IAntiSpamEffectProofReader>(MockBehavior.Strict);
        proofs.SetupSequence(x => x.FindReceiptAsync(id.Value, operation,
                    AntiSpamEffectKind.MentionDeletion, It.IsAny<int>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((AntiSpamEffectReceipt?)null)
              .ReturnsAsync(new AntiSpamEffectReceipt(2, operation, AntiSpamEffectKind.MentionDeletion));

        var svc = BuildSvc(aggregates.Object, commands.Object, proofs.Object);
        var result = await svc.ReconcileLegacyAsync(job, default);

        Assert.True(result);
        var cmd = Assert.Single(published);
        Assert.Equal(id, cmd.AggregateId);
        Assert.Equal(operation, cmd.OperationKey);
        Assert.Equal(Msg, cmd.MessageId);
        Assert.Equal(2, cmd.UnreadCount);
    }

    [Fact]
    public async Task Missing_effects_field_returns_false()
    {
        var job = new BsonDocument
        {
            ["_id"] = Guid.NewGuid().ToString("N"),
            ["channel"] = Channel,
            ["message"] = Msg,
            ["request"] = Guid.NewGuid().ToString("N")
        };
        var svc = BuildSvc(new Mock<IAggregateStore>().Object,
                           new Mock<ICommandBus>().Object,
                           new Mock<IAntiSpamEffectProofReader>().Object);
        Assert.False(await svc.ReconcileLegacyAsync(job, default));
    }

    [Fact]
    public async Task IsNew_dialog_returns_false()
    {
        var job = LegacyJob();
        var peer = new Peer(PeerType.Channel, Channel);
        var id = DialogId.Create(User, peer);
        var isNew = new DialogAggregate(id);
        var aggregates = new Mock<IAggregateStore>(MockBehavior.Strict);
        aggregates.Setup(x => x.LoadAsync<DialogAggregate, DialogId>(id, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(isNew);
        var svc = BuildSvc(aggregates.Object, new Mock<ICommandBus>().Object, new Mock<IAntiSpamEffectProofReader>().Object);
        Assert.False(await svc.ReconcileLegacyAsync(job, default));
    }

    [Fact]
    public async Task Active_read_intent_blocks_reconciliation()
    {
        var job = LegacyJob();
        var operation = Op(job);
        var peer = new Peer(PeerType.Channel, Channel);
        var id = DialogId.Create(User, peer);
        var dialog = DialogWithAdmission(id, operation, readOpActive: true);
        var aggregates = new Mock<IAggregateStore>(MockBehavior.Strict);
        aggregates.Setup(x => x.LoadAsync<DialogAggregate, DialogId>(id, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(dialog);
        var svc = BuildSvc(aggregates.Object, new Mock<ICommandBus>().Object, new Mock<IAntiSpamEffectProofReader>().Object);
        Assert.False(await svc.ReconcileLegacyAsync(job, default));
    }

    [Fact]
    public async Task Wrong_operation_in_dialog_returns_false()
    {
        var job = LegacyJob();
        var peer = new Peer(PeerType.Channel, Channel);
        var id = DialogId.Create(User, peer);
        var dialog = DialogWithAdmission(id, "different-op");
        var aggregates = new Mock<IAggregateStore>(MockBehavior.Strict);
        aggregates.Setup(x => x.LoadAsync<DialogAggregate, DialogId>(id, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(dialog);
        var svc = BuildSvc(aggregates.Object, new Mock<ICommandBus>().Object, new Mock<IAntiSpamEffectProofReader>().Object);
        Assert.False(await svc.ReconcileLegacyAsync(job, default));
    }

    [Fact]
    public async Task Existing_receipt_skips_command_and_returns_true()
    {
        var job = LegacyJob();
        var operation = Op(job);
        var peer = new Peer(PeerType.Channel, Channel);
        var id = DialogId.Create(User, peer);
        var dialog = DialogWithAdmission(id, operation);
        var aggregates = new Mock<IAggregateStore>(MockBehavior.Strict);
        aggregates.Setup(x => x.LoadAsync<DialogAggregate, DialogId>(id, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(dialog);
        var proofs = new Mock<IAntiSpamEffectProofReader>(MockBehavior.Strict);
        proofs.Setup(x => x.FindReceiptAsync(id.Value, operation, AntiSpamEffectKind.MentionDeletion,
                    It.IsAny<int>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AntiSpamEffectReceipt(2, operation, AntiSpamEffectKind.MentionDeletion));
        // Strict — no publish calls.
        var commands = new Mock<ICommandBus>(MockBehavior.Strict);
        var svc = BuildSvc(aggregates.Object, commands.Object, proofs.Object);
        Assert.True(await svc.ReconcileLegacyAsync(job, default));
    }

    // Minimal concrete subclass that exposes the DomainEventFactory helper without the Mock<T> conflict.
    // ADomainEvent<> is avoided deliberately: it builds Metadata from AutoFixture, which leaves
    // source_id empty and EventFlow then throws MetadataParseException while replaying.
    private sealed class ConcreteTestBase : MyTelegramTestBase
    {
        public IDomainEvent<TAggregate, TIdentity> Build<TAggregate, TIdentity, TAggregateEvent>(
            TIdentity identity, TAggregateEvent aggregateEvent, int seqNo)
            where TAggregate : IAggregateRoot<TIdentity>
            where TIdentity : global::EventFlow.Core.IIdentity
            where TAggregateEvent : IAggregateEvent
            => ToDomainEvent<TAggregate, TIdentity, TAggregateEvent>(identity, aggregateEvent, seqNo);
    }
}
