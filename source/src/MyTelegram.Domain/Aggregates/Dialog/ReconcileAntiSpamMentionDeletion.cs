using EventFlow.Commands;

namespace MyTelegram.Domain.Aggregates.Dialog;

/// <summary>Installs an exact legacy recount while retaining the deletion's durable reservation.</summary>
public sealed class ReconcileAntiSpamMentionDeletionCommand(DialogId aggregateId, string operationKey,
    int messageId, int expectedVersion, int unreadCount)
    : Command<DialogAggregate, DialogId, IExecutionResult>(aggregateId)
{
    public string OperationKey { get; } = operationKey;
    public int MessageId { get; } = messageId;
    public int ExpectedVersion { get; } = expectedVersion;
    public int UnreadCount { get; } = unreadCount;
}

public sealed class ReconcileAntiSpamMentionDeletionCommandHandler(IAntiSpamEffectProofReader proofs)
    : CommandHandler<DialogAggregate, DialogId, ReconcileAntiSpamMentionDeletionCommand>
{
    public override async Task ExecuteAsync(DialogAggregate aggregate, ReconcileAntiSpamMentionDeletionCommand command,
        CancellationToken cancellationToken)
    {
        if (aggregate.MentionReadOperation != null ||
            aggregate.AntiSpamMentionOperation != command.OperationKey ||
            aggregate.AntiSpamMentionMessageId != command.MessageId) return;
        var receipt = await proofs.FindReceiptAsync(aggregate.Id.Value, command.OperationKey,
            AntiSpamEffectKind.MentionDeletion, aggregate.Version, cancellationToken);
        if (receipt != null)
        {
            aggregate.ApplyAntiSpamMentionDeletion(command.OperationKey, command.MessageId, true);
            return;
        }
        aggregate.ReconcileAntiSpamMentionDeletion(command.OperationKey, command.MessageId,
            command.ExpectedVersion, command.UnreadCount);
    }
}

public partial class DialogAggregate
{
    internal void ReconcileAntiSpamMentionDeletion(string operationKey, int messageId, int expectedVersion,
        int unreadCount)
    {
        if (IsNew || Version != expectedVersion || unreadCount < 0 || _state.MentionReadOperation != null ||
            _state.AntiSpamMentionOperation != operationKey || _state.AntiSpamMentionMessageId != messageId) return;
        // The supplied count already excludes this deletion. Do not decrement it a second time.
        Emit(new AntiSpamMentionDeletionAppliedEvent(operationKey, messageId, unreadCount));
    }
}
