using EventFlow.Commands;

namespace MyTelegram.Domain.Aggregates.Dialog;

/// <summary>One durable deletion reservation per dialog; it has no lease or automatic expiry.</summary>
public sealed class AntiSpamMentionAdmittedEvent(string operationKey, int messageId, bool consumed)
    : AggregateEvent<DialogAggregate, DialogId>
{
    public string OperationKey { get; } = operationKey;
    public int MessageId { get; } = messageId;
    public bool Consumed { get; } = consumed;
}

public sealed class AntiSpamMentionAdmissionReleasedEvent(string operationKey)
    : AggregateEvent<DialogAggregate, DialogId>
{
    public string OperationKey { get; } = operationKey;
}

public sealed class MentionRecountRequestedEvent : AggregateEvent<DialogAggregate, DialogId>;
public sealed class MentionRecountCompletedEvent : AggregateEvent<DialogAggregate, DialogId>;

/// <summary>The supplied consumption proof must describe exactly ExpectedVersion.</summary>
public sealed class TryAdmitAntiSpamMentionCommand(DialogId aggregateId, string operationKey, int messageId,
    int expectedVersion, bool consumed) : Command<DialogAggregate, DialogId, IExecutionResult>(aggregateId)
{
    public string OperationKey { get; } = operationKey;
    public int MessageId { get; } = messageId;
    public int ExpectedVersion { get; } = expectedVersion;
    public bool Consumed { get; } = consumed;
}

public sealed class TryAdmitAntiSpamMentionCommandHandler
    : CommandHandler<DialogAggregate, DialogId, TryAdmitAntiSpamMentionCommand>
{
    public override Task ExecuteAsync(DialogAggregate aggregate, TryAdmitAntiSpamMentionCommand command,
        CancellationToken cancellationToken)
    {
        aggregate.TryAdmitAntiSpamMention(command.OperationKey, command.MessageId,
            command.ExpectedVersion, command.Consumed);
        return Task.CompletedTask;
    }
}

/// <summary>Only the durable deletion job may release after proven abort or projected completion.</summary>
public sealed class ReleaseAntiSpamMentionAdmissionCommand(DialogId aggregateId, string operationKey)
    : Command<DialogAggregate, DialogId, IExecutionResult>(aggregateId)
{
    public string OperationKey { get; } = operationKey;
}

public sealed class ReleaseAntiSpamMentionAdmissionCommandHandler
    : CommandHandler<DialogAggregate, DialogId, ReleaseAntiSpamMentionAdmissionCommand>
{
    public override Task ExecuteAsync(DialogAggregate aggregate, ReleaseAntiSpamMentionAdmissionCommand command,
        CancellationToken cancellationToken)
    {
        aggregate.ReleaseAntiSpamMentionAdmission(command.OperationKey);
        return Task.CompletedTask;
    }
}

public partial class DialogAggregate
{
    public string? AntiSpamMentionOperation => _state.AntiSpamMentionOperation;
    public int AntiSpamMentionMessageId => _state.AntiSpamMentionMessageId;
    public bool AntiSpamMentionConsumed => _state.AntiSpamMentionConsumed;
    public bool MentionRecountRequired => _state.MentionRecountRequired;

    internal void TryAdmitAntiSpamMention(string operationKey, int messageId, int expectedVersion, bool consumed)
    {
        // Caller reloads after publishing to distinguish busy/stale from successful admission.
        // No wait is performed inside the aggregate update boundary.
        if (IsNew || Version != expectedVersion || _state.AntiSpamMentionOperation != null) return;
        Emit(new AntiSpamMentionAdmittedEvent(operationKey, messageId, consumed));
    }

    internal void ReleaseAntiSpamMentionAdmission(string operationKey)
    {
        // Also close a never-acquired target: this committed version barrier rejects a delayed
        // TryAdmit whose expected version was captured before the job entered aborting.
        Emit(new AntiSpamMentionAdmissionReleasedEvent(operationKey));
    }
}
