using MongoDB.Driver;
using MyTelegram.Domain.Shared;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;
using MyTelegram.Services.Phone;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;

internal sealed class ConfirmCallHandler(
    IMongoDatabase mongoDatabase,
    IUserConverterService userConverterService,
    IObjectMessageSender objectMessageSender,
    IOptions<MyTelegramMessengerServerOptions> optionsAccessor,
    IAccessHashHelper2 accessHashHelper2,
    IPrivacyAppService privacyAppService,
    ILogger<ConfirmCallHandler> logger)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestConfirmCall, MyTelegram.Schema.Phone.IPhoneCall>
{
    private readonly IMongoCollection<CallSessionDocument> _callCollection =
        mongoDatabase.GetCollection<CallSessionDocument>("call_sessions");

    protected override async Task<MyTelegram.Schema.Phone.IPhoneCall> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestConfirmCall obj)
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

        if (session.CallerId != input.UserId)
        {
            RpcErrors.RpcErrors400.CallPeerInvalid.ThrowRpcError();
            return null!;
        }

        if (session.State == CallSessionStates.Discarded)
        {
            RpcErrors.RpcErrors400.CallAlreadyDeclined.ThrowRpcError();
            return null!;
        }

        if (session.State != CallSessionStates.Accepted)
        {
            RpcErrors.RpcErrors400.CallPeerInvalid.ThrowRpcError();
            return null!;
        }

        if (session.GB == null || session.GB.Length == 0)
        {
            RpcErrors.RpcErrors400.CallPeerInvalid.ThrowRpcError();
            return null!;
        }

        if (!PhoneCallDhValidator.IsGaHashValid(obj.GA, session.GAHash))
        {
            RpcErrors.RpcErrors400.DhGAInvalid.ThrowRpcError();
            return null!;
        }

        if (!PhoneCallDhValidator.IsValidDhValue(obj.GA))
        {
            RpcErrors.RpcErrors400.DhGAInvalid.ThrowRpcError();
            return null!;
        }

        var currentDate = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // R5.6: peer-to-peer is allowed only when BOTH peers' privacyKeyPhoneP2P settings allow it. Telegram
        // still lists each server with its stun flag either way; p2p_allowed is what tells the clients.
        var p2pAllowed = await IsP2pAllowedForBothPeersAsync(session.CallerId, session.CalleeId);

        // Each participant gets their own TURN credentials for this call, as on Telegram.
        var webRtcConnections = optionsAccessor.Value.WebRtcConnections;
        var now = DateTimeOffset.UtcNow;
        var connections = PhoneCallConnections.Build(webRtcConnections, session.CallerId, session.CallId, now);
        var calleeConnections = PhoneCallConnections.Build(webRtcConnections, session.CalleeId, session.CallId, now);

        if (connections.Count == 0)
        {
            // The server cannot allocate media endpoints for this call. Surface it as an RPC error the
            // client understands instead of an unhandled exception (which reaches the client as an
            // opaque internal error), and do it BEFORE the session is moved to "confirmed" so a
            // misconfigured server leaves the call recoverable rather than wedged.
            logger.LogError(
                "Cannot confirm call {CallId}: no WebRTC connections configured. Set App__WebRtcConnections in .env",
                session.CallId);
            RpcErrors.RpcErrors500.CallOccupyFailed.ThrowRpcError();
            return null!;
        }

        if (!PhoneCallProtocolHelper.HasCommonLibraryVersion(session.CallerLibraryVersions, session.CalleeLibraryVersions))
        {
            RpcErrors.RpcErrors406.CallProtocolCompatLayerInvalid.ThrowRpcError();
            return null!;
        }

        // Every failure mode is behind us - only now commit the state transition.
        var update = Builders<CallSessionDocument>.Update
            .Set(s => s.GA, obj.GA)
            .Set(s => s.KeyFingerprint, obj.KeyFingerprint)
            .Set(s => s.State, CallSessionStates.Confirmed)
            .Set(s => s.StateChangedDate, currentDate);

        // Compare-and-set on State: a discard (or the expiry sweeper) landing since the read wins.
        var claimFilter = Builders<CallSessionDocument>.Filter.And(
            Builders<CallSessionDocument>.Filter.Eq(s => s.CallId, session.CallId),
            Builders<CallSessionDocument>.Filter.Eq(s => s.State, CallSessionStates.Accepted));
        if ((await _callCollection.UpdateOneAsync(claimFilter, update)).ModifiedCount == 0)
        {
            RpcErrors.RpcErrors400.CallAlreadyDeclined.ThrowRpcError();
            return null!;
        }

        var protocol = PhoneCallProtocolHelper.Negotiate(session.CallerLibraryVersions, session.CalleeLibraryVersions);
        var conferenceSupported = session.CallerConferenceSupported && session.CalleeConferenceSupported;
        var phoneCallForCaller = CreatePhoneCall(
            session,
            session.GetAccessHashForUser(session.CallerId),
            session.GB,
            obj.KeyFingerprint,
            protocol,
            currentDate,
            connections,
            conferenceSupported,
            p2pAllowed);
        var phoneCallForCallee = CreatePhoneCall(
            session,
            session.GetAccessHashForUser(session.CalleeId),
            obj.GA,
            obj.KeyFingerprint,
            protocol,
            currentDate,
            calleeConnections,
            conferenceSupported,
            p2pAllowed);

        var users = await userConverterService.GetUserListAsync(input, new List<long> { session.CallerId, session.CalleeId }, false, false, input.Layer);
        var usersVector = new TVector<MyTelegram.Schema.IUser>(users);

        var updatePhoneCall = new MyTelegram.Schema.TUpdatePhoneCall { PhoneCall = phoneCallForCallee };

        var otherUserId = input.UserId == session.CallerId ? session.CalleeId : session.CallerId;
        var otherPeer = new Peer(PeerType.User, otherUserId);
        await objectMessageSender.PushMessageToPeerAsync(otherPeer,
            new TUpdates
            {
                Updates = new TVector<IUpdate> { updatePhoneCall },
                Users = usersVector,
                Chats = new TVector<IChat>(),
                Date = currentDate
            });

        return new MyTelegram.Schema.Phone.TPhoneCall
        {
            PhoneCall = phoneCallForCaller,
            Users = usersVector
        };
    }

    private static Schema.TPhoneCall CreatePhoneCall(
        CallSessionDocument session,
        long accessHash,
        byte[] ga,
        long keyFingerprint,
        IPhoneCallProtocol protocol,
        int currentDate,
        TVector<MyTelegram.Schema.IPhoneConnection> connections,
        bool conferenceSupported,
        bool p2pAllowed)
    {
        return new Schema.TPhoneCall
        {
            Id = session.CallId,
            AccessHash = accessHash,
            AdminId = session.CallerId,
            ParticipantId = session.CalleeId,
            GAOrB = ga,
            KeyFingerprint = keyFingerprint,
            // R5.6: p2p_allowed reflects both peers' privacyKeyPhoneP2P privacy settings.
            P2pAllowed = p2pAllowed,
            ConferenceSupported = conferenceSupported,
            Protocol = protocol,
            Date = session.Date,
            StartDate = currentDate,
            Connections = connections,
            Video = session.Video
        };
    }

    // R5.6: peer-to-peer is only permitted when BOTH the caller and the callee allow
    // P2P via their privacyKeyPhoneP2P setting. ApplyPrivacyAsync invokes the callback
    // when the target user is not permitted by the self user's privacy rules; a fired
    // callback in either direction means P2P must be disabled.
    private async Task<bool> IsP2pAllowedForBothPeersAsync(long callerId, long calleeId)
    {
        var allowed = true;

        await privacyAppService.ApplyPrivacyAsync(callerId, calleeId, _ =>
        {
            allowed = false;
        }, PrivacyType.PhoneP2P);

        await privacyAppService.ApplyPrivacyAsync(calleeId, callerId, _ =>
        {
            allowed = false;
        }, PrivacyType.PhoneP2P);

        return allowed;
    }
}
