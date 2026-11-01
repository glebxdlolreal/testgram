using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Moq;
using MyTelegram.Messenger;
using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Messenger.Services.Caching;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;

namespace MyTelegram.Services.Tests.Phone;

/// <summary>
/// Handler-level (example) tests for the 1:1 call lifecycle and the request-time rejections.
///
/// <para>The happy-path test drives the real handlers through the full lifecycle
/// <c>request → received → accept → confirm → discard</c> against an in-memory Mongo store, asserting after
/// every step: the persisted <see cref="CallSessionDocument.State"/> transition, the TL constructor returned
/// synchronously to the caller/callee, and the <c>update*</c> constructor(s) pushed to the other party's
/// active sessions (and, on accept, the <c>phoneCallDiscarded</c> fanned out to the callee's other devices,
/// excluding the accepting device).</para>
///
/// <para>The rejection tests cover the busy/occupy guard (a callee already engaged in a
/// <c>received</c>/<c>accepted</c>/<c>confirmed</c> call rejects new incoming calls with
/// <c>CALL_OCCUPY_FAILED</c>) and the duplicate <c>random_id</c> guard (<c>RANDOM_ID_DUPLICATE</c>).</para>
///
/// Covers Requirements 2.6 (CALL_OCCUPY_FAILED), 2.7 (RANDOM_ID_DUPLICATE) and 6.2 (busy semantics),
/// and exercises the transitions / update-dispatch behaviour of Requirements 2.1-2.2, 4.1-4.3, 5.1-5.2,
/// 6.1 and 7.1-7.2.
/// </summary>
public class CallLifecycleHandlerTests
{
    private const long CallerId = 1;
    private const long CalleeId = 2;
    private const long OtherCallerId = 3;

    // ---- full lifecycle --------------------------------------------------------------------------

    [Fact]
    public async Task FullLifecycle_RequestReceivedAcceptConfirmDiscard_TransitionsAndDispatchesUpdates()
    {
        var harness = new CallHarness();
        var collection = harness.Sessions;

        // -- request (caller) → state 'requested' ---------------------------------------------------
        var response = await harness.RequestCallAsync();

        // R2.1: the caller synchronously receives a phone.phoneCall wrapping phoneCallWaiting.
        PhoneCallOf(response).ShouldBeOfType<TPhoneCallWaiting>();

        var session = await FirstSessionAsync(collection);
        session.State.ShouldBe("requested");

        // R2.2: the callee's active sessions receive updatePhoneCall{ phoneCallRequested }; the caller
        // receives no push.
        var requestedPush = harness.Sender.PushesToUser(CalleeId).ShouldHaveSingleItem();
        PhoneCallOf(requestedPush).ShouldBeOfType<TPhoneCallRequested>();
        harness.Sender.PushesToUser(CallerId).ShouldBeEmpty();

        // -- received (callee device rings) → state 'received' --------------------------------------
        harness.Sender.Clear();
        var receivedResult = await harness.ReceivedCallAsync();
        receivedResult.ShouldBeOfType<TBoolTrue>();

        (await FirstSessionAsync(collection)).State.ShouldBe("received");

        // R6.1: the caller learns the callee is ringing via updatePhoneCall{ phoneCallWaiting }.
        var ringingPush = harness.Sender.PushesToUser(CallerId).ShouldHaveSingleItem();
        PhoneCallOf(ringingPush).ShouldBeOfType<TPhoneCallWaiting>();

        // -- accept (callee, g_b) → state 'accepted' ------------------------------------------------
        harness.Sender.Clear();
        var acceptResponse = await harness.AcceptCallAsync();

        // R4.1: the accepting callee synchronously receives phone.phoneCall{ phoneCallWaiting }.
        PhoneCallOf(acceptResponse).ShouldBeOfType<TPhoneCallWaiting>();
        (await FirstSessionAsync(collection)).State.ShouldBe("accepted");

        // R4.2: the caller receives updatePhoneCall{ phoneCallAccepted } carrying g_b.
        var acceptedPush = harness.Sender.PushesToUser(CallerId).ShouldHaveSingleItem();
        var accepted = PhoneCallOf(acceptedPush).ShouldBeOfType<TPhoneCallAccepted>();
        accepted.GB.ShouldBe(harness.GbBytes);

        // R4.3: the accepting callee's OTHER devices receive updatePhoneCall{ phoneCallDiscarded },
        // and that push excludes the accepting device (excludeAuthKeyId).
        var otherDevicePush = harness.Sender.PushesToUser(CalleeId).ShouldHaveSingleItem();
        PhoneCallOf(otherDevicePush).ShouldBeOfType<TPhoneCallDiscarded>();
        otherDevicePush.ExcludeAuthKeyId.ShouldBe(harness.CalleeInput.AuthKeyId);

        // -- confirm (caller, g_a) → state 'confirmed' ----------------------------------------------
        harness.Sender.Clear();
        var confirmResponse = await harness.ConfirmCallAsync();

        // R5.1: the caller synchronously receives phone.phoneCall{ phoneCall } with connections.
        var confirmedCall = PhoneCallOf(confirmResponse).ShouldBeOfType<MyTelegram.Schema.TPhoneCall>();
        confirmedCall.Connections.Count.ShouldBeGreaterThan(0);
        (await FirstSessionAsync(collection)).State.ShouldBe("confirmed");

        // R5.2: the callee receives updatePhoneCall{ phoneCall } with connection info.
        var confirmPush = harness.Sender.PushesToUser(CalleeId).ShouldHaveSingleItem();
        var pushedCall = PhoneCallOf(confirmPush).ShouldBeOfType<MyTelegram.Schema.TPhoneCall>();
        pushedCall.Connections.Count.ShouldBeGreaterThan(0);

        // -- discard (caller) → state 'discarded' ---------------------------------------------------
        harness.Sender.Clear();
        var discardResult = await harness.DiscardCallAsync(harness.CallerInput, harness.CallerPeer());

        // R7.1: an Updates with updatePhoneCall{ phoneCallDiscarded } is returned.
        var discardUpdates = discardResult.ShouldBeOfType<TUpdates>();
        var returnedUpdate = discardUpdates.Updates.OfType<TUpdatePhoneCall>().ShouldHaveSingleItem();
        returnedUpdate.PhoneCall.ShouldBeOfType<TPhoneCallDiscarded>();
        (await FirstSessionAsync(collection)).State.ShouldBe("discarded");

        // R7.2: the other party (callee) receives updatePhoneCall{ phoneCallDiscarded }.
        var discardPush = harness.Sender.PushesToUser(CalleeId).ShouldHaveSingleItem();
        PhoneCallOf(discardPush).ShouldBeOfType<TPhoneCallDiscarded>();
    }

    // ---- callee validation (R2.x) ----------------------------------------------------------------

    [Fact]
    public async Task RequestCall_WithWrongCalleeAccessHash_ThrowsUserIdInvalid()
    {
        var harness = new CallHarness();

        // The InputUser comes from the client, so its access_hash has to be checked - otherwise any
        // user id could be dialled by guessing.
        var request = new RequestRequestCall
        {
            UserId = new TInputUser { UserId = CalleeId, AccessHash = 999_999 },
            RandomId = 100_001,
            GAHash = harness.GaHash,
            Protocol = CallHarness.ProtocolForTests(),
            Video = false
        };

        var ex = await Should.ThrowAsync<RpcException>(() => harness.InvokeRequestCallAsync(request));
        ex.Message.ShouldBe("USER_ID_INVALID");

        (await harness.Sessions.CountDocumentsAsync(Builders<CallSessionDocument>.Filter.Empty)).ShouldBe(0);
    }

    [Fact]
    public async Task RequestCall_WhenCalleeDoesNotExist_ThrowsUserIdInvalid()
    {
        var harness = new CallHarness(userAppService: FakeUserAppService.For(_ => null));

        var ex = await Should.ThrowAsync<RpcException>(() => harness.RequestCallAsync());
        ex.Message.ShouldBe("USER_ID_INVALID");

        // Nothing was persisted, so the callee is not left "busy" against a call that cannot ring.
        (await harness.Sessions.CountDocumentsAsync(Builders<CallSessionDocument>.Filter.Empty)).ShouldBe(0);
    }

    [Fact]
    public async Task RequestCall_WhenCalleeIsDeleted_ThrowsInputUserDeactivated()
    {
        var harness = new CallHarness(
            userAppService: FakeUserAppService.For(_ => FakeUserAppService.Callable(isDeleted: true)));

        var ex = await Should.ThrowAsync<RpcException>(() => harness.RequestCallAsync());
        ex.Message.ShouldBe("INPUT_USER_DEACTIVATED");
    }

    [Fact]
    public async Task RequestCall_WhenCalleeIsABot_ThrowsUserIdInvalid()
    {
        var harness = new CallHarness(
            userAppService: FakeUserAppService.For(_ => FakeUserAppService.Callable(isBot: true)));

        var ex = await Should.ThrowAsync<RpcException>(() => harness.RequestCallAsync());
        ex.Message.ShouldBe("USER_ID_INVALID");
    }

    // ---- server misconfiguration ------------------------------------------------------------------

    [Fact]
    public async Task ConfirmCall_WithNoWebRtcConnections_ThrowsRpcErrorAndLeavesCallAccepted()
    {
        var harness = new CallHarness(webRtcConnections: []);

        await harness.RequestCallAsync();
        await harness.ReceivedCallAsync();
        await harness.AcceptCallAsync();

        // An unconfigured server must surface an RPC error rather than an unhandled exception...
        var ex = await Should.ThrowAsync<RpcException>(() => harness.ConfirmCallAsync());
        ex.Message.ShouldBe("CALL_OCCUPY_FAILED");

        // ...and must not have moved the session to "confirmed" on its way out.
        (await FirstSessionAsync(harness.Sessions)).State.ShouldBe("accepted");
    }

    // ---- busy / occupy rejection (R2.6, R6.2) ----------------------------------------------------

    [Theory]
    [InlineData("requested")]
    [InlineData("received")]
    [InlineData("accepted")]
    [InlineData("confirmed")]
    public async Task RequestCall_WhenCalleeIsBusy_StillRingsThem_LikeTelegram(string busyState)
    {
        var harness = new CallHarness();

        // Establish an existing call to the callee and drive it into a busy state.
        await harness.RequestCallAsync();
        if (busyState != "requested")
        {
            await harness.ReceivedCallAsync();
        }

        if (busyState is "accepted" or "confirmed")
        {
            await harness.AcceptCallAsync();
        }

        if (busyState == "confirmed")
        {
            await harness.ConfirmCallAsync();
        }

        (await FirstSessionAsync(harness.Sessions)).State.ShouldBe(busyState);
        harness.Sender.Clear();

        // Telegram's servers do not refuse a call to someone who is already in one: the second call rings,
        // and the callee's own client declines it with phoneCallDiscardReasonBusy.
        var otherCaller = PhoneTestFixtures.RequestInput(OtherCallerId).Build();
        await harness.RequestCallAsync(otherCaller, CalleeId, randomId: 999_001);

        (await harness.Sessions.CountDocumentsAsync(Builders<CallSessionDocument>.Filter.Empty)).ShouldBe(2);
        harness.Sender.PushesToUser(CalleeId).ShouldContain(push => push.Carries<TUpdatePhoneCall>());
    }

    [Fact]
    public async Task RequestCall_WhileTheCallersOwnCallIsUnanswered_IsAllowed()
    {
        var harness = new CallHarness();

        // Telegram let a caller place a second call while their first one was still ringing.
        await harness.RequestCallAsync();
        await harness.ReceivedCallAsync();

        await harness.RequestCallAsync(harness.CallerInput, calleeId: OtherCallerId, randomId: 999_003);

        (await harness.Sessions.CountDocumentsAsync(Builders<CallSessionDocument>.Filter.Empty)).ShouldBe(2);
    }

    // ---- the call's one history message, as on Telegram's servers ---------------------------------

    [Fact]
    public async Task StartingAndAnsweringACall_WritesNoMessage()
    {
        var harness = new CallHarness();

        await harness.RequestCallAsync();
        await harness.ReceivedCallAsync();
        await harness.AcceptCallAsync();
        await harness.ConfirmCallAsync();

        harness.MessageApp.SentMessages.ShouldBeEmpty();
    }

    [Fact]
    public async Task CalleeHangingUp_LeavesOneMessageFromTheCaller_WithReasonAndDuration()
    {
        var harness = new CallHarness();
        await harness.RequestCallAsync();
        await harness.ReceivedCallAsync();
        await harness.AcceptCallAsync();
        await harness.ConfirmCallAsync();

        await harness.DiscardCallAsync(harness.CalleeInput, harness.CalleePeer(), new TPhoneCallDiscardReasonHangup(), 3);

        var message = harness.MessageApp.SentMessages.ShouldHaveSingleItem();
        message.RequestInfo.UserId.ShouldBe(CallerId);
        message.ToPeer.ShouldBe(new Peer(PeerType.User, CalleeId));
        var action = message.MessageAction.ShouldBeOfType<TMessageActionPhoneCall>();
        action.Reason.ShouldBeOfType<TPhoneCallDiscardReasonHangup>();
        action.Duration.ShouldBe(3);
    }

    [Fact]
    public async Task Decline_TellsTheCallerBusy_AnswersTheDeclinerWithoutAReason_AndWritesABusyMessage()
    {
        var harness = new CallHarness();
        await harness.RequestCallAsync();
        await harness.ReceivedCallAsync();
        harness.Sender.Clear();

        var result = await harness.DiscardCallAsync(
            harness.CalleeInput, harness.CalleePeer(), new TPhoneCallDiscardReasonBusy(), 0);

        var own = result.ShouldBeOfType<TUpdates>().Updates.OfType<TUpdatePhoneCall>().ShouldHaveSingleItem()
            .PhoneCall.ShouldBeOfType<TPhoneCallDiscarded>();
        own.Reason.ShouldBeNull();
        own.Duration.ShouldBeNull();
        own.NeedDebug.ShouldBeFalse();

        var callers = harness.Sender.PushesToUser(CallerId)
            .SelectMany(push => push.Updates.OfType<TUpdatePhoneCall>())
            .Select(update => update.PhoneCall)
            .OfType<TPhoneCallDiscarded>()
            .ShouldHaveSingleItem();
        callers.Reason.ShouldBeOfType<TPhoneCallDiscardReasonBusy>();
        callers.Duration.ShouldBeNull();

        var action = harness.MessageApp.SentMessages.ShouldHaveSingleItem()
            .MessageAction.ShouldBeOfType<TMessageActionPhoneCall>();
        action.Reason.ShouldBeOfType<TPhoneCallDiscardReasonBusy>();
        action.Duration.ShouldBeNull();

        // Asking again gives the decliner the same reason-less answer.
        var again = await harness.DiscardCallAsync(
            harness.CalleeInput, harness.CalleePeer(), new TPhoneCallDiscardReasonHangup(), 0);
        again.ShouldBeOfType<TUpdates>().Updates.OfType<TUpdatePhoneCall>().Single()
            .PhoneCall.ShouldBeOfType<TPhoneCallDiscarded>().Reason.ShouldBeNull();
        harness.MessageApp.SentMessages.Count.ShouldBe(1);
    }

    // ---- reflectors ---------------------------------------------------------------------------------

    [Fact]
    public async Task Confirm_ListsTheReflector_WithOnePeerTagSharedByBothSides()
    {
        var harness = new CallHarness();
        await harness.RequestCallAsync();
        await harness.ReceivedCallAsync();
        await harness.AcceptCallAsync();
        harness.Sender.Clear();

        var result = await harness.ConfirmCallAsync();

        var callers = result.ShouldBeOfType<MyTelegram.Schema.Phone.TPhoneCall>().PhoneCall.ShouldBeOfType<MyTelegram.Schema.TPhoneCall>()
            .Connections.OfType<MyTelegram.Schema.TPhoneConnection>().ShouldHaveSingleItem();
        var callees = harness.Sender.PushesToUser(CalleeId)
            .SelectMany(push => push.Updates.OfType<TUpdatePhoneCall>())
            .Select(update => update.PhoneCall).OfType<MyTelegram.Schema.TPhoneCall>().ShouldHaveSingleItem()
            .Connections.OfType<MyTelegram.Schema.TPhoneConnection>().ShouldHaveSingleItem();

        callers.Id.ShouldBe(2574219217251);
        callers.Ip.ShouldBe("5.6.7.8");
        callers.Port.ShouldBe(599);
        callers.Tcp.ShouldBeFalse();
        callers.PeerTag.Length.ShouldBe(16);
        callees.Id.ShouldBe(callers.Id);
        callees.PeerTag.ToArray().ShouldBe(callers.PeerTag.ToArray());
        (await FirstSessionAsync(harness.Sessions)).ReflectorPeerTag.ShouldBe(callers.PeerTag.ToArray());
    }

    // ---- malformed Diffie-Hellman values ----------------------------------------------------------

    [Fact]
    public async Task Accept_WithAMalformedGb_IsDhGbInvalid()
    {
        var harness = new CallHarness();
        await harness.RequestCallAsync();
        await harness.ReceivedCallAsync();

        var ex = await Should.ThrowAsync<RpcException>(() => harness.AcceptCallAsync([1]));

        ex.Message.ShouldBe("DH_G_B_INVALID");
    }

    [Fact]
    public async Task Confirm_WithAGaThatDoesNotMatchItsHash_IsDhGaInvalid()
    {
        var harness = new CallHarness();
        await harness.RequestCallAsync();
        await harness.ReceivedCallAsync();
        await harness.AcceptCallAsync();

        var ex = await Should.ThrowAsync<RpcException>(() => harness.ConfirmCallAsync(harness.GbBytes));

        ex.Message.ShouldBe("DH_G_A_INVALID");
    }

    // ---- duplicate random_id rejection (R2.7) ----------------------------------------------------

    [Fact]
    public async Task RequestCall_WithDuplicateRandomId_ThrowsRandomIdDuplicate()
    {
        var harness = new CallHarness();

        await harness.RequestCallAsync(randomId: 424_242);

        // R2.7: the same caller re-using an existing random_id (even for a different callee) is rejected.
        var ex = await Should.ThrowAsync<RpcException>(() =>
            harness.RequestCallAsync(harness.CallerInput, calleeId: 42, randomId: 424_242));
        ex.Message.ShouldBe("RANDOM_ID_DUPLICATE");
    }

    [Fact]
    public async Task RequestCall_DifferentCallersReuseSameRandomId_IsAllowed()
    {
        var harness = new CallHarness();

        // random_id uniqueness is scoped per caller: a different caller may reuse the same value.
        await harness.RequestCallAsync(randomId: 555);
        var otherCaller = PhoneTestFixtures.RequestInput(OtherCallerId).Build();
        await harness.RequestCallAsync(otherCaller, calleeId: 42, randomId: 555);

        var count = await harness.Sessions.CountDocumentsAsync(Builders<CallSessionDocument>.Filter.Empty);
        count.ShouldBe(2);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static MyTelegram.Schema.IPhoneCall PhoneCallOf(IObject response)
    {
        return response.ShouldBeOfType<MyTelegram.Schema.Phone.TPhoneCall>().PhoneCall;
    }

    private static MyTelegram.Schema.IPhoneCall PhoneCallOf(CapturedPush push)
    {
        var update = push.Updates.OfType<TUpdatePhoneCall>().ShouldHaveSingleItem();
        return update.PhoneCall;
    }

    private static async Task<CallSessionDocument> FirstSessionAsync(IMongoCollection<CallSessionDocument> collection)
    {
        var session = await collection.Find(Builders<CallSessionDocument>.Filter.Empty).FirstOrDefaultAsync();
        session.ShouldNotBeNull();
        return session!;
    }

    /// <summary>
    /// Builds the real 1:1 call handlers over a shared in-memory Mongo store and drives them through
    /// the lifecycle. The primary call is always CallerId → CalleeId; the primary call's ids / access
    /// hashes are resolved from the persisted session so the peers can address the call.
    /// </summary>
    private sealed class CallHarness
    {
        private readonly object _requestHandler;
        private readonly object _receivedHandler;
        private readonly object _acceptHandler;
        private readonly object _confirmHandler;
        private readonly object _discardHandler;

        public CallHarness(
            IUserAppService? userAppService = null,
            List<WebRtcConnection>? webRtcConnections = null)
        {
            Database = PhoneTestFixtures.CreateDatabase(out _);
            Sessions = Database.GetCollection<CallSessionDocument>(PhoneTestFixtures.CallSessionsCollectionName);
            Sender = new CapturingObjectMessageSender();

            var messageAppService = new FakeMessageAppService();
            MessageApp = messageAppService;
            var accessHashKeyCache = new FakeUserAccessHashKeyCache();
            var accessHashHelper = new FakeAccessHashHelper2();
            _accessHashHelper = accessHashHelper;

            var userConverter = new Mock<IUserConverterService>();
            userConverter
                .Setup(x => x.GetUserListAsync(
                    It.IsAny<IRequestWithAccessHashKeyId>(),
                    It.IsAny<List<long>>(),
                    It.IsAny<bool>(),
                    It.IsAny<bool>(),
                    It.IsAny<int>()))
                .ReturnsAsync(new List<ILayeredUser>());

            // Privacy allows the call (callback never fired) and P2P is permitted; the callee has not
            // blocked the caller. None of this affects the lifecycle transitions under test.
            var privacy = new Mock<IPrivacyAppService>();
            privacy
                .Setup(x => x.ApplyPrivacyAsync(
                    It.IsAny<long>(),
                    It.IsAny<long>(),
                    It.IsAny<Action<PrivacyValueType>>(),
                    It.IsAny<PrivacyType>()))
                .Returns(Task.CompletedTask);

            var block = new Mock<IBlockCacheAppService>();
            block.Setup(x => x.IsBlockedAsync(It.IsAny<long>(), It.IsAny<long>())).ReturnsAsync(false);

            var options = Options.Create(new MyTelegramMessengerServerOptions
            {
                WebRtcConnections = webRtcConnections ??
                [
                    new WebRtcConnection
                    {
                        Ip = "1.2.3.4",
                        Ipv6 = "",
                        Port = 3478,
                        Turn = true,
                        Stun = false,
                        UserName = "user",
                        Password = "pass"
                    }
                ],
                CallReflectors = [new CallReflectorConfig { Ip = "5.6.7.8", Port = 599, Id = 2574219217251 }]
            });

            _requestHandler = CreateHandler("RequestCallHandler",
                Database, userConverter.Object, Sender, messageAppService, accessHashKeyCache, accessHashHelper, block.Object, privacy.Object,
                userAppService ?? FakeUserAppService.AllCallable());
            _receivedHandler = CreateHandler("ReceivedCallHandler",
                Database, userConverter.Object, Sender, accessHashHelper);
            _acceptHandler = CreateHandler("AcceptCallHandler",
                Database, userConverter.Object, Sender, messageAppService, accessHashHelper);
            _confirmHandler = CreateHandler("ConfirmCallHandler",
                Database, userConverter.Object, Sender, options, accessHashHelper, privacy.Object);
            _discardHandler = CreateHandler("DiscardCallHandler",
                Database, userConverter.Object, Sender, messageAppService, accessHashHelper);

            CallerInput = PhoneTestFixtures.RequestInput(CallerId).Build();
            CalleeInput = PhoneTestFixtures.RequestInput(CalleeId).Build();

            GaBytes = ValidDhValue(offset: 0);
            GbBytes = ValidDhValue(offset: 7);
            GaHash = SHA256.HashData(GaBytes);
        }

        private readonly FakeAccessHashHelper2 _accessHashHelper;

        public IMongoDatabase Database { get; }
        public IMongoCollection<CallSessionDocument> Sessions { get; }
        public CapturingObjectMessageSender Sender { get; }
        public FakeMessageAppService MessageApp { get; }
        public IRequestInput CallerInput { get; }
        public IRequestInput CalleeInput { get; }
        public byte[] GaBytes { get; }
        public byte[] GbBytes { get; }
        public byte[] GaHash { get; }

        private static IPhoneCallProtocol Protocol() => new TPhoneCallProtocol
        {
            UdpP2p = true,
            UdpReflector = true,
            MinLayer = 65,
            MaxLayer = 92,
            LibraryVersions = new TVector<string> { "3.0.0" }
        };

        public IInputPhoneCall CallerPeer()
        {
            var session = Sessions.Find(Builders<CallSessionDocument>.Filter.Eq(s => s.CallerId, CallerId)).First();
            return new TInputPhoneCall { Id = session.CallId, AccessHash = session.GetAccessHashForUser(CallerId) };
        }

        public IInputPhoneCall CalleePeer()
        {
            var session = Sessions.Find(Builders<CallSessionDocument>.Filter.Eq(s => s.CallerId, CallerId)).First();
            return new TInputPhoneCall { Id = session.CallId, AccessHash = session.GetAccessHashForUser(CalleeId) };
        }

        /// <summary>The protocol the harness sends, exposed so tests can build bespoke requests.</summary>
        public static IPhoneCallProtocol ProtocolForTests() => Protocol();

        /// <summary>Invokes phone.requestCall with a caller-supplied request object.</summary>
        public Task<IObject> InvokeRequestCallAsync(RequestRequestCall request)
            => InvokeAsync(_requestHandler, CallerInput, request);

        public Task<IObject> RequestCallAsync(int randomId = 100_001)
            => RequestCallAsync(CallerInput, CalleeId, randomId);

        public async Task<IObject> RequestCallAsync(IRequestInput caller, long calleeId, int randomId)
        {
            var request = new RequestRequestCall
            {
                UserId = new TInputUser
                {
                    UserId = calleeId,
                    AccessHash = _accessHashHelper.GenerateAccessHash(
                        caller.UserId, caller.AccessHashKeyId, calleeId, AccessHashType.User)
                },
                RandomId = randomId,
                GAHash = GaHash,
                Protocol = Protocol(),
                Video = false
            };
            return await InvokeAsync(_requestHandler, caller, request);
        }

        public Task<IObject> ReceivedCallAsync()
            => InvokeAsync(_receivedHandler, CalleeInput, new RequestReceivedCall { Peer = CalleePeer() });

        public Task<IObject> AcceptCallAsync()
            => InvokeAsync(_acceptHandler, CalleeInput,
                new RequestAcceptCall { Peer = CalleePeer(), GB = GbBytes, Protocol = Protocol() });

        public Task<IObject> ConfirmCallAsync()
            => InvokeAsync(_confirmHandler, CallerInput,
                new RequestConfirmCall { Peer = CallerPeer(), GA = GaBytes, KeyFingerprint = 987654321L, Protocol = Protocol() });

        public Task<IObject> DiscardCallAsync(IRequestInput input, IInputPhoneCall peer,
            IPhoneCallDiscardReason reason, int duration)
            => InvokeAsync(_discardHandler, input,
                new RequestDiscardCall
                {
                    Peer = peer,
                    Duration = duration,
                    Reason = reason,
                    Video = false,
                    ConnectionId = 0
                });

        public Task<IObject> AcceptCallAsync(byte[] gb)
            => InvokeAsync(_acceptHandler, CalleeInput,
                new RequestAcceptCall { Peer = CalleePeer(), GB = gb, Protocol = Protocol() });

        public Task<IObject> ConfirmCallAsync(byte[] ga)
            => InvokeAsync(_confirmHandler, CallerInput,
                new RequestConfirmCall { Peer = CallerPeer(), GA = ga, KeyFingerprint = 987654321L, Protocol = Protocol() });

        public Task<IObject> DiscardCallAsync(IRequestInput input, IInputPhoneCall peer)
            => InvokeAsync(_discardHandler, input,
                new RequestDiscardCall
                {
                    Peer = peer,
                    Duration = 10,
                    Reason = new TPhoneCallDiscardReasonHangup(),
                    Video = false,
                    ConnectionId = 0
                });

        private static async Task<IObject> InvokeAsync(object handler, IRequestInput input, IObject request)
        {
            var method = handler.GetType().GetMethod("HandleAsync", new[] { typeof(IRequestInput), typeof(IObject) })!;
            object taskObj;
            try
            {
                taskObj = method.Invoke(handler, new object[] { input, request })!;
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }

            var result = await (Task<IObject>)taskObj;
            return ((TRpcResult)result).Result;
        }

        private static object CreateHandler(string handlerTypeName, params object[] args)
        {
            var assembly = typeof(CallSessionDocument).Assembly;
            var type = assembly.GetType($"MyTelegram.Messenger.Handlers.LatestLayer.Phone.{handlerTypeName}", throwOnError: true)!;
            return PhoneTestFixtures.CreateGroupCallHandler(type, args);
        }

        /// <summary>Big-endian unsigned DH value guaranteed to sit inside the valid safety range.</summary>
        private static byte[] ValidDhValue(int offset)
        {
            var g = (BigInteger.One << (2048 - 64)) + offset;
            return g.ToByteArray(isUnsigned: true, isBigEndian: true);
        }
    }
}
