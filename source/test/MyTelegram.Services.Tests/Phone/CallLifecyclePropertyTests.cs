using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using CsCheck;
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
/// Property-based tests for the 1:1 call state machine.
///
/// <para><b>Property 1: State-machine monotonicity (1:1)</b> — a <see cref="CallSessionDocument.State"/>
/// only advances along <c>requested → {received} → accepted → confirmed → discarded</c>; it never moves
/// backward, and <c>discarded</c> is terminal.</para>
///
/// The property drives the real 1:1 handlers (<c>RequestCallHandler</c>, <c>ReceivedCallHandler</c>,
/// <c>AcceptCallHandler</c>, <c>ConfirmCallHandler</c>, <c>DiscardCallHandler</c>) over an in-memory Mongo
/// store, applying randomly-generated sequences of lifecycle operations. After every operation it reads the
/// persisted state and asserts the state rank never decreases and that <c>discarded</c> is absorbing.
///
/// <b>Validates: Requirements 4.1, 5.1, 6.1, 7.1</b>
/// </summary>
public class CallLifecyclePropertyTests
{
    private const long CallerId = 1;
    private const long CalleeId = 2;

    // Lifecycle operations the property may apply in any order.
    private enum Op
    {
        Received = 0,
        Accept = 1,
        Confirm = 2,
        Discard = 3
    }

    // Monotonic rank of every reachable state. A valid transition may only keep or increase the rank.
    private static int Rank(string state) => state switch
    {
        "requested" => 0,
        "received" => 1,
        "accepted" => 2,
        "confirmed" => 3,
        "discarded" => 4,
        _ => throw new InvalidOperationException($"Unexpected call state '{state}'.")
    };

    [Fact]
    public void State_OnlyAdvances_AndDiscardedIsTerminal()
    {
        // Generate a sequence of 1..14 lifecycle operations, each one of the four transitions.
        Gen.Int[0, 3]
            .Select(i => (Op)i)
            .Array[1, 14]
            .Sample(ops => RunScenario(ops));
    }

    // A deterministic example that walks the full forward path and then proves discarded is absorbing.
    [Fact]
    public void FullForwardPath_ReachesConfirmedThenDiscarded_AndStaysDiscarded()
    {
        RunScenario(new[] { Op.Received, Op.Accept, Op.Confirm, Op.Discard, Op.Accept, Op.Confirm, Op.Received });
    }

    private static void RunScenario(IReadOnlyList<Op> ops)
    {
        RunScenarioAsync(ops).GetAwaiter().GetResult();
    }

    private static async Task RunScenarioAsync(IReadOnlyList<Op> ops)
    {
        var database = PhoneTestFixtures.CreateDatabase(out _);
        var sender = new CapturingObjectMessageSender();
        var messageAppService = new FakeMessageAppService();
        var accessHashKeyCache = new FakeUserAccessHashKeyCache();
        var accessHashHelper = new FakeAccessHashHelper2();

        var userConverter = new Mock<IUserConverterService>();
        userConverter
            .Setup(x => x.GetUserListAsync(
                It.IsAny<IRequestWithAccessHashKeyId>(),
                It.IsAny<List<long>>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<int>()))
            .ReturnsAsync(new List<ILayeredUser>());

        // Privacy allows the call (callback never invoked) and P2P is allowed; the callee has not blocked
        // the caller. None of this affects the state machine, but the handlers depend on these services.
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
            WebRtcConnections =
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
            ]
        });

        var requestHandler = CreateHandler("RequestCallHandler",
            database, userConverter.Object, sender, messageAppService, accessHashKeyCache, accessHashHelper, block.Object, privacy.Object, FakeUserAppService.AllCallable());
        var receivedHandler = CreateHandler("ReceivedCallHandler",
            database, userConverter.Object, sender, accessHashHelper);
        var acceptHandler = CreateHandler("AcceptCallHandler",
            database, userConverter.Object, sender, messageAppService, accessHashHelper);
        var confirmHandler = CreateHandler("ConfirmCallHandler",
            database, userConverter.Object, sender, options, accessHashHelper, privacy.Object);
        var discardHandler = CreateHandler("DiscardCallHandler",
            database, userConverter.Object, sender, messageAppService, accessHashHelper);

        var callerInput = PhoneTestFixtures.RequestInput(CallerId).Build();
        var calleeInput = PhoneTestFixtures.RequestInput(CalleeId).Build();

        // Valid DH values: g in [2^(2048-64), p - 2^(2048-64)] and 1 < g < p-1. g_a_hash = SHA256(g_a).
        var gaBytes = ValidDhValue(offset: 0);
        var gbBytes = ValidDhValue(offset: 7);
        var gaHash = SHA256.HashData(gaBytes);

        // Both peers advertise the same tgcalls library version so negotiation never rejects the call.
        static IPhoneCallProtocol Protocol() => new TPhoneCallProtocol
        {
            UdpP2p = true,
            UdpReflector = true,
            MinLayer = 65,
            MaxLayer = 92,
            LibraryVersions = new TVector<string> { "3.0.0" }
        };

        // Establish the call (state = requested).
        var requestCall = new RequestRequestCall
        {
            UserId = new TInputUser
            {
                UserId = CalleeId,
                AccessHash = accessHashHelper.GenerateAccessHash(
                    callerInput.UserId, callerInput.AccessHashKeyId, CalleeId, AccessHashType.User)
            },
            RandomId = Random.Shared.Next(1, int.MaxValue),
            GAHash = gaHash,
            Protocol = Protocol(),
            Video = false
        };
        await InvokeAsync(requestHandler, callerInput, requestCall);

        var collection = database.GetCollection<CallSessionDocument>(PhoneTestFixtures.CallSessionsCollectionName);
        var session = await collection.Find(Builders<CallSessionDocument>.Filter.Empty).FirstOrDefaultAsync();
        session.ShouldNotBeNull();

        var callId = session!.CallId;
        var callerAccessHash = session.GetAccessHashForUser(CallerId);
        var calleeAccessHash = session.GetAccessHashForUser(CalleeId);

        IInputPhoneCall CallerPeer() => new TInputPhoneCall { Id = callId, AccessHash = callerAccessHash };
        IInputPhoneCall CalleePeer() => new TInputPhoneCall { Id = callId, AccessHash = calleeAccessHash };

        var previousRank = Rank(await ReadStateAsync(collection, callId));
        previousRank.ShouldBe(Rank("requested"));

        foreach (var op in ops)
        {
            switch (op)
            {
                case Op.Received:
                    await TryInvokeAsync(receivedHandler, calleeInput,
                        new RequestReceivedCall { Peer = CalleePeer() });
                    break;
                case Op.Accept:
                    await TryInvokeAsync(acceptHandler, calleeInput,
                        new RequestAcceptCall { Peer = CalleePeer(), GB = gbBytes, Protocol = Protocol() });
                    break;
                case Op.Confirm:
                    await TryInvokeAsync(confirmHandler, callerInput,
                        new RequestConfirmCall { Peer = CallerPeer(), GA = gaBytes, KeyFingerprint = 987654321L, Protocol = Protocol() });
                    break;
                case Op.Discard:
                    await TryInvokeAsync(discardHandler, callerInput,
                        new RequestDiscardCall
                        {
                            Peer = CallerPeer(),
                            Duration = 10,
                            Reason = new TPhoneCallDiscardReasonHangup(),
                            Video = false,
                            ConnectionId = 0
                        });
                    break;
            }

            var currentRank = Rank(await ReadStateAsync(collection, callId));

            // Property 1: the state never moves backward.
            currentRank.ShouldBeGreaterThanOrEqualTo(previousRank,
                $"state moved backward after {op}: rank {previousRank} -> {currentRank}");

            // Property 1: discarded is terminal - once discarded, it stays discarded.
            if (previousRank == Rank("discarded"))
            {
                currentRank.ShouldBe(Rank("discarded"), "discarded state must be terminal");
            }

            previousRank = currentRank;
        }
    }

    private static async Task<string> ReadStateAsync(IMongoCollection<CallSessionDocument> collection, long callId)
    {
        var session = await collection
            .Find(Builders<CallSessionDocument>.Filter.Eq(s => s.CallId, callId))
            .FirstOrDefaultAsync();
        session.ShouldNotBeNull();
        return session!.State;
    }

    /// <summary>Builds a big-endian unsigned DH value guaranteed to sit inside the valid safety range.</summary>
    private static byte[] ValidDhValue(int offset)
    {
        var g = (BigInteger.One << (2048 - 64)) + offset;
        return g.ToByteArray(isUnsigned: true, isBigEndian: true);
    }

    /// <summary>Invokes a handler and swallows the expected invalid-transition RPC errors.</summary>
    private static async Task TryInvokeAsync(object handler, IRequestInput input, IObject request)
    {
        try
        {
            await InvokeAsync(handler, input, request);
        }
        catch (MyTelegram.RpcException)
        {
            // An invalid transition is rejected and leaves the stored state unchanged - that is exactly
            // what the monotonicity property expects, so the rejection is not a failure here.
        }
    }

    private static async Task InvokeAsync(object handler, IRequestInput input, IObject request)
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

        await (Task<IObject>)taskObj;
    }

    private static object CreateHandler(string handlerTypeName, params object[] args)
    {
        var assembly = typeof(CallSessionDocument).Assembly;
        var type = assembly.GetType($"MyTelegram.Messenger.Handlers.LatestLayer.Phone.{handlerTypeName}", throwOnError: true)!;
        return PhoneTestFixtures.CreateGroupCallHandler(type, args);
    }
}
