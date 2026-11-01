using System.Reflection;
using CsCheck;
using MongoDB.Driver;
using Moq;
using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;
using MyTelegram.Services.Services;

namespace MyTelegram.Services.Tests.Phone;

/// <summary>
/// Property-based test for design Property 3 (Single-acceptance).
///
/// Property 3: When a Call_Session is accepted on one of the Callee's devices, the Update_Dispatcher
/// pushes an <c>updatePhoneCall{ phoneCallDiscarded }</c> to the Callee peer while excluding the
/// accepting device (via <c>excludeAuthKeyId</c>). Delivery therefore reaches every OTHER active
/// session of the Callee, so the accepting device is the only one that proceeds with the call - the
/// call can never be accepted twice.
///
/// The property drives the real <c>AcceptCallHandler</c> over an in-memory Mongo store across randomly
/// generated accepting-device identities, pre-accept states, video flags, and negotiated library
/// versions. For every scenario it asserts:
///   * exactly one push carrying <c>phoneCallDiscarded</c> is addressed to the Callee peer;
///   * that push excludes precisely the accepting device's auth key id (so its other devices receive it);
///   * the discarded call echoes the session id + video flag;
///   * the accepting device is NOT itself pushed the discard (it receives the RPC phoneCallWaiting reply);
///   * the Caller peer receives <c>phoneCallAccepted</c> and never a spurious <c>phoneCallDiscarded</c>.
///
/// **Validates: Requirements 4.3, 30.2**
/// </summary>
public class SingleAcceptancePropertyTests
{
    private const long CallId = 77;
    private const long CallerId = 1;
    private const long CalleeId = 2;
    private const long CallerAccessHash = 111;
    private const long CalleeAccessHash = 222;
    private const string CommonLibraryVersion = "3.0.0";

    [Fact]
    public void Accept_PushesDiscardedToOtherCalleeDevices_ExcludingTheAcceptingDevice()
    {
        // A callee can accept from any device (distinct auth key id) while the session is in a
        // pre-accept state ("waiting" or "received"); the video flag and negotiated library version
        // are irrelevant to the single-acceptance guarantee and are varied to widen coverage.
        var gen =
            from initialState in Gen.OneOfConst("waiting", "received")
            from video in Gen.Bool
            from acceptingAuthKeyId in Gen.Long[1, long.MaxValue]
            from libraryVersion in Gen.OneOfConst(CommonLibraryVersion, "4.1.2", "5.0.0")
            select new AcceptScenario(initialState, video, acceptingAuthKeyId, libraryVersion);

        gen.Sample(scenario => RunScenario(scenario), iter: 200);
    }

    private static void RunScenario(AcceptScenario scenario)
    {
        var database = PhoneTestFixtures.CreateDatabase(out _);
        var collection = database.GetCollection<CallSessionDocument>(PhoneTestFixtures.CallSessionsCollectionName);
        collection.InsertOne(new CallSessionDocument
        {
            Id = CallId,
            CallId = CallId,
            CallerId = CallerId,
            CalleeId = CalleeId,
            CallerAccessHash = CallerAccessHash,
            CalleeAccessHash = CalleeAccessHash,
            State = scenario.InitialState,
            Video = scenario.Video,
            // The accept handler rejects the call unless the caller and callee agree on a common tgcalls
            // version, so seed the caller side with the same version the accepting device advertises.
            CallerLibraryVersions = [scenario.LibraryVersion]
        });

        var sender = new CapturingObjectMessageSender();
        var messageApp = new FakeMessageAppService();
        var userConverter = CreateUserConverter();
        var accessHash = new FakeAccessHashHelper2();

        // The accepting device: a callee session whose temp auth key id is the value under test.
        var acceptingInput = PhoneTestFixtures.RequestInput(CalleeId)
            .WithSession(sessionId: CalleeId * 1000 + 1, authKeyId: scenario.AcceptingAuthKeyId)
            .Build();

        var request = new RequestAcceptCall
        {
            Peer = new TInputPhoneCall { Id = CallId, AccessHash = CalleeAccessHash },
            GB = ValidGb(),
            Protocol = Protocol(scenario.LibraryVersion)
        };

        InvokeAcceptAsync(database, userConverter, sender, messageApp, accessHash, acceptingInput, request)
            .GetAwaiter().GetResult();

        // ---- R4.3 / R30.2: the callee's OTHER devices are told to discard --------------------------

        var calleeDiscards = sender.PushesToUser(CalleeId)
            .Where(p => p.Carries<TUpdatePhoneCall>() && CarriesDiscarded(p))
            .ToList();

        // Exactly one phoneCallDiscarded fan-out is addressed to the callee peer.
        calleeDiscards.Count.ShouldBe(1,
            "accepting must push exactly one phoneCallDiscarded to the callee's other devices");

        var discardPush = calleeDiscards[0];

        // The push excludes precisely the accepting device so that its OTHER sessions receive the
        // discard - guaranteeing at most one device proceeds with the call.
        discardPush.ExcludeAuthKeyId.ShouldBe(scenario.AcceptingAuthKeyId,
            "the phoneCallDiscarded fan-out must exclude the accepting device");

        // The discarded call echoes the session identity and its video flag.
        var discarded = discardPush.Updates.OfType<TUpdatePhoneCall>()
            .Select(u => u.PhoneCall).OfType<TPhoneCallDiscarded>().Single();
        discarded.Id.ShouldBe(CallId);
        discarded.Video.ShouldBe(scenario.Video);

        // The accepting device is never itself the target of a device-specific discard: because the
        // fan-out is a peer push that excludes its auth key id, its own session is not asked to discard.
        // (It instead receives the phoneCallWaiting RPC reply.)
        sender.Pushes
            .Where(p => p.ExcludeAuthKeyId == null && p.TargetUserId == CalleeId && CarriesDiscarded(p))
            .ShouldBeEmpty("the accepting device must not be pushed a phoneCallDiscarded");

        // ---- The caller learns the call was accepted, not discarded --------------------------------

        var callerPushes = sender.PushesToUser(CallerId).ToList();
        callerPushes.ShouldNotBeEmpty("the caller must be notified the call was accepted");
        callerPushes.Any(p => p.Updates.OfType<TUpdatePhoneCall>()
                .Any(u => u.PhoneCall is TPhoneCallAccepted))
            .ShouldBeTrue("the caller must receive phoneCallAccepted");
        callerPushes.Any(CarriesDiscarded)
            .ShouldBeFalse("the caller must not receive a spurious phoneCallDiscarded on accept");
    }

    private static bool CarriesDiscarded(CapturedPush push) =>
        push.Updates.OfType<TUpdatePhoneCall>().Any(u => u.PhoneCall is TPhoneCallDiscarded);

    private static IUserConverterService CreateUserConverter()
    {
        var mock = new Mock<IUserConverterService>();
        mock.Setup(x => x.GetUserListAsync(
                It.IsAny<IRequestWithAccessHashKeyId>(),
                It.IsAny<List<long>>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<int>()))
            .ReturnsAsync(new List<ILayeredUser>());
        return mock.Object;
    }

    private static IPhoneCallProtocol Protocol(string libraryVersion) => new TPhoneCallProtocol
    {
        UdpP2p = true,
        UdpReflector = true,
        MinLayer = 65,
        MaxLayer = 92,
        LibraryVersions = new TVector<string> { libraryVersion }
    };

    /// <summary>A g_b value inside the valid DH safety range so the accept handshake is not rejected.</summary>
    private static byte[] ValidGb()
    {
        var g = (System.Numerics.BigInteger.One << (2048 - 64)) + 5;
        return g.ToByteArray(isUnsigned: true, isBigEndian: true);
    }

    private static async Task InvokeAcceptAsync(
        IMongoDatabase database,
        IUserConverterService userConverter,
        IObjectMessageSender sender,
        object messageApp,
        object accessHash,
        IRequestInput input,
        RequestAcceptCall request)
    {
        var assembly = typeof(CallSessionDocument).Assembly;
        var type = assembly.GetType("MyTelegram.Messenger.Handlers.LatestLayer.Phone.AcceptCallHandler", throwOnError: true)!;
        var handler = PhoneTestFixtures.CreateGroupCallHandler(type, database, userConverter, sender, messageApp, accessHash);
        var method = type.GetMethod("HandleAsync", new[] { typeof(IRequestInput), typeof(IObject) })!;

        object taskObj;
        try
        {
            taskObj = method.Invoke(handler, new object[] { input, request })!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }

        await (Task<IObject>)taskObj;
    }

    private sealed record AcceptScenario(
        string InitialState,
        bool Video,
        long AcceptingAuthKeyId,
        string LibraryVersion);
}
