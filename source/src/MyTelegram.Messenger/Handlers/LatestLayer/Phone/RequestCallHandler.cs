using MongoDB.Driver;
using MyTelegram.Domain.Shared;
using MyTelegram.Messenger.Services.Caching;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Messenger.Services;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;
using MyTelegram.Services.Phone;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;

internal sealed class RequestCallHandler(
    IMongoDatabase mongoDatabase,
    IUserConverterService userConverterService,
    IObjectMessageSender objectMessageSender,
    IUserAccessHashKeyCache userAccessHashKeyCache,
    IAccessHashHelper2 accessHashHelper2,
    IBlockCacheAppService blockCacheAppService,
    IPrivacyAppService privacyAppService,
    IUserAppService userAppService)
    : RpcResultObjectHandler<RequestRequestCall, MyTelegram.Schema.Phone.IPhoneCall>
{
    private readonly IMongoCollection<CallSessionDocument> _callCollection =
        mongoDatabase.GetCollection<CallSessionDocument>("call_sessions");

    protected override async Task<MyTelegram.Schema.Phone.IPhoneCall> HandleCoreAsync(IRequestInput input, RequestRequestCall obj)
    {
        long calleeId;
        if (obj.UserId is TInputUser inputUser)
        {
            calleeId = inputUser.UserId;
        }
        else if (obj.UserId is TInputUserSelf)
        {
            calleeId = input.UserId;
        }
        else
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
            return null!;
        }

        if (calleeId == input.UserId)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
            return null!;
        }

        // The callee is identified by an InputUser supplied by the client, so its access_hash must be
        // validated before we act on it - otherwise any user id could be dialled by guessing.
        await accessHashHelper2.CheckAccessHashAsync(input, obj.UserId);

        // ...and the user has to actually exist and be callable, or we would persist a call session
        // pointing at nobody and ring into the void.
        // The long? overload returns null for a missing user; the long overload throws instead.
        var calleeReadModel = await userAppService.GetAsync((long?)calleeId);
        if (calleeReadModel == null)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
        }

        if (calleeReadModel!.IsDeleted == true)
        {
            RpcErrors.RpcErrors400.InputUserDeactivated.ThrowRpcError();
        }

        if (calleeReadModel.Bot)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
        }

        ValidateHandshakeProtocol(obj.Protocol);

        // R2.9: reject the request if the Caller has been blocked by the Callee. The
        // block list stores an entry keyed by the blocker; IsBlockedAsync(calleeId,
        // callerId) is true when the callee has the caller on their blocklist.
        if (await blockCacheAppService.IsBlockedAsync(calleeId, input.UserId))
        {
            RpcErrors.RpcErrors403.UserIsBlocked.ThrowRpcError();
        }

        // R2.8: honor the Callee's phoneCall privacy setting. ApplyPrivacyAsync reads
        // the target (callee) privacy rules and invokes the callback when the Caller is
        // not permitted to call, at which point we surface USER_PRIVACY_RESTRICTED.
        await privacyAppService.ApplyPrivacyAsync(input.UserId, calleeId, _ =>
        {
            RpcErrors.RpcErrors403.UserPrivacyRestricted.ThrowRpcError();
        }, PrivacyType.PhoneCall);

        var duplicateFilter = Builders<CallSessionDocument>.Filter.And(
            Builders<CallSessionDocument>.Filter.Eq(s => s.CallerId, input.UserId),
            Builders<CallSessionDocument>.Filter.Eq(s => s.RandomId, obj.RandomId));
        if (await _callCollection.Find(duplicateFilter).AnyAsync())
        {
            RpcErrors.RpcErrors500.RandomIdDuplicate.ThrowRpcError();
            return null!;
        }

        // No busy check: Telegram's servers let a call through to someone who is already ringing or talking
        // (and let a caller dial while their own call is unanswered); the callee's client declines it with
        // phoneCallDiscardReasonBusy itself. Observed on the production servers, 2026-11-01.

        var callId = Random.Shared.NextInt64();
        callId = Math.Abs(callId);
        if (callId == 0) callId = 1;

        await userAccessHashKeyCache.RememberAsync(input.UserId, input.AccessHashKeyId);

        var callerAccessHash = accessHashHelper2.GenerateAccessHash(input.UserId, input.AccessHashKeyId, callId, AccessHashType.Call);
        var calleeAccessHash = await CreateCallAccessHashForUserAsync(calleeId, callId);

        var currentDate = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var session = new CallSessionDocument
        {
            Id = callId,
            CallId = callId,
            AccessHash = callerAccessHash,
            CallerAccessHash = callerAccessHash,
            CalleeAccessHash = calleeAccessHash,
            CallerId = input.UserId,
            CalleeId = calleeId,
            RandomId = obj.RandomId,
            GAHash = obj.GAHash,
            Video = obj.Video,
            State = CallSessionStates.Requested,
            Date = currentDate,
            StateChangedDate = currentDate,
            CallerLibraryVersions = [.. PhoneCallProtocolHelper.GetLibraryVersions(obj.Protocol)],
            CallerConferenceSupported = PhoneCallProtocolHelper.AdvertisesConferenceSupport(obj.Protocol)
        };
        await _callCollection.InsertOneAsync(session);

        var phoneCallWaiting = new MyTelegram.Schema.TPhoneCallWaiting
        {
            Id = callId,
            AccessHash = callerAccessHash,
            AdminId = input.UserId,
            ParticipantId = calleeId,
            Date = currentDate,
            Protocol = PhoneCallProtocolHelper.Normalize(obj.Protocol),
            Video = obj.Video
        };

        var phoneCallRequested = new MyTelegram.Schema.TPhoneCallRequested
        {
            Id = callId,
            AccessHash = calleeAccessHash,
            AdminId = input.UserId,
            ParticipantId = calleeId,
            GAHash = obj.GAHash,
            Date = currentDate,
            Protocol = PhoneCallProtocolHelper.Normalize(obj.Protocol),
            Video = obj.Video
        };

        var users = await userConverterService.GetUserListAsync(input, new List<long> { input.UserId, calleeId }, false, false, input.Layer);
        var usersVector = new TVector<MyTelegram.Schema.IUser>(users);

        var updatePhoneCall = new MyTelegram.Schema.TUpdatePhoneCall { PhoneCall = phoneCallRequested };
        var incomingCallUpdates = new TUpdates
        {
            Updates = new TVector<IUpdate> { updatePhoneCall },
            Users = usersVector,
            Chats = new TVector<IChat>(),
            Date = currentDate
        };

        var calleePeer = new Peer(PeerType.User, calleeId);
        await objectMessageSender.PushMessageToPeerAsync(
            calleePeer,
            incomingCallUpdates,
            pushData: CreateIncomingCallPushData(input.UserId, calleeId, callId, calleeAccessHash, incomingCallUpdates, users));

        return new MyTelegram.Schema.Phone.TPhoneCall
        {
            PhoneCall = phoneCallWaiting,
            Users = usersVector
        };
    }

    private async Task<long> CreateCallAccessHashForUserAsync(long userId, long callId)
    {
        var accessHashKeyId = await userAccessHashKeyCache.GetAsync(userId);
        if (accessHashKeyId.HasValue)
        {
            return accessHashHelper2.GenerateAccessHash(userId, accessHashKeyId.Value, callId, AccessHashType.Call);
        }

        var fallbackAccessHash = Random.Shared.NextInt64();
        fallbackAccessHash = Math.Abs(fallbackAccessHash);
        return fallbackAccessHash == 0 ? 1 : fallbackAccessHash;
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

    private static PushData CreateIncomingCallPushData(
        long callerId,
        long calleeId,
        long callId,
        long accessHash,
        TUpdates updates,
        IReadOnlyCollection<IUser> users)
    {
        var callerName = callerId.ToString();
        var caller = users.OfType<TUser>().FirstOrDefault(user => user.Id == callerId);
        if (caller != null)
        {
            callerName = string.IsNullOrWhiteSpace(caller.LastName)
                ? caller.FirstName ?? callerName
                : $"{caller.FirstName} {caller.LastName}".Trim();
        }

        return new PushData(
            PushNotificationTypes.PhoneCallRequest,
            [callerName],
            calleeId,
            new PushNotificationCustomData
            {
                Updates = updates.ToBytes().ToBase64Url(),
                CallId = callId,
                CallAh = accessHash
            },
            null);
    }
}
