using MyTelegram.Domain.Aggregates.Dialog;

namespace MyTelegram.Domain.Tests.UnitTests.Aggregates.Dialog;

public sealed class MentionReadIntentTests : TestsFor<DialogAggregate>
{
    private static readonly Peer Channel = new(PeerType.Channel, 1001);
    public MentionReadIntentTests() => Fixture.Customize<DialogId>(x => x.FromFactory(() => DialogId.Create(111, Channel)));

    private void Seed(int count = 3)
    {
        Sut.ApplyEvents([ADomainEvent<DialogAggregate, DialogId, MentionCreatedEvent>(
            new MentionCreatedEvent(111, Channel, 10, count), 1)]);
    }

    private Task Admit(string key, int? version = null) => new TryAdmitMentionReadIntentCommandHandler().ExecuteAsync(
        Sut, new TryAdmitMentionReadIntentCommand(Sut.Id, key, version ?? Sut.Version), default);

    private Task Release(string key) => new ReleaseMentionReadIntentCommandHandler().ExecuteAsync(
        Sut, new ReleaseMentionReadIntentCommand(Sut.Id, key), default);

    private Mock<IAntiSpamEffectProofReader> Proofs(string key, AntiSpamEffectReceipt? receipt = null)
    {
        var proofs = new Mock<IAntiSpamEffectProofReader>(MockBehavior.Strict);
        proofs.Setup(x => x.FindReceiptAsync(Sut.Id.Value, key, AntiSpamEffectKind.MentionReadIntent,
            Sut.Version, It.IsAny<CancellationToken>())).ReturnsAsync(receipt);
        return proofs;
    }

    [Fact]
    public async Task B_marker_C_counter_gap_blocks_recount_and_second_read_until_matching_release()
    {
        Seed();
        await Admit("B");
        var before = Sut.UncommittedEvents.Count();
        await Admit("C");
        await Release("C");
        Sut.UncommittedEvents.Count().ShouldBe(before);
        Sut.MentionReadOperation.ShouldBe("B");
        // B's external marker is now visible, but B's counter receipt is not yet appended.
        Sut.SyncUnreadMentionsCount(1, Sut.Version);
        Sut.UncommittedEvents.ShouldNotContain(x => x.AggregateEvent is UnreadMentionsCountSyncedEvent);
        Sut.MentionRecountRequired.ShouldBeTrue();
        await Release("B");
        Sut.MentionReadOperation.ShouldBeNull();
        Sut.UncommittedEvents.Last().AggregateEvent.ShouldBeOfType<MentionReadIntentReleasedEvent>();
        await Admit("C");
        Sut.MentionReadOperation.ShouldBe("C");
    }

    [Fact]
    public async Task Stale_admission_and_pre_gate_recount_are_rejected_after_release()
    {
        Seed();
        var oldVersion = Sut.Version;
        await Admit("B");
        await Release("B");
        await Admit("late", oldVersion);
        Sut.MentionReadOperation.ShouldBeNull();
        Sut.SyncUnreadMentionsCount(0, oldVersion);
        Sut.UncommittedEvents.ShouldNotContain(x => x.AggregateEvent is UnreadMentionsCountSyncedEvent);
    }

    [Fact]
    public async Task Read_all_effect_and_receipt_share_append_and_retry_preserves_new_mentions()
    {
        Seed();
        await Admit("all");
        var proofs = Proofs("all");
        await new ApplyMentionReadIntentCommandHandler(proofs.Object).ExecuteAsync(Sut,
            new ApplyMentionReadIntentCommand(Sut.Id, "all", [], true), default);
        Sut.UncommittedEvents.Count(x => x.AggregateEvent is UnreadMentionsReadEvent).ShouldBe(1);
        Sut.UncommittedEvents.Last().AggregateEvent.ShouldBeOfType<MentionReadIntentAppliedEvent>();
        Sut.MentionReadOperation.ShouldBe("all");

        // Reload the committed receipt after a crash before marker/release, with a newer mention.
        var reloaded = new DialogAggregate(Sut.Id);
        reloaded.ApplyEvents([
            ADomainEvent<DialogAggregate, DialogId, MentionReadIntentAdmittedEvent>(new MentionReadIntentAdmittedEvent("all"), 1),
            ADomainEvent<DialogAggregate, DialogId, UnreadMentionsReadEvent>(new UnreadMentionsReadEvent(111, Channel, 0), 2),
            ADomainEvent<DialogAggregate, DialogId, MentionReadIntentAppliedEvent>(new MentionReadIntentAppliedEvent("all"), 3),
            ADomainEvent<DialogAggregate, DialogId, MentionCreatedEvent>(new MentionCreatedEvent(111, Channel, 11, 1), 4)]);
        var retryProofs = new Mock<IAntiSpamEffectProofReader>(MockBehavior.Strict);
        retryProofs.Setup(x => x.FindReceiptAsync(Sut.Id.Value, "all", AntiSpamEffectKind.MentionReadIntent,
            4, It.IsAny<CancellationToken>())).ReturnsAsync(new AntiSpamEffectReceipt(3, "all", AntiSpamEffectKind.MentionReadIntent));
        await new ApplyMentionReadIntentCommandHandler(retryProofs.Object).ExecuteAsync(reloaded,
            new ApplyMentionReadIntentCommand(Sut.Id, "all", [], true), default);
        reloaded.UncommittedEvents.ShouldBeEmpty();
        proofs.VerifyAll();
        retryProofs.VerifyAll();
    }

    [Fact]
    public async Task Individual_reads_deduplicate_and_require_exact_committed_proof_prefix_before_any_emission()
    {
        Seed();
        await Admit("ids");
        var version = Sut.Version;
        var proofs = Proofs("ids");
        proofs.Setup(x => x.GetMentionConsumptionAsync(Sut.Id.Value, 10, version, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MentionConsumptionProof(version, 1, null));
        proofs.Setup(x => x.GetMentionConsumptionAsync(Sut.Id.Value, 11, version, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MentionConsumptionProof(version, 1, 2));
        proofs.Setup(x => x.GetMentionConsumptionAsync(Sut.Id.Value, 12, version, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MentionConsumptionProof(version, 1, null));
        await new ApplyMentionReadIntentCommandHandler(proofs.Object).ExecuteAsync(Sut,
            new ApplyMentionReadIntentCommand(Sut.Id, "ids", [10, 10, 11, 12], false), default);
        var reads = Sut.UncommittedEvents.Select(x => x.AggregateEvent).OfType<MentionReadEvent>().ToArray();
        reads.Select(x => x.MessageId).ShouldBe([10, 12]);
        reads.Select(x => x.UnreadMentionsCount).ShouldBe([2, 1]);
        Sut.UncommittedEvents.Last().AggregateEvent.ShouldBeOfType<MentionReadIntentAppliedEvent>();
        proofs.Verify(x => x.GetMentionConsumptionAsync(Sut.Id.Value, 10, version, It.IsAny<CancellationToken>()), Times.Once);
        proofs.VerifyAll();
    }

    [Fact]
    public async Task Stale_individual_proof_fails_without_partial_counter_events_or_receipt()
    {
        Seed();
        await Admit("ids");
        var before = Sut.UncommittedEvents.Count();
        var version = Sut.Version;
        var proofs = Proofs("ids");
        proofs.Setup(x => x.GetMentionConsumptionAsync(Sut.Id.Value, 10, version, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MentionConsumptionProof(version, 1, null));
        proofs.Setup(x => x.GetMentionConsumptionAsync(Sut.Id.Value, 11, version, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MentionConsumptionProof(version - 1, 1, null));
        await Should.ThrowAsync<InvalidOperationException>(() => new ApplyMentionReadIntentCommandHandler(proofs.Object)
            .ExecuteAsync(Sut, new ApplyMentionReadIntentCommand(Sut.Id, "ids", [10, 11], false), default));
        Sut.UncommittedEvents.Count().ShouldBe(before);
    }

    [Fact]
    public void Snapshot_restores_independent_read_gate_and_old_snapshots_default_to_no_gate()
    {
        var state = new DialogState();
        state.LoadSnapshot(new DialogSnapshot(111, 0, 0, 0, 0, Channel, false, false, 0, null, 3, null,
            antiSpamMentionOperation: "deletion", antiSpamMentionMessageId: 10, mentionReadOperation: "read"));
        state.MentionReadOperation.ShouldBe("read");
        state.AntiSpamMentionOperation.ShouldBe("deletion");
        state.Apply(new MentionReadIntentAppliedEvent("read"));
        state.MentionReadOperation.ShouldBe("read");
        state.Apply(new MentionReadIntentReleasedEvent("other"));
        state.MentionReadOperation.ShouldBe("read");
        state.Apply(new MentionReadIntentReleasedEvent("read"));
        state.MentionReadOperation.ShouldBeNull();
        state.AntiSpamMentionOperation.ShouldBe("deletion");
        state.LoadSnapshot(new DialogSnapshot(111, 0, 0, 0, 0, Channel, false, false, 0, null, 3, null));
        state.MentionReadOperation.ShouldBeNull();
    }

    [Fact]
    public async Task Empty_terminal_intent_receipts_without_clearing_newer_mentions()
    {
        Seed();
        await Admit("terminal");
        var proofs = Proofs("terminal");
        await new ApplyMentionReadIntentCommandHandler(proofs.Object).ExecuteAsync(Sut,
            new ApplyMentionReadIntentCommand(Sut.Id, "terminal", [], false), default);
        Sut.UncommittedEvents.ShouldNotContain(x => x.AggregateEvent is MentionReadEvent || x.AggregateEvent is UnreadMentionsReadEvent);
        Sut.UncommittedEvents.Last().AggregateEvent.ShouldBeOfType<MentionReadIntentAppliedEvent>();
    }
}
