using MyTelegram.Domain.Aggregates.Dialog;

namespace MyTelegram.Domain.Tests.UnitTests.Aggregates.Dialog;

public sealed class AntiSpamMentionEffectsTests : TestsFor<DialogAggregate>
{
    private static readonly Peer Channel = new(PeerType.Channel, 1001);
    public AntiSpamMentionEffectsTests() => Fixture.Customize<DialogId>(x => x.FromFactory(() => DialogId.Create(111, Channel)));

    [Fact]
    public async Task Journal_receipt_prevents_retry_after_ordinary_source_id_history_would_expire()
    {
        Sut.ApplyEvents([ADomainEvent<DialogAggregate, DialogId, AntiSpamMentionDeletionAppliedEvent>(
            new AntiSpamMentionDeletionAppliedEvent("job", 10, 0), 1)]);
        // A different mention arrived after the original receipt. Refresh must publish the
        // current scalar, not the scalar stored in the receipt and not another decrement.
        Sut.ApplyEvents([ADomainEvent<DialogAggregate, DialogId, MentionCreatedEvent>(
            new MentionCreatedEvent(111, Channel, 11, 1), 2)]);
        var proofs = new Mock<IAntiSpamEffectProofReader>(MockBehavior.Strict);
        proofs.Setup(x => x.FindReceiptAsync(Sut.Id.Value, "job", AntiSpamEffectKind.MentionDeletion,
                Sut.Version, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AntiSpamEffectReceipt(1, "job", AntiSpamEffectKind.MentionDeletion));

        await new ApplyAntiSpamMentionDeletionCommandHandler(proofs.Object).ExecuteAsync(Sut,
            new ApplyAntiSpamMentionDeletionCommand(Sut.Id, "job", 10, 0, false), default);

        var refresh = Sut.UncommittedEvents.Single().AggregateEvent
            .ShouldBeOfType<AntiSpamMentionDeletionAppliedEvent>();
        refresh.OperationKey.ShouldBe("job");
        refresh.MessageId.ShouldBe(10);
        refresh.UnreadMentionsCount.ShouldBe(1);
        proofs.VerifyAll();
    }

    [Fact]
    public async Task Ordinary_read_after_effect_commit_does_not_decrement_again()
    {
        Sut.ApplyEvents([ADomainEvent<DialogAggregate, DialogId, AntiSpamMentionDeletionAppliedEvent>(
            new AntiSpamMentionDeletionAppliedEvent("job", 10, 2), 1)]);
        var proofs = new Mock<IAntiSpamEffectProofReader>(MockBehavior.Strict);
        proofs.Setup(x => x.GetMentionConsumptionAsync(Sut.Id.Value, 10, Sut.Version,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MentionConsumptionProof(Sut.Version, 0, 1));

        await new ReadMentionCommandHandler(proofs.Object).ExecuteAsync(Sut,
            new ReadMentionCommand(Sut.Id, 10), default);

        Sut.UncommittedEvents.ShouldBeEmpty();
        proofs.VerifyAll();
    }

    [Fact]
    public async Task Ordinary_read_before_effect_commit_is_not_counted_twice()
    {
        Sut.ApplyEvents([ADomainEvent<DialogAggregate, DialogId, MentionReadEvent>(
            new MentionReadEvent(111, Channel, 10, 2), 1)]);
        var proofs = new Mock<IAntiSpamEffectProofReader>(MockBehavior.Strict);
        proofs.Setup(x => x.FindReceiptAsync(Sut.Id.Value, "job", AntiSpamEffectKind.MentionDeletion,
                Sut.Version, It.IsAny<CancellationToken>()))
            .ReturnsAsync((AntiSpamEffectReceipt?)null);
        proofs.Setup(x => x.GetMentionConsumptionAsync(Sut.Id.Value, 10, Sut.Version,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MentionConsumptionProof(Sut.Version, 0, 1));

        await new ApplyAntiSpamMentionDeletionCommandHandler(proofs.Object).ExecuteAsync(Sut,
            new ApplyAntiSpamMentionDeletionCommand(Sut.Id, "job", 10, 0, false), default);

        Sut.UncommittedEvents.Single().AggregateEvent.ShouldBeOfType<AntiSpamMentionDeletionAppliedEvent>()
            .UnreadMentionsCount.ShouldBe(2);
        proofs.VerifyAll();
    }
}
