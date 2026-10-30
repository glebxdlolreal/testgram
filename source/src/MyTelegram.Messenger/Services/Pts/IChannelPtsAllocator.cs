namespace MyTelegram.Messenger.Services.Pts;

/// <summary>
/// One durable authority for channel Pts. Identity is (channelId, operationId); IDs must remain
/// stable across retries within that channel, and the count cannot change for an existing identity.
/// </summary>
public interface IChannelPtsAllocator
{
    Task<int> ReserveAsync(long channelId, string operationId, int count = 1,
        CancellationToken cancellationToken = default);

    /// <summary>Blocks later reservations until this operation's durable update publication is acknowledged.</summary>
    Task<int> ReserveForPublicationAsync(long channelId, string operationId, int count = 1,
        CancellationToken cancellationToken = default);

    /// <summary>Returns null when another publication owns the channel; never clears its barrier.</summary>
    Task<int?> TryReserveForPublicationAsync(long channelId, string operationId, int count = 1,
        CancellationToken cancellationToken = default);

    /// <summary>Call only after the update and all completion projections are durably observable. Idempotent.</summary>
    Task MarkPublishedAsync(long channelId, string operationId, int pts,
        CancellationToken cancellationToken = default);

    Task<int?> GetReadablePtsCeilingAsync(long channelId, CancellationToken cancellationToken = default);
}
