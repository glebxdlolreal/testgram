using MyTelegram.Domain.Aggregates.Dialog;

namespace MyTelegram.Domain.Tests.UnitTests.Aggregates.Dialog;

public sealed class LegacyMentionReconciliationTests : TestsFor<DialogAggregate>
{
    private static readonly Peer Channel = new(PeerType.Channel, 1001);
    public LegacyMentionReconciliationTests() => Fixture.Customize<DialogId>(x => x.FromFactory(() => DialogId.Create(111, Channel)));

    private void Seed()
    {
        Sut.ApplyEvents([ADomainEvent<DialogAggregate, DialogId, MentionCreatedEvent>(
            new MentionCreatedEvent(111, Channel, 10, 3), 1)]);
    }

    private Task Admit(string key, int messageId) => new TryAdmitAntiSpamMentionCommandHandler().ExecuteAsync(Sut,
        new TryAdmitAntiSpamMentionCommand(Sut.Id, key, messageId, Sut.Version, false), default);

    private async Task Reconcile(string key, int messageId, int version, int count, bool receipt = false)
    {
        var proofs = new Mock<IAntiSpamEffectProofReader>(MockBehavior.Strict);
        proofs.Setup(x => x.FindReceiptAsync(Sut.Id.Value, key, AntiSpamEffectKind.MentionDeletion,
            Sut.Version, It.IsAny<CancellationToken>())).ReturnsAsync(receipt
            ? new AntiSpamEffectReceipt(1, key, AntiSpamEffectKind.MentionDeletion) : null);
        await new ReconcileAntiSpamMentionDeletionCommandHandler(proofs.Object).ExecuteAsync(Sut,
            new ReconcileAntiSpamMentionDeletionCommand(Sut.Id, key, messageId, version, count), default);
    }

    [Fact]
    public async Task Two_legacy_deletions_each_replace_count_without_second_decrement_and_keep_admission()
    {
        Seed();
        await Admit("first", 10);
        await Reconcile("first", 10, Sut.Version, 2);
        Sut.UncommittedEvents.Last().AggregateEvent.ShouldBeOfType<AntiSpamMentionDeletionAppliedEvent>()
            .UnreadMentionsCount.ShouldBe(2);
        Sut.AntiSpamMentionOperation.ShouldBe("first");
        Sut.AntiSpamMentionConsumed.ShouldBeTrue();
        await new ReleaseAntiSpamMentionAdmissionCommandHandler().ExecuteAsync(Sut,
            new ReleaseAntiSpamMentionAdmissionCommand(Sut.Id, "first"), default);
        await Admit("second", 11);
        await Reconcile("second", 11, Sut.Version, 1);
        Sut.UncommittedEvents.Last().AggregateEvent.ShouldBeOfType<AntiSpamMentionDeletionAppliedEvent>()
            .UnreadMentionsCount.ShouldBe(1);
        Sut.AntiSpamMentionOperation.ShouldBe("second");
    }

    [Fact]
    public async Task Stale_version_cannot_install_captured_count()
    {
        Seed();
        var captured = Sut.Version;
        await Admit("job", 10);
        await Reconcile("job", 10, captured, 0);
        Sut.UncommittedEvents.ShouldNotContain(x => x.AggregateEvent is AntiSpamMentionDeletionAppliedEvent);
    }

    [Fact]
    public async Task Read_marker_gap_refuses_legacy_reconcile_even_with_matching_version_and_reservation()
    {
        Seed();
        await Admit("job", 10);
        await new TryAdmitMentionReadIntentCommandHandler().ExecuteAsync(Sut,
            new TryAdmitMentionReadIntentCommand(Sut.Id, "read-B", Sut.Version), default);
        var proofs = new Mock<IAntiSpamEffectProofReader>(MockBehavior.Strict);
        await new ReconcileAntiSpamMentionDeletionCommandHandler(proofs.Object).ExecuteAsync(Sut,
            new ReconcileAntiSpamMentionDeletionCommand(Sut.Id, "job", 10, Sut.Version, 0), default);
        Sut.UncommittedEvents.ShouldNotContain(x => x.AggregateEvent is AntiSpamMentionDeletionAppliedEvent);
        Sut.MentionReadOperation.ShouldBe("read-B");
    }

    [Theory]
    [InlineData("other", 10)]
    [InlineData("job", 11)]
    public async Task Wrong_owner_or_message_cannot_reconcile(string key, int messageId)
    {
        Seed();
        await Admit("job", 10);
        var proofs = new Mock<IAntiSpamEffectProofReader>(MockBehavior.Strict);
        await new ReconcileAntiSpamMentionDeletionCommandHandler(proofs.Object).ExecuteAsync(Sut,
            new ReconcileAntiSpamMentionDeletionCommand(Sut.Id, key, messageId, Sut.Version, 0), default);
        Sut.UncommittedEvents.ShouldNotContain(x => x.AggregateEvent is AntiSpamMentionDeletionAppliedEvent);
    }

    [Fact]
    public async Task Existing_receipt_refreshes_current_count_not_stale_recount_snapshot()
    {
        Seed();
        await Admit("job", 10);
        await Reconcile("job", 10, Sut.Version, 2);
        Sut.CreateMention(12);
        await Reconcile("job", 10, 1, 0, receipt: true);
        Sut.UncommittedEvents.Last().AggregateEvent.ShouldBeOfType<AntiSpamMentionDeletionAppliedEvent>()
            .UnreadMentionsCount.ShouldBe(3);
        Sut.AntiSpamMentionOperation.ShouldBe("job");
    }
}
