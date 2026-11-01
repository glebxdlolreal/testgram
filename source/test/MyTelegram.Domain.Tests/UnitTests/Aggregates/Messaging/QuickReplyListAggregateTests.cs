namespace MyTelegram.Domain.Tests.UnitTests.Aggregates.Messaging;

/// <summary>
/// Feature: <a href="https://corefork.telegram.org/api/business#quick-reply-shortcuts">quick reply shortcuts</a>
/// are per user, unique by name, and every event carries the whole resulting list.
/// </summary>
public sealed class QuickReplyListAggregateTests
{
    private static readonly RequestInfo Request = new("c", 1, 1, 1, 1, 1, 1, Guid.NewGuid(), 0, 0);

    private static QuickReplyListAggregate NewAggregate() => new(QuickReplyListId.Create(1));

    [Fact]
    public void Creating_an_existing_name_again_is_a_no_op()
    {
        var sut = NewAggregate();
        sut.CreateShortcut(1, 1, "hello");
        sut.CreateShortcut(1, 2, "HELLO");

        sut.UncommittedEvents.Count().ShouldBe(1);
        sut.Shortcuts.ShouldBe([new QuickReplyShortcut(1, "hello")]);
    }

    [Fact]
    public void Rename_to_a_name_taken_by_another_shortcut_is_refused()
    {
        var sut = NewAggregate();
        sut.CreateShortcut(1, 1, "a");
        sut.CreateShortcut(1, 2, "b");

        Should.Throw<Exception>(() => sut.RenameShortcut(Request, 1, 2, "a"));
        Should.Throw<Exception>(() => sut.RenameShortcut(Request, 1, 9, "c"));
    }

    [Fact]
    public void Reorder_puts_the_listed_shortcuts_first_and_keeps_the_rest_after_them()
    {
        var sut = NewAggregate();
        sut.CreateShortcut(1, 1, "a");
        sut.CreateShortcut(1, 2, "b");
        sut.CreateShortcut(1, 3, "c");

        sut.ReorderShortcuts(Request, 1, [3]);

        sut.Shortcuts.Select(p => p.ShortcutId).ShouldBe([3, 1, 2]);
        sut.UncommittedEvents.Last().AggregateEvent.ShouldBeOfType<QuickRepliesReorderedEvent>().Changed
            .ShouldBeTrue();
    }

    [Fact]
    public void Delete_removes_only_that_shortcut()
    {
        var sut = NewAggregate();
        sut.CreateShortcut(1, 1, "a");
        sut.CreateShortcut(1, 2, "b");

        sut.DeleteShortcut(Request, 1, 1, [10, 11]);

        sut.Shortcuts.ShouldBe([new QuickReplyShortcut(2, "b")]);
        sut.UncommittedEvents.Last().AggregateEvent.ShouldBeOfType<QuickReplyShortcutDeletedEvent>().MessageIds
            .ShouldBe([10, 11]);
    }
}

/// <summary>
/// EventFlow refuses to emit an event the aggregate state has no <c>Apply</c> for; every business profile
/// event must therefore be applicable even though the state keeps nothing from it.
/// </summary>
public sealed class UserBusinessProfileEventsTests
{
    [Fact]
    public void Every_business_profile_event_has_an_apply_on_user_state()
    {
        var applied = typeof(UserState).GetInterfaces()
            .Where(p => p.IsGenericType && p.GetGenericTypeDefinition() == typeof(IApply<>))
            .Select(p => p.GetGenericArguments()[0])
            .ToList();

        applied.ShouldContain(typeof(BusinessWorkHoursUpdatedEvent));
        applied.ShouldContain(typeof(BusinessLocationUpdatedEvent));
        applied.ShouldContain(typeof(BusinessGreetingMessageUpdatedEvent));
        applied.ShouldContain(typeof(BusinessAwayMessageUpdatedEvent));
        applied.ShouldContain(typeof(BusinessIntroUpdatedEvent));
    }
}
