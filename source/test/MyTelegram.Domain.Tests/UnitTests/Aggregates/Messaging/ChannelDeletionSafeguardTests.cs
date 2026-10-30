using System.Reflection;

namespace MyTelegram.Domain.Tests.UnitTests.Aggregates.Messaging;

public class ChannelDeletionSafeguardTests : TestsFor<MessageAggregate>
{
    public ChannelDeletionSafeguardTests()
    {
        Fixture.Customize<MessageId>(c => c.FromFactory(() => MessageId.Create(100, 1)));
    }

    [Fact]
    public void Retry_acknowledges_without_another_deletion_and_preserves_first_request()
    {
        CreateMessage();
        var first = A<RequestInfo>();
        var retry = A<RequestInfo>();
        Sut.DeleteChannelMessage(first);
        Sut.DeleteChannelMessage(retry);
        Sut.DeleteChannelMessage(first);

        Sut.IsChannelDeleted.ShouldBeTrue();
        Sut.ChannelDeletionRequestId.ShouldBe(first.RequestId);
        Sut.UncommittedEvents.Count(e => e.AggregateEvent is ChannelMessageDeletedEvent).ShouldBe(1);
        var acknowledgements = Sut.UncommittedEvents.Select(e => e.AggregateEvent)
            .OfType<ChannelMessageDeletionAcknowledgedEvent>().ToList();
        acknowledgements.Count.ShouldBe(2);
        acknowledgements[0].RequestInfo.ShouldBe(retry);
        acknowledgements.ShouldAllBe(e => e.OriginalDeletionRequestId == first.RequestId);
    }

    [Fact]
    public void Replaying_legacy_duplicate_events_preserves_first_deleting_identity()
    {
        CreateMessage();
        var first = A<RequestInfo>();
        Sut.ApplyEvents([
            ADomainEvent<MessageAggregate, MessageId, ChannelMessageDeletedEvent>(Deletion(first), 2),
            ADomainEvent<MessageAggregate, MessageId, ChannelMessageDeletedEvent>(Deletion(A<RequestInfo>()), 3)
        ]);
        Sut.ChannelDeletionRequestId.ShouldBe(first.RequestId);
        Sut.DeleteChannelMessage(A<RequestInfo>());
        Sut.UncommittedEvents.Last().AggregateEvent.ShouldBeOfType<ChannelMessageDeletionAcknowledgedEvent>();
    }

    [Fact]
    public async Task Snapshot_preserves_deletion_identity_and_retry_is_acknowledged()
    {
        CreateMessage();
        var first = A<RequestInfo>();
        Sut.DeleteChannelMessage(first);
        var create = typeof(MessageAggregate).GetMethod("CreateSnapshotAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var snapshot = await (Task<MessageSnapshot>)create.Invoke(Sut, [CancellationToken.None])!;
        snapshot.IsChannelDeleted.ShouldBeTrue();
        snapshot.ChannelDeletionRequestId.ShouldBe(first.RequestId);

        var state = new MessageState();
        state.LoadSnapshot(snapshot);
        state.IsChannelDeleted.ShouldBeTrue();
        state.ChannelDeletionRequestId.ShouldBe(first.RequestId);
        var load = typeof(MessageAggregate).GetMethod("LoadSnapshotAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)load.Invoke(Sut, [snapshot, null, CancellationToken.None])!;
        Sut.DeleteChannelMessage(A<RequestInfo>());
        Sut.UncommittedEvents.Last().AggregateEvent.ShouldBeOfType<ChannelMessageDeletionAcknowledgedEvent>();
    }

    [Fact]
    public void Old_snapshot_defaults_do_not_invent_a_deletion_identity()
    {
        var snapshot = new MessageSnapshot(null!, [], 0, false, 0, false, 0, false);
        var state = new MessageState();
        state.LoadSnapshot(snapshot);
        state.IsChannelDeleted.ShouldBeFalse();
        state.ChannelDeletionRequestId.ShouldBeNull();
    }

    [Fact]
    public void Moderation_deletes_only_the_exact_observed_aggregate_revision()
    {
        CreateMessage();
        var request = A<RequestInfo>() with { UserId = MyTelegramConsts.AntiSpamBotUserId };
        Sut.DeleteChannelMessage(request, Sut.Version);
        Sut.IsChannelDeleted.ShouldBeTrue();
        Sut.ChannelDeletionRequestId.ShouldBe(request.RequestId);
    }

    [Fact]
    public void Moderation_edit_race_acknowledges_without_deleting_or_claiming_ownership()
    {
        CreateMessage();
        var observedVersion = Sut.Version;
        Sut.EditOutboxMessage(A<RequestInfo>(), 1, "changed since classification", DateTime.UtcNow.ToTimestamp(), null, null, null, false, null);
        Sut.DeleteChannelMessage(A<RequestInfo>() with { UserId = MyTelegramConsts.AntiSpamBotUserId }, observedVersion);
        Sut.IsChannelDeleted.ShouldBeFalse();
        Sut.ChannelDeletionRequestId.ShouldBeNull();
        var ack = Sut.UncommittedEvents.Last().AggregateEvent.ShouldBeOfType<ChannelMessageDeletionAcknowledgedEvent>();
        ack.OriginalDeletionRequestId.ShouldBeNull();
        Sut.UncommittedEvents.ShouldNotContain(e => e.AggregateEvent is ChannelMessageDeletedEvent);
    }

    [Fact]
    public void Moderation_requires_a_revision_but_manual_deletion_does_not()
    {
        CreateMessage();
        Sut.DeleteChannelMessage(A<RequestInfo>() with { UserId = MyTelegramConsts.AntiSpamBotUserId });
        Sut.IsChannelDeleted.ShouldBeFalse();
        Sut.DeleteChannelMessage(A<RequestInfo>() with { UserId = 200 }, -1);
        Sut.IsChannelDeleted.ShouldBeTrue();
    }

    private static ChannelMessageDeletedEvent Deletion(RequestInfo request) =>
        new(request, 100, 1, false, null, null, null);

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
