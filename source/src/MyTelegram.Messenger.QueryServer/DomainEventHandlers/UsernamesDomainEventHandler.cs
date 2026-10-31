using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;
using MyTelegram.Messenger.Services;

namespace MyTelegram.Messenger.QueryServer.DomainEventHandlers;

/// <summary>
/// Announces a changed <a href="https://corefork.telegram.org/api/fragment">usernames vector</a>.
/// The toggle and reorder methods answer with a plain <c>Bool</c>, and attaching a Fragment collectible
/// has no request behind it at all, so these updates are the only way the change reaches the owner's
/// other sessions, their contacts and the channel members.
/// </summary>
public class UsernamesDomainEventHandler(
    IUsernameUpdateNotifier usernameUpdateNotifier,
    IChannelUpdateNotifier channelUpdateNotifier,
    IMongoDatabase database,
    ILogger<UsernamesDomainEventHandler> logger)
    : ISubscribeSynchronousTo<UserAggregate, UserId, UserUsernamesChangedEvent>,
        ISubscribeSynchronousTo<ChannelAggregate, ChannelId, ChannelUsernamesChangedEvent>
{
    public Task HandleAsync(IDomainEvent<UserAggregate, UserId, UserUsernamesChangedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var e = domainEvent.AggregateEvent;

        // The calling session applied the change itself when it got the rpc result.
        return usernameUpdateNotifier.NotifyUserNameChangedAsync(e.UserId,
            e.RequestInfo.PermAuthKeyId == 0 ? null : e.RequestInfo.PermAuthKeyId,
            usernames: e.Usernames);
    }

    public async Task HandleAsync(IDomainEvent<ChannelAggregate, ChannelId, ChannelUsernamesChangedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var e = domainEvent.AggregateEvent;
        var previous = ActiveUsernames(e.OldUsernames);
        var current = ActiveUsernames(e.Usernames);

        if (e.RequestInfo.UserId != 0 && !previous.SequenceEqual(current))
        {
            try
            {
                // Keyed by the aggregate sequence number, so a redelivered event adds no second entry.
                await AdminLogHelper.LogChangeUsernames(database, e.ChannelId, e.RequestInfo.UserId, previous,
                    current, $"usernames-{domainEvent.AggregateSequenceNumber}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The change itself is committed; a missing log entry must not stop the updates.
                logger.LogError(ex, "Could not write the usernames admin log entry for {ChannelId}", e.ChannelId);
            }
        }

        await channelUpdateNotifier.NotifyChannelChangedAsync(e.RequestInfo, e.ChannelId);
    }

    private static List<string> ActiveUsernames(IEnumerable<UsernameItem>? usernames)
    {
        return usernames?.Where(p => p.Active).Select(p => p.Username).ToList() ?? [];
    }
}
