using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Messenger.Services;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;

internal sealed class DiscardCallHandler(
    IMongoDatabase mongoDatabase,
    IUserConverterService userConverterService,
    IObjectMessageSender objectMessageSender,
    IMessageAppService messageAppService,
    ITopPeerUsageRecorder topPeerUsageRecorder,
    IAccessHashHelper2 accessHashHelper2)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestDiscardCall, IUpdates>
{
    private readonly IMongoCollection<CallSessionDocument> _callCollection =
        mongoDatabase.GetCollection<CallSessionDocument>("call_sessions");

    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestDiscardCall obj)
    {
        if (obj.Peer is not TInputPhoneCall inputPhoneCall)
        {
            RpcErrors.RpcErrors400.CallPeerInvalid.ThrowRpcError();
            return null!;
        }

        var filter = Builders<CallSessionDocument>.Filter.Eq(s => s.CallId, inputPhoneCall.Id);

        var session = await _callCollection.Find(filter).FirstOrDefaultAsync();
        if (session == null ||
            (!session.HasAccessHashForUser(input.UserId, inputPhoneCall.AccessHash) &&
             !await accessHashHelper2.IsAccessHashValidAsync(input, inputPhoneCall.Id, inputPhoneCall.AccessHash, AccessHashType.Call)))
        {
            RpcErrors.RpcErrors400.CallPeerInvalid.ThrowRpcError();
            return null!;
        }

        if (session.CallerId != input.UserId && session.CalleeId != input.UserId)
        {
            RpcErrors.RpcErrors400.CallPeerInvalid.ThrowRpcError();
            return null!;
        }

        var currentDate = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var users = await userConverterService.GetUserListAsync(input, new List<long> { session.CallerId, session.CalleeId }, false, false, input.Layer);
        var usersVector = new TVector<MyTelegram.Schema.IUser>(users);

        // Requirement 7.9: idempotent re-discard - return the existing phoneCallDiscarded
        // without mutating recorded state and without re-pushing updates / service messages.
        if (session.State == CallSessionStates.Discarded)
        {
            // The user who ended a call that never connected was told no reason; tell them the same again.
            var hideReason = !session.NeedDebug && session.DiscardedByUserId == input.UserId;
            var existingDiscardedCall = new Schema.TPhoneCallDiscarded
            {
                Id = session.CallId,
                Reason = hideReason ? null : BuildStoredReason(session.DiscardReason, session.DiscardReasonSlug),
                Duration = session.Duration,
                NeedRating = session.NeedRating,
                NeedDebug = session.NeedDebug,
                Video = session.Video
            };

            return new TUpdates
            {
                Updates = new TVector<IUpdate> { new TUpdatePhoneCall { PhoneCall = existingDiscardedCall } },
                Users = usersVector,
                Chats = new TVector<IChat>(),
                Date = currentDate
            };
        }

        var reason = ConvertReason(obj.Reason);
        var reasonSlug = (obj.Reason as TPhoneCallDiscardReasonMigrateConferenceCall)?.Slug;

        // Requirement 7.4/7.5: rating/debug policy - a call is rateable/debuggable only if it
        // reached the connected (confirmed) state; rating additionally requires a non-zero duration.
        var connected = session.State == CallSessionStates.Confirmed;
        var needRating = connected && obj.Duration > 0;
        var needDebug = connected;
        // A call that never connected has no duration (Telegram reports none for a decline or a cancel).
        int? duration = connected ? obj.Duration : null;
        var video = session.Video || obj.Video;

        var update = Builders<CallSessionDocument>.Update
            .Set(s => s.State, CallSessionStates.Discarded)
            .Set(s => s.StateChangedDate, currentDate)
            .Set(s => s.Duration, duration)
            .Set(s => s.DiscardReason, reason)
            .Set(s => s.DiscardReasonSlug, reasonSlug)
            .Set(s => s.NeedRating, needRating)
            .Set(s => s.NeedDebug, needDebug)
            .Set(s => s.DiscardedByUserId, input.UserId)
            .Set(s => s.Video, video);

        // Compare-and-set on State, as the expiry sweeper does: the other side or the sweeper discarding
        // at the same moment must not produce a second phoneCallDiscarded or a second call message.
        var claimFilter = Builders<CallSessionDocument>.Filter.And(
            Builders<CallSessionDocument>.Filter.Eq(s => s.CallId, session.CallId),
            Builders<CallSessionDocument>.Filter.Eq(s => s.State, session.State));
        if ((await _callCollection.UpdateOneAsync(claimFilter, update)).ModifiedCount == 0)
        {
            var current = await _callCollection.Find(s => s.CallId == session.CallId).FirstOrDefaultAsync();
            return new TUpdates
            {
                Updates = new TVector<IUpdate>
                {
                    new TUpdatePhoneCall
                    {
                        PhoneCall = new Schema.TPhoneCallDiscarded
                        {
                            Id = session.CallId,
                            Reason = BuildStoredReason(current?.DiscardReason, current?.DiscardReasonSlug),
                            Duration = current?.Duration,
                            NeedRating = current?.NeedRating ?? false,
                            NeedDebug = current?.NeedDebug ?? false,
                            Video = current?.Video ?? session.Video
                        }
                    }
                },
                Users = usersVector,
                Chats = new TVector<IChat>(),
                Date = currentDate
            };
        }

        // The other side learns why the call ended. The side that ended a call which never connected
        // (declined, cancelled) gets its own copy without a reason, as on Telegram.
        var discardedForOtherSide = new Schema.TPhoneCallDiscarded
        {
            Id = session.CallId,
            Reason = obj.Reason,
            Duration = duration,
            NeedRating = needRating,
            NeedDebug = needDebug,
            Video = video
        };
        var discardedForSelf = new Schema.TPhoneCallDiscarded
        {
            Id = session.CallId,
            Reason = connected ? obj.Reason : null,
            Duration = duration,
            NeedRating = needRating,
            NeedDebug = needDebug,
            Video = video
        };

        var updatePhoneCall = new TUpdatePhoneCall { PhoneCall = discardedForSelf };

        var otherUserId = input.UserId == session.CallerId ? session.CalleeId : session.CallerId;
        var otherPeer = new Peer(PeerType.User, otherUserId);
        await objectMessageSender.PushMessageToPeerAsync(otherPeer,
            new TUpdates
            {
                Updates = new TVector<IUpdate> { new TUpdatePhoneCall { PhoneCall = discardedForOtherSide } },
                Users = usersVector,
                Chats = new TVector<IChat>(),
                Date = currentDate
            });

        // The discarding user's OTHER devices must stop ringing / tear down their controller too, so they
        // get the same phoneCallDiscarded. The device that issued phone.discardCall already has the result
        // of this very request and is excluded via excludeAuthKeyId (same pattern as AcceptCallHandler).
        var selfPeer = new Peer(PeerType.User, input.UserId);
        await objectMessageSender.PushMessageToPeerAsync(selfPeer,
            new TUpdates
            {
                Updates = new TVector<IUpdate> { new TUpdatePhoneCall { PhoneCall = discardedForSelf } },
                Users = usersVector,
                Chats = new TVector<IChat>(),
                Date = currentDate
            },
            excludeAuthKeyId: input.AuthKeyId);

        await PhoneCallServiceMessage.SendAsync(
            messageAppService,
            session,
            obj.Reason,
            duration,
            video,
            input.ToRequestInfo(),
            input.Layer);

        // A finished call is what topPeerCategoryPhoneCalls ranks, and it counts for both parties: the
        // other side becomes a likely call destination for each of them. Recorded here rather than at
        // phone.requestCall so a call is counted once — the idempotent re-discard above returns early.
        // See https://corefork.telegram.org/api/top-rating
        await topPeerUsageRecorder.RecordAsync(session.CallerId, TopPeerCategory.PhoneCalls, PeerType.User,
            session.CalleeId);
        await topPeerUsageRecorder.RecordAsync(session.CalleeId, TopPeerCategory.PhoneCalls, PeerType.User,
            session.CallerId);

        return new TUpdates
        {
            Updates = new TVector<IUpdate> { updatePhoneCall },
            Users = usersVector,
            Chats = new TVector<IChat>(),
            Date = currentDate
        };
    }

    private static string? ConvertReason(IPhoneCallDiscardReason? reason)
    {
        return reason switch
        {
            TPhoneCallDiscardReasonMissed => "missed",
            TPhoneCallDiscardReasonDisconnect => "disconnect",
            TPhoneCallDiscardReasonHangup => "hangup",
            TPhoneCallDiscardReasonBusy => "busy",
            TPhoneCallDiscardReasonMigrateConferenceCall => "migrate",
            _ => null
        };
    }

    private static IPhoneCallDiscardReason? BuildStoredReason(string? reason, string? slug)
    {
        return reason switch
        {
            "missed" => new TPhoneCallDiscardReasonMissed(),
            "disconnect" => new TPhoneCallDiscardReasonDisconnect(),
            "hangup" => new TPhoneCallDiscardReasonHangup(),
            "busy" => new TPhoneCallDiscardReasonBusy(),
            "migrate" => new TPhoneCallDiscardReasonMigrateConferenceCall { Slug = slug ?? string.Empty },
            _ => null
        };
    }
}
