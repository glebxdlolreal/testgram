using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Messenger.Services;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;
using MyTelegram.Services.Phone;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;

internal sealed class AcceptCallHandler(
    IMongoDatabase mongoDatabase,
    IUserConverterService userConverterService,
    IObjectMessageSender objectMessageSender,
    IAccessHashHelper2 accessHashHelper2)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestAcceptCall, MyTelegram.Schema.Phone.IPhoneCall>
{
    private readonly IMongoCollection<CallSessionDocument> _callCollection =
        mongoDatabase.GetCollection<CallSessionDocument>("call_sessions");

    protected override async Task<MyTelegram.Schema.Phone.IPhoneCall> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestAcceptCall obj)
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

        if (session.CalleeId != input.UserId)
        {
            RpcErrors.RpcErrors400.CallPeerInvalid.ThrowRpcError();
            return null!;
        }

        ValidateHandshakeProtocol(obj.Protocol);

        if (!PhoneCallDhValidator.IsValidDhValue(obj.GB))
        {
            CallExtraRpcErrors.DhGBInvalid.ThrowRpcError();
            return null!;
        }

        if (!PhoneCallProtocolHelper.HasCommonLibraryVersion(
                session.CallerLibraryVersions,
                PhoneCallProtocolHelper.GetLibraryVersions(obj.Protocol)))
        {
            RpcErrors.RpcErrors406.CallProtocolCompatLayerInvalid.ThrowRpcError();
            return null!;
        }

        if (session.State == CallSessionStates.Accepted || session.State == CallSessionStates.Confirmed)
        {
            RpcErrors.RpcErrors400.CallAlreadyAccepted.ThrowRpcError();
            return null!;
        }

        if (session.State == CallSessionStates.Discarded)
        {
            RpcErrors.RpcErrors400.CallAlreadyDeclined.ThrowRpcError();
            return null!;
        }

        var currentDate = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var update = Builders<CallSessionDocument>.Update
            .Set(s => s.GB, obj.GB)
            .Set(s => s.CalleeLibraryVersions, [.. PhoneCallProtocolHelper.GetLibraryVersions(obj.Protocol)])
            .Set(s => s.CalleeConferenceSupported, PhoneCallProtocolHelper.AdvertisesConferenceSupport(obj.Protocol))
            .Set(s => s.State, CallSessionStates.Accepted)
            .Set(s => s.StateChangedDate, currentDate);

        // Compare-and-set on State: two devices of the callee accepting at once, or a discard landing in
        // between, must not both win.
        var claimFilter = Builders<CallSessionDocument>.Filter.And(
            Builders<CallSessionDocument>.Filter.Eq(s => s.CallId, session.CallId),
            Builders<CallSessionDocument>.Filter.Eq(s => s.State, session.State));
        if ((await _callCollection.UpdateOneAsync(claimFilter, update)).ModifiedCount == 0)
        {
            var current = await _callCollection.Find(s => s.CallId == session.CallId).FirstOrDefaultAsync();
            (current?.State == CallSessionStates.Discarded
                ? RpcErrors.RpcErrors400.CallAlreadyDeclined
                : RpcErrors.RpcErrors400.CallAlreadyAccepted).ThrowRpcError();
            return null!;
        }

        var phoneCallAcceptedForCaller = CreatePhoneCallAccepted(
            session,
            session.GetAccessHashForUser(session.CallerId),
            obj.GB,
            obj.Protocol,
            currentDate);
        var phoneCallWaitingForCallee = CreatePhoneCallWaiting(
            session,
            session.GetAccessHashForUser(session.CalleeId),
            obj.Protocol,
            currentDate);

        var users = await userConverterService.GetUserListAsync(input, new List<long> { session.CallerId, session.CalleeId }, false, false, input.Layer);
        var usersVector = new TVector<MyTelegram.Schema.IUser>(users);

        var updatePhoneCall = new MyTelegram.Schema.TUpdatePhoneCall { PhoneCall = phoneCallAcceptedForCaller };

        var callerPeer = new Peer(PeerType.User, session.CallerId);
        await objectMessageSender.PushMessageToPeerAsync(callerPeer,
            new TUpdates
            {
                Updates = new TVector<IUpdate> { updatePhoneCall },
                Users = usersVector,
                Chats = new TVector<IChat>(),
                Date = currentDate
            });

        // Requirement 4.3 / 30.2: the accepting callee's other devices receive a
        // phoneCallDiscarded so the call cannot be accepted twice. Push to all of the
        // callee's active sessions except the device that performed the accept.
        var phoneCallDiscardedForOtherDevices = new Schema.TPhoneCallDiscarded
        {
            Id = session.CallId,
            Video = session.Video
        };
        var calleePeer = new Peer(PeerType.User, session.CalleeId);
        await objectMessageSender.PushMessageToPeerAsync(calleePeer,
            new TUpdates
            {
                Updates = new TVector<IUpdate>
                {
                    new MyTelegram.Schema.TUpdatePhoneCall { PhoneCall = phoneCallDiscardedForOtherDevices }
                },
                Users = usersVector,
                Chats = new TVector<IChat>(),
                Date = currentDate
            },
            excludeAuthKeyId: input.AuthKeyId);

        return new MyTelegram.Schema.Phone.TPhoneCall
        {
            PhoneCall = phoneCallWaitingForCallee,
            Users = usersVector
        };
    }

    private static Schema.TPhoneCallAccepted CreatePhoneCallAccepted(
        CallSessionDocument session,
        long accessHash,
        byte[] gb,
        IPhoneCallProtocol? protocol,
        int currentDate)
    {
        return new Schema.TPhoneCallAccepted
        {
            Id = session.CallId,
            AccessHash = accessHash,
            AdminId = session.CallerId,
            ParticipantId = session.CalleeId,
            GB = gb,
            Protocol = PhoneCallProtocolHelper.Negotiate(session.CallerLibraryVersions, protocol),
            Date = session.Date,
            Video = session.Video
        };
    }

    private static Schema.TPhoneCallWaiting CreatePhoneCallWaiting(
        CallSessionDocument session,
        long accessHash,
        IPhoneCallProtocol? protocol,
        int currentDate)
    {
        return new Schema.TPhoneCallWaiting
        {
            Id = session.CallId,
            AccessHash = accessHash,
            AdminId = session.CallerId,
            ParticipantId = session.CalleeId,
            Protocol = PhoneCallProtocolHelper.Negotiate(session.CallerLibraryVersions, protocol),
            Date = session.Date,
            ReceiveDate = session.ReceivedDate ?? currentDate,
            Video = session.Video
        };
    }

    private static void ValidateHandshakeProtocol(IPhoneCallProtocol? protocol)
    {
        if (!PhoneCallProtocolHelper.HasValidLegacyFlags(protocol))
        {
            RpcErrors.RpcErrors400.CallProtocolFlagsInvalid.ThrowRpcError();
        }

        if (!PhoneCallProtocolHelper.HasValidLegacyLayers(protocol))
        {
            RpcErrors.RpcErrors400.CallProtocolLayerInvalid.ThrowRpcError();
        }
    }

}
