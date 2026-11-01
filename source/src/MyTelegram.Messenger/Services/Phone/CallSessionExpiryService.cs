using MyTelegram.Messenger.Services.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using MyTelegram.Schema;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Services.Phone;

/// <summary>
/// Terminates 1:1 call sessions that no client ever discarded.
/// </summary>
/// <remarks>
/// Clients run their own timers off the <c>call_receive_timeout_ms</c> / <c>call_ring_timeout_ms</c> /
/// <c>call_connect_timeout_ms</c> values published in <c>help.getConfig</c> and normally send
/// <c>phone.discardCall</c> themselves. When a client dies, loses connectivity, or is force-stopped, the
/// session would otherwise stay live forever and permanently mark both participants as busy - nothing
/// else in the system ever touches <c>call_sessions</c>.
/// <para>
/// Deadlines carry a grace period so the server never beats the client's own timer to the punch, and each
/// transition is claimed with a compare-and-set on <c>State</c> so a real <c>phone.discardCall</c> racing
/// the sweeper cannot produce a duplicate <c>phoneCallDiscarded</c>.
/// </para>
/// </remarks>
public sealed class CallSessionExpiryService(
    IMongoDatabase mongoDatabase,
    IObjectMessageSender objectMessageSender,
    IMessageAppService messageAppService,
    IOptionsMonitor<MyTelegramMessengerServerOptions> options,
    ILogger<CallSessionExpiryService> logger)
{
    private readonly IMongoCollection<CallSessionDocument> _callCollection =
        mongoDatabase.GetCollection<CallSessionDocument>("call_sessions");

    /// <summary>Expires every session past its deadline. Returns how many were discarded.</summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken = default)
    {
        var config = options.CurrentValue.Calls ?? new CallsConfig();
        var grace = Math.Max(0, config.ExpiryGraceSeconds);
        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var candidates = await _callCollection
            .Find(BuildCandidateFilter(config, grace, now))
            .Limit(Math.Max(1, config.ExpiryBatchSize))
            .ToListAsync(cancellationToken);

        var expired = 0;
        foreach (var session in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var deadline = DeadlineSeconds(session.State, config, grace);
            if (deadline == null || session.StateSince > now - deadline.Value)
            {
                // Only a loose pre-filter matched (it keys off Date, which is <= StateSince); this
                // session has not actually reached its deadline yet.
                continue;
            }

            if (await ExpireAsync(session, now, cancellationToken))
            {
                expired++;
            }
        }

        return expired;
    }

    /// <summary>
    /// Pre-filter evaluated by MongoDB. It keys off <see cref="CallSessionDocument.Date"/> rather than
    /// <c>StateChangedDate</c>: <c>Date &lt;= StateSince</c> always holds, so this cannot miss an expired
    /// session, and <see cref="SweepAsync"/> then applies the exact deadline in memory.
    /// </summary>
    private static FilterDefinition<CallSessionDocument> BuildCandidateFilter(
        CallsConfig config,
        int grace,
        int now)
    {
        var builder = Builders<CallSessionDocument>.Filter;

        FilterDefinition<CallSessionDocument> ForState(string state, int deadline) => builder.And(
            builder.Eq(s => s.State, state),
            builder.Lte(s => s.Date, now - deadline));

        return builder.Or(
            ForState(CallSessionStates.Requested, config.ReceiveTimeoutSeconds + grace),
            ForState(CallSessionStates.Received, config.RingTimeoutSeconds + grace),
            ForState(CallSessionStates.Accepted, config.ConnectTimeoutSeconds + grace),
            ForState(CallSessionStates.Confirmed, config.MaxCallDurationSeconds));
    }

    private static int? DeadlineSeconds(string state, CallsConfig config, int grace) => state switch
    {
        CallSessionStates.Requested => config.ReceiveTimeoutSeconds + grace,
        CallSessionStates.Received => config.RingTimeoutSeconds + grace,
        CallSessionStates.Accepted => config.ConnectTimeoutSeconds + grace,
        CallSessionStates.Confirmed => config.MaxCallDurationSeconds,
        _ => null
    };

    private async Task<bool> ExpireAsync(CallSessionDocument session, int now, CancellationToken cancellationToken)
    {
        var wasConnected = session.State == CallSessionStates.Confirmed;

        // A call that never connected has no duration; a connected one ran until now.
        int? duration = wasConnected ? Math.Max(0, now - session.StateSince) : null;
        var needRating = wasConnected && duration > 0;
        var needDebug = wasConnected;

        // Never answered => missed (what the caller's own timer would have reported).
        // Answered but never connected, or connected and abandoned => disconnect.
        var reasonName = session.State is CallSessionStates.Requested or CallSessionStates.Received
            ? "missed"
            : "disconnect";

        // Compare-and-set on State: if a real phone.discardCall landed between the query and here, it
        // already pushed the update and we must not push a second one.
        var claimFilter = Builders<CallSessionDocument>.Filter.And(
            Builders<CallSessionDocument>.Filter.Eq(s => s.CallId, session.CallId),
            Builders<CallSessionDocument>.Filter.Eq(s => s.State, session.State));

        var update = Builders<CallSessionDocument>.Update
            .Set(s => s.State, CallSessionStates.Discarded)
            .Set(s => s.StateChangedDate, now)
            .Set(s => s.Duration, duration)
            .Set(s => s.DiscardReason, reasonName)
            .Set(s => s.NeedRating, needRating)
            .Set(s => s.NeedDebug, needDebug);

        var result = await _callCollection.UpdateOneAsync(claimFilter, update, cancellationToken: cancellationToken);
        if (result.ModifiedCount == 0)
        {
            return false;
        }

        var discardedCall = new TPhoneCallDiscarded
        {
            Id = session.CallId,
            Reason = reasonName == "missed"
                ? new TPhoneCallDiscardReasonMissed()
                : new TPhoneCallDiscardReasonDisconnect(),
            Duration = duration,
            NeedRating = needRating,
            NeedDebug = needDebug,
            Video = session.Video
        };

        // The call still leaves its one message in both histories, from the caller, as on Telegram: a
        // missed call shows up as "Missed call" for the callee.
        await PhoneCallServiceMessage.SendAsync(
            messageAppService,
            session,
            discardedCall.Reason,
            duration,
            session.Video,
            requestInfo: null,
            layer: 0);

        // No device initiated this, so both participants - all of their sessions - must be told.
        foreach (var userId in new[] { session.CallerId, session.CalleeId })
        {
            await objectMessageSender.PushMessageToPeerAsync(
                new Peer(PeerType.User, userId),
                new TUpdates
                {
                    Updates = new TVector<IUpdate> { new TUpdatePhoneCall { PhoneCall = discardedCall } },
                    Users = new TVector<IUser>(),
                    Chats = new TVector<IChat>(),
                    Date = now
                });
        }

        logger.LogInformation(
            "Expired call {CallId} from state {State} after {Age}s (reason {Reason})",
            session.CallId,
            session.State,
            now - session.StateSince,
            reasonName);

        return true;
    }
}
