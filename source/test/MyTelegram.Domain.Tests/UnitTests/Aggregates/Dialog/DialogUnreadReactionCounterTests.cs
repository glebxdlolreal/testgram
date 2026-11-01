using MyTelegram.Domain.Aggregates.Dialog;

namespace MyTelegram.Domain.Tests.UnitTests.Aggregates.Dialog;

/// <summary>
/// Feature: the unread reaction badge of a dialog, see https://corefork.telegram.org/api/reactions.
/// It counts messages that carry someone else's reaction, not reactions: Android walks it with
/// messages.getUnreadReactions(add_offset=count-1), so a counter above the number of such messages
/// never clears by scrolling.
/// </summary>
public class DialogUnreadReactionCounterTests : TestsFor<DialogAggregate>
{
    private const long OwnerUserId = 111;
    private static readonly Peer Group = new(PeerType.Channel, 1001);

    public DialogUnreadReactionCounterTests()
    {
        Fixture.Customize<DialogId>(x => x.FromFactory(() => DialogId.Create(OwnerUserId, Group)));
    }

    [Fact]
    public void A_reaction_in_a_dialog_that_does_not_exist_yet_throws()
    {
        // The caller treats the badge as best-effort and swallows this; emitting would store a
        // dialog with no owner and no peer.
        Should.Throw<Exception>(() => Sut.CreateUnreadReaction(10));
    }

    [Fact]
    public void Each_message_counts_once()
    {
        CreateDialog();

        UnreadReaction(10);
        UnreadReaction(11);

        LastEvent<UnreadReactionCreatedEvent>().UnreadReactionsCount.ShouldBe(2);
    }

    [Fact]
    public void A_second_reaction_to_an_unread_message_changes_nothing()
    {
        CreateDialog();
        UnreadReaction(10);
        var eventCount = Sut.UncommittedEvents.Count();

        Sut.CreateUnreadReaction(10);

        Sut.UncommittedEvents.Count().ShouldBe(eventCount);
    }

    [Fact]
    public void Taking_back_the_last_reaction_lowers_the_counter()
    {
        CreateDialog();
        UnreadReaction(10);
        UnreadReaction(11);

        Sut.RemoveUnreadReaction(10);

        var @event = LastEvent<UnreadReactionRemovedEvent>();
        @event.MessageId.ShouldBe(10);
        @event.UnreadReactionsCount.ShouldBe(1);
    }

    [Fact]
    public void Removing_a_message_that_is_not_counted_emits_nothing()
    {
        CreateDialog();
        UnreadReaction(10);
        var eventCount = Sut.UncommittedEvents.Count();

        Sut.RemoveUnreadReaction(11);

        Sut.UncommittedEvents.Count().ShouldBe(eventCount);
    }

    [Fact]
    public void A_message_counts_again_after_the_badge_was_read()
    {
        CreateDialog();
        UnreadReaction(10);
        Sut.ReadUnreadReactions();
        ApplyEvent((UnreadReactionsReadEvent)Sut.UncommittedEvents.Last().AggregateEvent);

        Sut.CreateUnreadReaction(10);

        LastEvent<UnreadReactionCreatedEvent>().UnreadReactionsCount.ShouldBe(1);
    }

    [Fact]
    public void The_snapshot_keeps_the_counted_messages()
    {
        var state = new DialogState();
        state.LoadSnapshot(new DialogSnapshot(OwnerUserId, 0, 0, 0, 0, Group, false, false, 0,
            null, 0, null, unreadReactionsCount: 2, unreadReactionMessageIds: [10, 11]));

        state.UnreadReactionsCount.ShouldBe(2);
        state.UnreadReactionMessageIds.ShouldBe([10, 11], ignoreOrder: true);
    }

    [Fact]
    public void A_snapshot_from_before_the_tracking_loads_with_no_messages()
    {
        var state = new DialogState();
        state.LoadSnapshot(new DialogSnapshot(OwnerUserId, 0, 0, 0, 0, Group, false, false, 0,
            null, 0, null, unreadReactionsCount: 3));

        state.UnreadReactionsCount.ShouldBe(3);
        state.UnreadReactionMessageIds.ShouldBeEmpty();
    }

    private void CreateDialog()
    {
        Sut.CreateDialog(A<RequestInfo>(), OwnerUserId, Group, 0, 0);
        ApplyEvent((DialogCreatedEvent)Sut.UncommittedEvents.Last().AggregateEvent);
    }

    private void UnreadReaction(int messageId)
    {
        Sut.CreateUnreadReaction(messageId);
        ApplyEvent((UnreadReactionCreatedEvent)Sut.UncommittedEvents.Last().AggregateEvent);
    }

    private void ApplyEvent<TEvent>(TEvent aggregateEvent)
        where TEvent : IAggregateEvent<DialogAggregate, DialogId>
    {
        Sut.ApplyEvents([ADomainEvent<DialogAggregate, DialogId, TEvent>(aggregateEvent, Sut.Version + 1)]);
    }

    private T LastEvent<T>() where T : class
    {
        return Sut.UncommittedEvents.Last().AggregateEvent.ShouldBeOfType<T>();
    }
}
