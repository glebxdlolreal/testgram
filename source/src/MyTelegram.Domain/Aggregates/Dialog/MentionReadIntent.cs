using EventFlow.Commands;

namespace MyTelegram.Domain.Aggregates.Dialog;

public sealed class MentionReadIntentAdmittedEvent(string operationKey) : AggregateEvent<DialogAggregate, DialogId>
{
    public string OperationKey { get; } = operationKey;
}

/// <summary>Committed in the same append as every counter effect of this read intent.</summary>
public sealed class MentionReadIntentAppliedEvent(string operationKey) : AggregateEvent<DialogAggregate, DialogId>
{
    public string OperationKey { get; } = operationKey;
}

public sealed class MentionReadIntentReleasedEvent(string operationKey) : AggregateEvent<DialogAggregate, DialogId>
{
    public string OperationKey { get; } = operationKey;
}

public sealed class TryAdmitMentionReadIntentCommand(DialogId aggregateId, string operationKey, int expectedVersion)
    : Command<DialogAggregate, DialogId, IExecutionResult>(aggregateId)
{
    public string OperationKey { get; } = operationKey;
    public int ExpectedVersion { get; } = expectedVersion;
}

public sealed class TryAdmitMentionReadIntentCommandHandler
    : CommandHandler<DialogAggregate, DialogId, TryAdmitMentionReadIntentCommand>
{
    public override Task ExecuteAsync(DialogAggregate aggregate, TryAdmitMentionReadIntentCommand command,
        CancellationToken cancellationToken)
    {
        aggregate.TryAdmitMentionReadIntent(command.OperationKey, command.ExpectedVersion);
        return Task.CompletedTask;
    }
}

public sealed class ApplyMentionReadIntentCommand(DialogId aggregateId, string operationKey,
    List<int> messageIds, bool readAll) : Command<DialogAggregate, DialogId, IExecutionResult>(aggregateId)
{
    public string OperationKey { get; } = operationKey;
    public List<int> MessageIds { get; } = messageIds;
    public bool ReadAll { get; } = readAll;
}

public sealed class ApplyMentionReadIntentCommandHandler(IAntiSpamEffectProofReader proofs)
    : CommandHandler<DialogAggregate, DialogId, ApplyMentionReadIntentCommand>
{
    public override async Task ExecuteAsync(DialogAggregate aggregate, ApplyMentionReadIntentCommand command,
        CancellationToken cancellationToken)
    {
        if (aggregate.MentionReadOperation != command.OperationKey) return;
        var receipt = await proofs.FindReceiptAsync(aggregate.Id.Value, command.OperationKey,
            AntiSpamEffectKind.MentionReadIntent, aggregate.Version, cancellationToken);
        // In particular, never clear mentions that arrived after an earlier read-all append.
        if (receipt != null) return;
        if (command.ReadAll)
        {
            aggregate.ReadUnreadMentions();
        }
        else
        {
            var unreadMessageIds = new List<int>();
            // Resolve all proofs against the committed prefix before emitting anything: the
            // journal reader cannot see this command's pending events until the atomic append.
            foreach (var messageId in command.MessageIds.Distinct())
            {
                var proof = await proofs.GetMentionConsumptionAsync(aggregate.Id.Value, messageId,
                    aggregate.Version, cancellationToken);
                if (proof.ThroughVersion != aggregate.Version)
                    throw new InvalidOperationException("Mention proof version mismatch");
                if (proof.CreatedAtVersion.HasValue && !proof.ConsumedAtVersion.HasValue)
                    unreadMessageIds.Add(messageId);
            }
            foreach (var messageId in unreadMessageIds) aggregate.ReadMention(messageId);
        }
        aggregate.CompleteMentionReadIntent(command.OperationKey);
    }
}

public sealed class ReleaseMentionReadIntentCommand(DialogId aggregateId, string operationKey)
    : Command<DialogAggregate, DialogId, IExecutionResult>(aggregateId)
{
    public string OperationKey { get; } = operationKey;
}

public sealed class ReleaseMentionReadIntentCommandHandler
    : CommandHandler<DialogAggregate, DialogId, ReleaseMentionReadIntentCommand>
{
    public override Task ExecuteAsync(DialogAggregate aggregate, ReleaseMentionReadIntentCommand command,
        CancellationToken cancellationToken)
    {
        aggregate.ReleaseMentionReadIntent(command.OperationKey);
        return Task.CompletedTask;
    }
}

public partial class DialogAggregate
{
    public string? MentionReadOperation => _state.MentionReadOperation;

    internal void TryAdmitMentionReadIntent(string operationKey, int expectedVersion)
    {
        if (IsNew || Version != expectedVersion || _state.MentionReadOperation != null) return;
        Emit(new MentionReadIntentAdmittedEvent(operationKey));
    }

    internal void CompleteMentionReadIntent(string operationKey)
    {
        if (_state.MentionReadOperation == operationKey)
            Emit(new MentionReadIntentAppliedEvent(operationKey));
    }

    internal void ReleaseMentionReadIntent(string operationKey)
    {
        if (_state.MentionReadOperation == operationKey)
            Emit(new MentionReadIntentReleasedEvent(operationKey));
    }
}
