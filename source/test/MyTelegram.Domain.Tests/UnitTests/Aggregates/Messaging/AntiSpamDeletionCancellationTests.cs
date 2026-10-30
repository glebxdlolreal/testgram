using System.Reflection;

namespace MyTelegram.Domain.Tests.UnitTests.Aggregates.Messaging;

public class AntiSpamDeletionCancellationTests : TestsFor<MessageAggregate>
{
    public AntiSpamDeletionCancellationTests()
    {
        Fixture.Customize<MessageId>(c => c.FromFactory(() => MessageId.Create(100, 1)));
    }

    [Fact]
    public async Task Cancellation_command_fences_dispatched_deletion_even_with_current_revision()
    {
        CreateMessage();
        var request = ModerationRequest();
        var dispatchedVersion = Sut.Version;
        var command = new CancelAntiSpamDeletionCommand(Sut.Id, request.RequestId);
        await new CancelAntiSpamDeletionCommandHandler().ExecuteAsync(Sut, command, CancellationToken.None);

        Sut.DeleteChannelMessage(request, dispatchedVersion);
        Sut.DeleteChannelMessage(request, Sut.Version);

        Sut.AntiSpamDeletionCancelledRequestId.ShouldBe(request.RequestId);
        AssertNotDeleted();
    }

    [Fact]
    public void Repeated_and_different_cancellations_keep_first_token_without_more_events()
    {
        CreateMessage();
        var first = Guid.NewGuid();
        Sut.CancelAntiSpamDeletion(first);
        var count = Sut.UncommittedEvents.Count();
        Sut.CancelAntiSpamDeletion(first);
        Sut.CancelAntiSpamDeletion(Guid.NewGuid());

        Sut.AntiSpamDeletionCancelledRequestId.ShouldBe(first);
        Sut.UncommittedEvents.Count().ShouldBe(count);
        Sut.UncommittedEvents.Count(e => e.AggregateEvent is AntiSpamDeletionCancelledEvent).ShouldBe(1);
    }

    [Fact]
    public void Recreated_job_cannot_bypass_first_cancellation_with_another_request_id()
    {
        CreateMessage();
        Sut.CancelAntiSpamDeletion(Guid.NewGuid());
        Sut.DeleteChannelMessage(ModerationRequest(), Sut.Version);
        AssertNotDeleted();
    }

    [Fact]
    public void Ordinary_deletion_remains_allowed_after_cancellation()
    {
        CreateMessage();
        var cancelled = Guid.NewGuid();
        Sut.CancelAntiSpamDeletion(cancelled);
        var manual = A<RequestInfo>() with { UserId = 200, RequestId = cancelled };
        Sut.DeleteChannelMessage(manual);

        Sut.IsChannelDeleted.ShouldBeTrue();
        Sut.ChannelDeletionRequestId.ShouldBe(manual.RequestId);
        Sut.AntiSpamDeletionCancelledRequestId.ShouldBe(cancelled);
    }

    [Fact]
    public void Deletion_winning_serialization_cannot_be_relabelled_as_cancelled()
    {
        CreateMessage();
        var request = ModerationRequest();
        Sut.DeleteChannelMessage(request, Sut.Version);
        var count = Sut.UncommittedEvents.Count();
        Sut.CancelAntiSpamDeletion(request.RequestId);

        Sut.IsChannelDeleted.ShouldBeTrue();
        Sut.ChannelDeletionRequestId.ShouldBe(request.RequestId);
        Sut.AntiSpamDeletionCancelledRequestId.ShouldBeNull();
        Sut.UncommittedEvents.Count().ShouldBe(count);
    }

    [Fact]
    public void Other_deletion_paths_also_make_cancellation_a_noop()
    {
        CreateMessage();
        Sut.DeleteMessage(A<RequestInfo>());
        var count = Sut.UncommittedEvents.Count();
        Sut.CancelAntiSpamDeletion(Guid.NewGuid());
        Sut.AntiSpamDeletionCancelledRequestId.ShouldBeNull();
        Sut.UncommittedEvents.Count().ShouldBe(count);
    }

    [Fact]
    public void Event_replay_restores_fence_and_retains_first_token()
    {
        CreateMessage();
        var first = Guid.NewGuid();
        Sut.ApplyEvents([
            ADomainEvent<MessageAggregate, MessageId, AntiSpamDeletionCancelledEvent>(new(first), 2),
            ADomainEvent<MessageAggregate, MessageId, AntiSpamDeletionCancelledEvent>(new(Guid.NewGuid()), 3)
        ]);

        Sut.AntiSpamDeletionCancelledRequestId.ShouldBe(first);
        Sut.DeleteChannelMessage(ModerationRequest(), Sut.Version);
        AssertNotDeleted();
    }

    [Fact]
    public async Task Snapshot_roundtrip_preserves_cancellation_and_blocks_late_request()
    {
        CreateMessage();
        var request = ModerationRequest();
        Sut.CancelAntiSpamDeletion(request.RequestId);
        var create = typeof(MessageAggregate).GetMethod("CreateSnapshotAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var snapshot = await (Task<MessageSnapshot>)create.Invoke(Sut, [CancellationToken.None])!;
        snapshot.AntiSpamDeletionCancelledRequestId.ShouldBe(request.RequestId);

        var state = new MessageState();
        state.LoadSnapshot(snapshot);
        state.AntiSpamDeletionCancelledRequestId.ShouldBe(request.RequestId);
        var restored = new MessageAggregate(Sut.Id);
        restored.ApplyEvents([
            ADomainEvent<MessageAggregate, MessageId, OutboxMessageCreatedEvent>(
                new OutboxMessageCreatedEvent(A<RequestInfo>(), snapshot.MessageItem, null, null, true, 1, null, null), 1)
        ]);
        var load = typeof(MessageAggregate).GetMethod("LoadSnapshotAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)load.Invoke(restored, [snapshot, null, CancellationToken.None])!;
        restored.AntiSpamDeletionCancelledRequestId.ShouldBe(request.RequestId);
        restored.DeleteChannelMessage(request, restored.Version);
        restored.IsChannelDeleted.ShouldBeFalse();
        restored.UncommittedEvents.ShouldNotContain(e => e.AggregateEvent is ChannelMessageDeletedEvent);
    }

    [Fact]
    public void Legacy_snapshot_does_not_invent_cancellation()
    {
        var snapshot = new MessageSnapshot(null!, [], 0, false, 0, false, 0, false);
        var state = new MessageState();
        state.LoadSnapshot(snapshot);
        snapshot.AntiSpamDeletionCancelledRequestId.ShouldBeNull();
        state.AntiSpamDeletionCancelledRequestId.ShouldBeNull();
    }

    private RequestInfo ModerationRequest() => A<RequestInfo>() with { UserId = MyTelegramConsts.AntiSpamBotUserId };

    private void AssertNotDeleted()
    {
        Sut.IsChannelDeleted.ShouldBeFalse();
        Sut.ChannelDeletionRequestId.ShouldBeNull();
        Sut.UncommittedEvents.ShouldNotContain(e => e.AggregateEvent is ChannelMessageDeletedEvent);
        Sut.UncommittedEvents.Last().AggregateEvent.ShouldBeOfType<ChannelMessageDeletionAcknowledgedEvent>()
            .OriginalDeletionRequestId.ShouldBeNull();
    }

    private void CreateMessage()
    {
        var owner = 100L.ToChannelPeer();
        var sender = 200L.ToUserPeer();
        var item = new MessageItem(owner, owner, sender, sender.PeerId, 1, "test", DateTime.UtcNow.ToTimestamp(), 1, true);
        Sut.ApplyEvents([
            ADomainEvent<MessageAggregate, MessageId, OutboxMessageCreatedEvent>(
                new OutboxMessageCreatedEvent(A<RequestInfo>(), item, null, null, true, 1, null, null), 1)
        ]);
    }
}
