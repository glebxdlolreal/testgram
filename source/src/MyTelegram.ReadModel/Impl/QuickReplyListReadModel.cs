namespace MyTelegram.ReadModel.Impl;

public class QuickReplyListReadModel : IQuickReplyListReadModel,
    IAmReadModelFor<QuickReplyListAggregate, QuickReplyListId, QuickReplyShortcutCreatedEvent>,
    IAmReadModelFor<QuickReplyListAggregate, QuickReplyListId, QuickReplyShortcutRenamedEvent>,
    IAmReadModelFor<QuickReplyListAggregate, QuickReplyListId, QuickReplyShortcutDeletedEvent>,
    IAmReadModelFor<QuickReplyListAggregate, QuickReplyListId, QuickRepliesReorderedEvent>
{
    public long OwnerUserId { get; private set; }
    public IReadOnlyList<QuickReplyShortcut> Shortcuts { get; private set; } = [];
    public virtual string Id { get; private set; } = null!;
    public virtual long? Version { get; set; }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<QuickReplyListAggregate, QuickReplyListId, QuickReplyShortcutCreatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        return Set(domainEvent.AggregateIdentity, domainEvent.AggregateEvent.OwnerUserId,
            domainEvent.AggregateEvent.Shortcuts);
    }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<QuickReplyListAggregate, QuickReplyListId, QuickReplyShortcutRenamedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        return Set(domainEvent.AggregateIdentity, domainEvent.AggregateEvent.OwnerUserId,
            domainEvent.AggregateEvent.Shortcuts);
    }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<QuickReplyListAggregate, QuickReplyListId, QuickReplyShortcutDeletedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        return Set(domainEvent.AggregateIdentity, domainEvent.AggregateEvent.OwnerUserId,
            domainEvent.AggregateEvent.Shortcuts);
    }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<QuickReplyListAggregate, QuickReplyListId, QuickRepliesReorderedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        return Set(domainEvent.AggregateIdentity, domainEvent.AggregateEvent.OwnerUserId,
            domainEvent.AggregateEvent.Shortcuts);
    }

    private Task Set(QuickReplyListId id, long ownerUserId, IEnumerable<QuickReplyShortcut> shortcuts)
    {
        Id = id.Value;
        OwnerUserId = ownerUserId;
        Shortcuts = [.. shortcuts];
        return Task.CompletedTask;
    }
}
