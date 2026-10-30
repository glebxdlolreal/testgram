using EventFlow.Commands;

namespace MyTelegram.Domain.Aggregates.Dialog;

public partial class UnreadMentionsCountSyncedEvent(long ownerUserId, Peer toPeer, int unreadMentionsCount)
    : AggregateEvent<DialogAggregate, DialogId>
{
    public long OwnerUserId { get; } = ownerUserId;
    public Peer ToPeer { get; } = toPeer;
    public int UnreadMentionsCount { get; } = unreadMentionsCount;
}

/// <summary>ExpectedVersion is captured before reading either mention read state or messages.</summary>
public sealed class SyncUnreadMentionsCountCommand(DialogId aggregateId, int unreadMentionsCount, int expectedVersion)
    : Command<DialogAggregate, DialogId, IExecutionResult>(aggregateId)
{
    public int UnreadMentionsCount { get; } = unreadMentionsCount;
    public int ExpectedVersion { get; } = expectedVersion;
}

public sealed class SyncUnreadMentionsCountCommandHandler
    : CommandHandler<DialogAggregate, DialogId, SyncUnreadMentionsCountCommand>
{
    public override Task ExecuteAsync(DialogAggregate aggregate, SyncUnreadMentionsCountCommand command,
        CancellationToken cancellationToken)
    {
        aggregate.SyncUnreadMentionsCount(command.UnreadMentionsCount, command.ExpectedVersion);
        return Task.CompletedTask;
    }
}
