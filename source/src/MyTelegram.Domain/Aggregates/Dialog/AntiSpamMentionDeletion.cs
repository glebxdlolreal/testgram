using EventFlow.Commands;

namespace MyTelegram.Domain.Aggregates.Dialog;

public sealed class AntiSpamMentionDeletionAppliedEvent(string operationKey, int messageId, int unreadMentionsCount)
    : AggregateEvent<DialogAggregate, DialogId>
{
    public string OperationKey { get; } = operationKey;
    public int MessageId { get; } = messageId;
    public int UnreadMentionsCount { get; } = unreadMentionsCount;
}

public sealed class ApplyAntiSpamMentionDeletionCommand(DialogId aggregateId, string operationKey, int messageId,
    int capturedVersion, bool alreadyRead) : Command<DialogAggregate, DialogId, IExecutionResult>(aggregateId)
{
    public string OperationKey { get; } = operationKey;
    public int MessageId { get; } = messageId;
    public int CapturedVersion { get; } = capturedVersion;
    public bool AlreadyRead { get; } = alreadyRead;
}

public sealed class ApplyAntiSpamMentionDeletionCommandHandler(IAntiSpamEffectProofReader proofs)
    : CommandHandler<DialogAggregate, DialogId, ApplyAntiSpamMentionDeletionCommand>
{
    public override async Task ExecuteAsync(DialogAggregate aggregate, ApplyAntiSpamMentionDeletionCommand command,
        CancellationToken cancellationToken)
    {
        var receipt = await proofs.FindReceiptAsync(aggregate.Id.Value, command.OperationKey,
            AntiSpamEffectKind.MentionDeletion, aggregate.Version, cancellationToken);
        if (receipt != null)
        {
            // Re-publish the CURRENT scalar for a missing projection, never replay the old
            // receipt's scalar or decrement twice. Completion keeps the earliest receipt threshold.
            aggregate.ApplyAntiSpamMentionDeletion(command.OperationKey, command.MessageId, true);
            return;
        }
        var proof = await proofs.GetMentionConsumptionAsync(aggregate.Id.Value, command.MessageId,
            aggregate.Version, cancellationToken);
        if (proof.ThroughVersion != aggregate.Version) throw new InvalidOperationException("Mention proof version mismatch");
        var consumed = !proof.CreatedAtVersion.HasValue || proof.ConsumedAtVersion.HasValue;
        aggregate.ApplyAntiSpamMentionDeletion(command.OperationKey, command.MessageId, consumed);
    }
}

// Explicit handler prevents ordinary readMessageContents from decrementing a mention again in the
// crash window between committing moderation's dialog event and marking the external read state.
public partial class MentionReadEvent(long ownerUserId, Peer toPeer, int messageId, int unreadMentionsCount)
    : AggregateEvent<DialogAggregate, DialogId>
{
    public long OwnerUserId { get; } = ownerUserId;
    public Peer ToPeer { get; } = toPeer;
    public int MessageId { get; } = messageId;
    public int UnreadMentionsCount { get; } = unreadMentionsCount;
}

public partial class ReadMentionCommand(DialogId aggregateId, int messageId)
    : Command<DialogAggregate, DialogId, IExecutionResult>(aggregateId)
{
    public int MessageId { get; } = messageId;
}

public sealed class ReadMentionCommandHandler(IAntiSpamEffectProofReader proofs)
    : CommandHandler<DialogAggregate, DialogId, ReadMentionCommand>
{
    public override async Task ExecuteAsync(DialogAggregate aggregate, ReadMentionCommand command,
        CancellationToken cancellationToken)
    {
        if (!aggregate.HasAntiSpamMentionEffects)
        {
            aggregate.ReadMention(command.MessageId);
            return;
        }
        var proof = await proofs.GetMentionConsumptionAsync(aggregate.Id.Value, command.MessageId,
            aggregate.Version, cancellationToken);
        if (proof.ThroughVersion != aggregate.Version) throw new InvalidOperationException("Mention proof version mismatch");
        if (!proof.CreatedAtVersion.HasValue || proof.ConsumedAtVersion.HasValue) return;
        aggregate.ReadMention(command.MessageId);
    }
}
