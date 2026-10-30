using EventFlow.Commands;

namespace MyTelegram.Domain.Aggregates.Messaging;

public sealed class AntiSpamDeletionCancelledEvent(Guid requestId)
    : AggregateEvent<MessageAggregate, MessageId>
{
    public Guid RequestId { get; } = requestId;
}

/// <summary>Fence the original moderation request inside the same OCC stream as deletion.</summary>
public sealed class CancelAntiSpamDeletionCommand(MessageId aggregateId, Guid requestId)
    : Command<MessageAggregate, MessageId, IExecutionResult>(aggregateId)
{
    public Guid RequestId { get; } = requestId;
}

public sealed class CancelAntiSpamDeletionCommandHandler
    : CommandHandler<MessageAggregate, MessageId, CancelAntiSpamDeletionCommand>
{
    public override Task ExecuteAsync(MessageAggregate aggregate, CancelAntiSpamDeletionCommand command,
        CancellationToken cancellationToken)
    {
        aggregate.CancelAntiSpamDeletion(command.RequestId);
        return Task.CompletedTask;
    }
}
