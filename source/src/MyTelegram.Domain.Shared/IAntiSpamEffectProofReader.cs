namespace MyTelegram;

/// <summary>Committed event evidence through the caller's loaded aggregate version.</summary>
public interface IAntiSpamEffectProofReader
{
    Task<MentionConsumptionProof> GetMentionConsumptionAsync(string aggregateId, int messageId,
        int throughVersion, CancellationToken cancellationToken);
    Task<AntiSpamEffectReceipt?> FindReceiptAsync(string aggregateId, string operationKey,
        AntiSpamEffectKind kind, int throughVersion, CancellationToken cancellationToken);
}

public enum AntiSpamEffectKind { MentionDeletion, ReplyDeletion, MentionReadIntent }
public sealed record MentionConsumptionProof(int ThroughVersion, int? CreatedAtVersion, int? ConsumedAtVersion);
public sealed record AntiSpamEffectReceipt(int AggregateVersion, string OperationKey, AntiSpamEffectKind Kind);
