namespace MyTelegram.Domain.Aggregates.Messaging;

public class QuickReplyListState : AggregateState<QuickReplyListAggregate, QuickReplyListId, QuickReplyListState>,
    IApply<QuickReplyShortcutCreatedEvent>,
    IApply<QuickReplyShortcutRenamedEvent>,
    IApply<QuickReplyShortcutDeletedEvent>,
    IApply<QuickRepliesReorderedEvent>
{
    public List<QuickReplyShortcut> Shortcuts { get; private set; } = [];

    public void Apply(QuickReplyShortcutCreatedEvent aggregateEvent)
    {
        Shortcuts = [.. aggregateEvent.Shortcuts];
    }

    public void Apply(QuickReplyShortcutRenamedEvent aggregateEvent)
    {
        Shortcuts = [.. aggregateEvent.Shortcuts];
    }

    public void Apply(QuickReplyShortcutDeletedEvent aggregateEvent)
    {
        Shortcuts = [.. aggregateEvent.Shortcuts];
    }

    public void Apply(QuickRepliesReorderedEvent aggregateEvent)
    {
        Shortcuts = [.. aggregateEvent.Shortcuts];
    }
}
