using MyTelegram.Domain.Aggregates.Dialog;

namespace MyTelegram.Domain.Tests.UnitTests.Aggregates.Dialog;

public class DialogMentionAdmissionTests : TestsFor<DialogAggregate>
{
    private static readonly Peer Channel = new(PeerType.Channel, 1001);

    public DialogMentionAdmissionTests()
    {
        Fixture.Customize<DialogId>(x => x.FromFactory(() => DialogId.Create(111, Channel)));
    }

    [Fact]
    public void Recount_cannot_consume_a_reserved_deletion()
    {
        Create();
        Apply(new AntiSpamMentionAdmittedEvent("op", 10, false));
        Sut.SyncUnreadMentionsCount(1, Sut.Version);
        Sut.UncommittedEvents.Last().AggregateEvent.ShouldBeOfType<MentionRecountRequestedEvent>();
        Sut.AntiSpamMentionConsumed.ShouldBeFalse();
    }

    [Fact]
    public void Recount_captured_before_admission_stays_invalid_after_release()
    {
        Create();
        var captured = Sut.Version;
        Apply(new AntiSpamMentionAdmittedEvent("op", 10, false));
        Apply(new AntiSpamMentionAdmissionReleasedEvent("op"));
        var before = Sut.UncommittedEvents.Count();
        Sut.SyncUnreadMentionsCount(0, captured);
        Sut.UncommittedEvents.Count().ShouldBe(before);
        Sut.MentionRecountRequired.ShouldBeTrue();
    }

    [Fact]
    public void Ordinary_read_consumes_admitted_mention_once()
    {
        Create();
        Apply(new AntiSpamMentionAdmittedEvent("op", 10, false));
        Sut.ReadMention(10);
        var read = Sut.UncommittedEvents.Last().AggregateEvent.ShouldBeOfType<MentionReadEvent>();
        read.UnreadMentionsCount.ShouldBe(1);
        Apply(read);
        var before = Sut.UncommittedEvents.Count();
        Sut.ReadMention(10);
        Sut.UncommittedEvents.Count().ShouldBe(before);
        Sut.AntiSpamMentionConsumed.ShouldBeTrue();
    }

    [Fact]
    public void Read_all_consumes_active_admission_without_blocking()
    {
        Create();
        Apply(new AntiSpamMentionAdmittedEvent("op", 10, false));
        Sut.ReadUnreadMentions();
        Apply(Sut.UncommittedEvents.Last().AggregateEvent.ShouldBeOfType<UnreadMentionsReadEvent>());
        Sut.AntiSpamMentionConsumed.ShouldBeTrue();
    }

    [Fact]
    public void Successful_recount_clears_durable_retry_request_even_when_count_unchanged()
    {
        Create();
        Apply(new MentionRecountRequestedEvent());
        Sut.SyncUnreadMentionsCount(2, Sut.Version);
        Sut.UncommittedEvents.Last().AggregateEvent.ShouldBeOfType<MentionRecountCompletedEvent>();
    }

    [Fact]
    public void Snapshot_preserves_active_admission_and_retry_request()
    {
        var state = new DialogState();
        state.LoadSnapshot(new DialogSnapshot(111, 0, 0, 0, 0, Channel, false, false, 0,
            null, 2, null, antiSpamMentionOperation: "op", antiSpamMentionMessageId: 10,
            antiSpamMentionConsumed: true, mentionRecountRequired: true));
        state.AntiSpamMentionOperation.ShouldBe("op");
        state.AntiSpamMentionMessageId.ShouldBe(10);
        state.AntiSpamMentionConsumed.ShouldBeTrue();
        state.MentionRecountRequired.ShouldBeTrue();
    }

    [Fact]
    public async Task Closing_never_acquired_target_invalidates_delayed_admission()
    {
        Create();
        var captured = Sut.Version;
        Apply(new AntiSpamMentionAdmissionReleasedEvent("op"));
        await new TryAdmitAntiSpamMentionCommandHandler().ExecuteAsync(Sut,
            new TryAdmitAntiSpamMentionCommand(Sut.Id, "op", 10, captured, false), CancellationToken.None);
        Sut.AntiSpamMentionOperation.ShouldBeNull();
    }

    [Fact]
    public void Closing_other_operation_does_not_release_foreign_admission()
    {
        Create();
        Apply(new AntiSpamMentionAdmittedEvent("foreign", 11, false));
        Apply(new AntiSpamMentionAdmissionReleasedEvent("op"));
        Sut.AntiSpamMentionOperation.ShouldBe("foreign");
        Sut.AntiSpamMentionMessageId.ShouldBe(11);
    }

    [Fact]
    public async Task Closing_never_acquired_target_emits_durable_barrier()
    {
        Create();
        await new ReleaseAntiSpamMentionAdmissionCommandHandler().ExecuteAsync(Sut,
            new ReleaseAntiSpamMentionAdmissionCommand(Sut.Id, "op"), CancellationToken.None);
        Sut.UncommittedEvents.Last().AggregateEvent.ShouldBeOfType<AntiSpamMentionAdmissionReleasedEvent>()
            .OperationKey.ShouldBe("op");
    }

    private void Create()
    {
        Sut.CreateDialog(A<RequestInfo>(), 111, Channel, 0, 0);
        Apply(Sut.UncommittedEvents.Last().AggregateEvent.ShouldBeOfType<DialogCreatedEvent>());
        Apply(new MentionCreatedEvent(111, Channel, 10, 1));
        Apply(new MentionCreatedEvent(111, Channel, 11, 2));
    }

    private void Apply<T>(T e) where T : IAggregateEvent<DialogAggregate, DialogId>
        => Sut.ApplyEvents([ADomainEvent<DialogAggregate, DialogId, T>(e, Sut.Version + 1)]);
}
