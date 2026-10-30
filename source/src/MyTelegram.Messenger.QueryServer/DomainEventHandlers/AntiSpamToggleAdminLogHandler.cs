using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;

namespace MyTelegram.Messenger.QueryServer.DomainEventHandlers;

/// <summary>
/// Records <c>channelAdminLogEventActionToggleAntiSpam</c> in Recent Actions, as Telegram does for
/// <a href="https://corefork.telegram.org/api/antispam">native antispam</a>. It runs on the persisted
/// event, so a refused toggle is never logged, and keys the entry by the aggregate sequence number so a
/// redelivered event cannot add a second one.
/// </summary>
public class AntiSpamToggleAdminLogHandler(IMongoDatabase database, ILogger<AntiSpamToggleAdminLogHandler> logger)
    : ISubscribeSynchronousTo<ChannelAggregate, ChannelId, ChannelAntiSpamChangedEvent>
{
    public async Task HandleAsync(IDomainEvent<ChannelAggregate, ChannelId, ChannelAntiSpamChangedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var e = domainEvent.AggregateEvent;
        try
        {
            await AdminLogHelper.LogToggleAntiSpam(database, e.ChannelId, e.RequestInfo.UserId, e.Enabled,
                $"antispam-toggle-{domainEvent.AggregateSequenceNumber}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The toggle itself is committed; a missing log entry must not fail the caller's RPC.
            logger.LogError(ex, "Could not write the antispam toggle admin log entry for {ChannelId}", e.ChannelId);
        }
    }
}
