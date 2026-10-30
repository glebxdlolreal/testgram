using EventFlow.Commands;

namespace MyTelegram.Domain.Aggregates.Messaging;

public sealed class AntiSpamReplyDeletionAppliedEvent(string operationKey, int replies, int pts,
    long? postChannelId = null, int? postMessageId = null, MessageReply? reply = null, string? expectedStaleSourceId = null)
    : AggregateEvent<MessageAggregate, MessageId>
{
    public string? ExpectedStaleSourceId { get; } = expectedStaleSourceId;
    public string OperationKey { get; } = operationKey;
    public MessageReply? Reply { get; } = reply;
    public int Replies { get; } = replies;
    public long? PostChannelId { get; } = postChannelId;
    public int? PostMessageId { get; } = postMessageId;
    public int Pts { get; } = pts;
}

public sealed class ApplyAntiSpamReplyDeletionCommand(MessageId aggregateId, string operationKey, int pts,
    long? postChannelId = null, int? postMessageId = null, string? expectedStaleSourceId = null)
    : Command<MessageAggregate, MessageId, IExecutionResult>(aggregateId)
{
    public long? PostChannelId { get; } = postChannelId;
    public int? PostMessageId { get; } = postMessageId;
    public string? ExpectedStaleSourceId { get; } = expectedStaleSourceId;
    public string OperationKey { get; } = operationKey;
    public int Pts { get; } = pts;
}

public sealed class ApplyAntiSpamReplyDeletionCommandHandler(IAntiSpamEffectProofReader proofs)
    : CommandHandler<MessageAggregate, MessageId, ApplyAntiSpamReplyDeletionCommand>
{
    public override async Task ExecuteAsync(MessageAggregate aggregate, ApplyAntiSpamReplyDeletionCommand command,
        CancellationToken cancellationToken)
    {
        var applied = await proofs.FindReceiptAsync(aggregate.Id.Value, command.OperationKey,
            AntiSpamEffectKind.ReplyDeletion, aggregate.Version, cancellationToken) != null;
        aggregate.ApplyAntiSpamReplyDeletion(command.OperationKey, command.Pts, applied,
            command.PostChannelId, command.PostMessageId, command.ExpectedStaleSourceId);
    }
}
