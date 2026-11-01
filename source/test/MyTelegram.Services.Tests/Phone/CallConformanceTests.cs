using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Moq;
using MyTelegram.Messenger;
using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Messenger.Services.Caching;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;
using MyTelegram.Services.Services;

namespace MyTelegram.Services.Tests.Phone;

/// <summary>
/// End-to-end <b>conformance</b> tests spanning the whole cross-handler flows. Where the per-handler
/// lifecycle tests (<see cref="CallLifecycleHandlerTests"/>, <see cref="GroupCallLifecycleHandlerTests"/>)
/// assert one step at a time (and clear the capturing sender between steps), these tests drive the real
/// handlers over a shared in-memory Mongo store <em>without</em> clearing the sender, then assert the
/// <b>exact ordered sequence</b> of emitted TL update constructors and their <b>recipients</b>
/// (target user / peer, plus the <c>excludeAuthKeyId</c> / <c>excludeUserId</c> exclusions) that official
/// clients rely on across the full flow.
///
/// <para>The 1:1 flow (<c>request → received → accept → confirm → discard</c>) verifies the precise
/// <c>updatePhoneCall{…}</c> constructor pushed to each party at each transition, including the
/// <c>phoneCallDiscarded</c> fanned out to the accepting callee's <em>other</em> devices with the
/// accepting device excluded via <c>excludeAuthKeyId</c> (Requirements 30.1, 30.2).</para>
///
/// <para>The group-call flow (<c>create → join → join → edit → leave → discard</c>) verifies that every
/// participant-set / state change is delivered to exactly the current subscribers minus the originator,
/// carrying <c>updateGroupCallParticipants</c> / <c>updateGroupCall</c> with a monotonically increasing
/// <c>version</c> (Requirements 30.1, 30.3).</para>
///
/// Covers Requirements 30.1 (delivery to all active sessions of every subscriber), 30.2 (single-device
/// acceptance via <c>phoneCallDiscarded</c> to the callee's other devices) and 30.3 (monotonic group-call
/// version on participant-set changes).
/// </summary>
public class CallConformanceTests
{
    // ---- 1:1 full flow --------------------------------------------------------------------------

    private const long CallerId = 1;
    private const long CalleeId = 2;

    [Fact]
    public async Task OneToOneCall_FullFlow_EmitsExactConstructorSequenceAndRecipients()
    {
        var harness = new CallHarness();
        var sender = harness.Sender;

        // request → received → accept → confirm → discard, capturing every push in order.
        await harness.RequestCallAsync();
        await harness.ReceivedCallAsync();
        await harness.AcceptCallAsync();
        await harness.ConfirmCallAsync();
        await harness.DiscardCallAsync(harness.CallerInput, harness.CallerPeer());

        // The full ordered push sequence emitted across the whole flow.
        var pushes = sender.Pushes;
        pushes.Count.ShouldBe(7,
            "expected exactly 7 pushed updates across request→received→accept(x2)→confirm→discard(x2), got: " +
            Describe(pushes));

        // 1. requestCall → callee learns of the incoming call: updatePhoneCall{ phoneCallRequested }.
        AssertPhoneCallPush(pushes[0], CalleeId, typeof(TPhoneCallRequested));
        pushes[0].ExcludeAuthKeyId.ShouldBeNull();
        pushes[0].ExcludeUserId.ShouldBeNull();

        // 2. receivedCall → caller learns the callee's device is ringing: updatePhoneCall{ phoneCallWaiting }.
        AssertPhoneCallPush(pushes[1], CallerId, typeof(TPhoneCallWaiting));
        pushes[1].ExcludeAuthKeyId.ShouldBeNull();

        // 3a. acceptCall → caller receives updatePhoneCall{ phoneCallAccepted } (carrying g_b).
        AssertPhoneCallPush(pushes[2], CallerId, typeof(TPhoneCallAccepted));
        pushes[2].ExcludeAuthKeyId.ShouldBeNull();

        // 3b. acceptCall → the accepting callee's OTHER devices receive updatePhoneCall{ phoneCallDiscarded },
        //     excluding the accepting device via excludeAuthKeyId (R30.2 - single-device acceptance).
        AssertPhoneCallPush(pushes[3], CalleeId, typeof(TPhoneCallDiscarded));
        pushes[3].ExcludeAuthKeyId.ShouldBe(harness.CalleeInput.AuthKeyId);

        // 4. confirmCall → callee receives updatePhoneCall{ phoneCall } with connections.
        AssertPhoneCallPush(pushes[4], CalleeId, typeof(MyTelegram.Schema.TPhoneCall));
        pushes[4].ExcludeAuthKeyId.ShouldBeNull();

        // 5a. discardCall (by caller) → the other party (callee) receives updatePhoneCall{ phoneCallDiscarded }.
        AssertPhoneCallPush(pushes[5], CalleeId, typeof(TPhoneCallDiscarded));
        pushes[5].ExcludeAuthKeyId.ShouldBeNull();

        // 5b. discardCall → the discarding user's OWN other devices also receive phoneCallDiscarded so
        //     they stop ringing, excluding the device that issued the request (it has the RPC result).
        AssertPhoneCallPush(pushes[6], CallerId, typeof(TPhoneCallDiscarded));
        pushes[6].ExcludeAuthKeyId.ShouldBe(harness.CallerInput.AuthKeyId);
    }

    // ---- group-call full flow -------------------------------------------------------------------

    private const long CreatorId = 1;
    private const long FirstJoinerId = 2;
    private const long SecondJoinerId = 3;
    private const long ChannelPeerId = 500;
    private const int CreatedGroupCallId = 900;
    private const int FirstJoinSsrc = 111_111;
    private const int SecondJoinSsrc = 222_222;

    [Fact]
    public async Task GroupCall_FullFlow_EmitsExactConstructorSequenceAndRecipients()
    {
        var harness = new GroupCallHarness();
        var sender = harness.Sender;

        // The call is attached to a channel peer, so every state-changing update fans out both to each
        // subscriber's user peer (reaching all their active sessions - R30.1) AND to the channel peer,
        // with the originator excluded via excludeUserId. The subscriber set is
        // participants + creator + invited.

        // create (creator, channel admin) → returns updateGroupCall, pushes nothing.
        var createUpdates = await harness.CreateAsync(CreatorId);
        SingleUpdate<TUpdateGroupCall>(createUpdates).Call.ShouldBeAssignableTo<MyTelegram.Schema.TGroupCall>();
        sender.Pushes.ShouldBeEmpty("create must not fan out any push");

        var call = harness.LoadCall();
        var createVersion = call.Version;
        var inputCall = new TInputGroupCall { Id = call.CallId, AccessHash = call.AccessHash };

        // join (first joiner) → participants {2}, subscribers {creator}. Fans out updateGroupCallParticipants
        // to the creator (user peer) then the channel peer (joiner excluded as originator).
        await harness.JoinAsync(FirstJoinerId, inputCall, FirstJoinSsrc);
        var afterFirstJoin = sender.Pushes.ToList();
        afterFirstJoin.Count.ShouldBe(2, "first join should push to the creator and the channel peer: " + Describe(afterFirstJoin));
        AssertUserPush(afterFirstJoin[0], CreatorId, typeof(TUpdateGroupCallParticipants));
        AssertChannelPush(afterFirstJoin[1], typeof(TUpdateGroupCallParticipants), excludeUserId: FirstJoinerId);
        var firstJoinVersion = harness.LoadCall().Version;
        firstJoinVersion.ShouldBeGreaterThan(createVersion, "join must increment the group-call version (R30.3)");

        // join (second joiner) → participants {2,3}, subscribers {first joiner, creator}. Fans out to user 2,
        // then creator 1, then the channel peer (second joiner excluded).
        sender.Clear();
        await harness.JoinAsync(SecondJoinerId, inputCall, SecondJoinSsrc);
        var afterSecondJoin = sender.Pushes.ToList();
        afterSecondJoin.Count.ShouldBe(3, "second join should push to first joiner, creator and channel peer: " + Describe(afterSecondJoin));
        AssertUserPush(afterSecondJoin[0], FirstJoinerId, typeof(TUpdateGroupCallParticipants));
        AssertUserPush(afterSecondJoin[1], CreatorId, typeof(TUpdateGroupCallParticipants));
        AssertChannelPush(afterSecondJoin[2], typeof(TUpdateGroupCallParticipants), excludeUserId: SecondJoinerId);
        var secondJoinVersion = harness.LoadCall().Version;
        secondJoinVersion.ShouldBeGreaterThan(firstJoinVersion, "join must increment the group-call version (R30.3)");

        // edit participant (second joiner mutes self) → updateGroupCallParticipants to the other subscribers
        // (first joiner + creator) and the channel peer, never to the originator.
        sender.Clear();
        await harness.EditSelfMutedAsync(SecondJoinerId, inputCall, muted: true);
        var afterEdit = sender.Pushes.ToList();
        afterEdit.Count.ShouldBe(3, "edit should push to first joiner, creator and channel peer: " + Describe(afterEdit));
        AssertUserPush(afterEdit[0], FirstJoinerId, typeof(TUpdateGroupCallParticipants));
        AssertUserPush(afterEdit[1], CreatorId, typeof(TUpdateGroupCallParticipants));
        AssertChannelPush(afterEdit[2], typeof(TUpdateGroupCallParticipants), excludeUserId: SecondJoinerId);
        sender.PushesToUser(SecondJoinerId).ShouldBeEmpty("the originating (editing) device must not receive its own update");
        var editVersion = harness.LoadCall().Version;
        editVersion.ShouldBe(secondJoinVersion, "an edit does not change the participant set, so the version stays (as on Telegram)");

        // leave (second joiner) → updateGroupCallParticipants (marking the participant left) to the remaining
        // subscribers (first joiner + creator) and the channel peer, excluding the leaver.
        sender.Clear();
        await harness.LeaveAsync(SecondJoinerId, inputCall, SecondJoinSsrc);
        var afterLeave = sender.Pushes.ToList();
        afterLeave.Count.ShouldBe(3, "leave should push to first joiner, creator and channel peer: " + Describe(afterLeave));
        AssertUserPush(afterLeave[0], FirstJoinerId, typeof(TUpdateGroupCallParticipants));
        AssertUserPush(afterLeave[1], CreatorId, typeof(TUpdateGroupCallParticipants));
        AssertChannelPush(afterLeave[2], typeof(TUpdateGroupCallParticipants), excludeUserId: SecondJoinerId);
        afterLeave
            .SelectMany(p => p.Updates.OfType<TUpdateGroupCallParticipants>())
            .SelectMany(u => u.Participants.OfType<TGroupCallParticipant>())
            .ShouldAllBe(participant => participant.Left, "leave must mark the participant as left");
        var leaveVersion = harness.LoadCall().Version;
        leaveVersion.ShouldBeGreaterThan(editVersion, "leave must increment the group-call version (R30.3)");

        // discard (creator) → the terminating update (updateGroupCall{ groupCallDiscarded }) reaches the
        // remaining subscriber (first joiner) and the channel peer, never the discarding creator.
        sender.Clear();
        var discardUpdates = await harness.DiscardAsync(CreatorId, inputCall);
        SingleUpdate<TUpdateGroupCall>(discardUpdates).Call.ShouldBeOfType<TGroupCallDiscarded>();
        var afterDiscard = sender.Pushes.ToList();
        afterDiscard.Count.ShouldBe(2, "discard should push to the first joiner and the channel peer: " + Describe(afterDiscard));
        AssertUserPush(afterDiscard[0], FirstJoinerId, typeof(TUpdateGroupCall));
        AssertChannelPush(afterDiscard[1], typeof(TUpdateGroupCall), excludeUserId: CreatorId);
        sender.PushesToUser(CreatorId).ShouldBeEmpty("the discarding creator must not receive its own terminating update");
        harness.LoadCall().Active.ShouldBeFalse();
        harness.LoadCall().Version.ShouldBeGreaterThan(leaveVersion, "discard must increment the group-call version (R30.3)");
    }

    // ---- shared assertion helpers ---------------------------------------------------------------

    /// <summary>Asserts a push targets <paramref name="userId"/> and carries a single
    /// <c>updatePhoneCall</c> whose inner phone-call constructor is <paramref name="phoneCallType"/>.</summary>
    private static void AssertPhoneCallPush(CapturedPush push, long userId, Type phoneCallType)
    {
        push.TargetUserId.ShouldBe(userId);
        var update = push.Updates.OfType<TUpdatePhoneCall>().ShouldHaveSingleItem();
        update.PhoneCall.ShouldBeOfType(phoneCallType);
    }

    /// <summary>Asserts a push targets user <paramref name="userId"/>'s peer (reaching all its active
    /// sessions per R30.1) and carries an update of the given type, with no exclusions.</summary>
    private static void AssertUserPush(CapturedPush push, long userId, Type updateType)
    {
        push.Peer.PeerType.ShouldBe(PeerType.User);
        push.TargetUserId.ShouldBe(userId);
        push.ExcludeUserId.ShouldBeNull();
        push.Updates.ShouldContain(u => updateType.IsInstanceOfType(u),
            $"expected push to user {userId} to carry {updateType.Name}, got: [{string.Join(",", push.UpdateConstructorNames)}]");
    }

    /// <summary>Asserts a push targets the channel peer, carries an update of the given type, and excludes
    /// the originating user via <c>excludeUserId</c> (so the originator's session on that peer is skipped).</summary>
    private static void AssertChannelPush(CapturedPush push, Type updateType, long excludeUserId)
    {
        push.Peer.PeerType.ShouldBe(PeerType.Channel);
        push.Peer.PeerId.ShouldBe(ChannelPeerId);
        push.ExcludeUserId.ShouldBe(excludeUserId);
        push.Updates.ShouldContain(u => updateType.IsInstanceOfType(u),
            $"expected channel push to carry {updateType.Name}, got: [{string.Join(",", push.UpdateConstructorNames)}]");
    }

    private static TUpdate SingleUpdate<TUpdate>(IObject updates) where TUpdate : IUpdate
    {
        return updates.ShouldBeOfType<TUpdates>().Updates.OfType<TUpdate>().ShouldHaveSingleItem();
    }

    private static string Describe(IReadOnlyList<CapturedPush> pushes)
        => string.Join(" | ", pushes.Select(p =>
            $"user={p.TargetUserId ?? -1}:[{string.Join(",", p.UpdateConstructorNames)}]" +
            (p.ExcludeAuthKeyId.HasValue ? $" exAuth={p.ExcludeAuthKeyId}" : "") +
            (p.ExcludeUserId.HasValue ? $" exUser={p.ExcludeUserId}" : "")));

    private static object CreateHandler(string handlerTypeName, params object[] args)
    {
        var assembly = typeof(CallSessionDocument).Assembly;
        var type = assembly.GetType($"MyTelegram.Messenger.Handlers.LatestLayer.Phone.{handlerTypeName}", throwOnError: true)!;
        return PhoneTestFixtures.CreateGroupCallHandler(type, args);
    }

    // ---- 1:1 harness ----------------------------------------------------------------------------

    /// <summary>
    /// Builds the real 1:1 call handlers over a shared in-memory Mongo store and drives them through the
    /// lifecycle. Unlike <see cref="CallLifecycleHandlerTests"/> the capturing sender is never cleared, so
    /// <see cref="CapturingObjectMessageSender.Pushes"/> holds the whole ordered flow.
    /// </summary>
    private sealed class CallHarness
    {
        private readonly object _requestHandler;
        private readonly object _receivedHandler;
        private readonly object _acceptHandler;
        private readonly object _confirmHandler;
        private readonly object _discardHandler;

        public CallHarness()
        {
            Database = PhoneTestFixtures.CreateDatabase(out _);
            Sessions = Database.GetCollection<CallSessionDocument>(PhoneTestFixtures.CallSessionsCollectionName);
            Sender = new CapturingObjectMessageSender();

            var messageAppService = new FakeMessageAppService();
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

            _requestHandler = CreateHandler("RequestCallHandler",
                Database, userConverter.Object, Sender, messageAppService, accessHashKeyCache, accessHashHelper, block.Object, privacy.Object, FakeUserAppService.AllCallable());
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

        public Task<IObject> RequestCallAsync()
        {
            var request = new RequestRequestCall
            {
                UserId = new TInputUser
                {
                    UserId = CalleeId,
                    AccessHash = _accessHashHelper.GenerateAccessHash(
                        CallerInput.UserId, CallerInput.AccessHashKeyId, CalleeId, AccessHashType.User)
                },
                RandomId = 100_001,
                GAHash = GaHash,
                Protocol = Protocol(),
                Video = false
            };
            return InvokeAsync(_requestHandler, CallerInput, request);
        }

        public Task<IObject> ReceivedCallAsync()
            => InvokeAsync(_receivedHandler, CalleeInput, new RequestReceivedCall { Peer = CalleePeer() });

        public Task<IObject> AcceptCallAsync()
            => InvokeAsync(_acceptHandler, CalleeInput,
                new RequestAcceptCall { Peer = CalleePeer(), GB = GbBytes, Protocol = Protocol() });

        public Task<IObject> ConfirmCallAsync()
            => InvokeAsync(_confirmHandler, CallerInput,
                new RequestConfirmCall { Peer = CallerPeer(), GA = GaBytes, KeyFingerprint = 987654321L, Protocol = Protocol() });

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

        /// <summary>Big-endian unsigned DH value guaranteed to sit inside the valid safety range.</summary>
        private static byte[] ValidDhValue(int offset)
        {
            var g = (BigInteger.One << (2048 - 64)) + offset;
            return g.ToByteArray(isUnsigned: true, isBigEndian: true);
        }
    }

    // ---- group-call harness ---------------------------------------------------------------------

    /// <summary>
    /// Builds the real group-call handlers over a shared in-memory Mongo store. The peer is a user peer
    /// (the creator) so fan-out is purely to the participant/creator/invited user set (no channel peer).
    /// </summary>
    private sealed class GroupCallHarness
    {
        private readonly IMongoDatabase _database;
        private readonly InMemoryMongoStore _store;
        private readonly object _createHandler;
        private readonly object _joinHandler;
        private readonly object _editHandler;
        private readonly object _leaveHandler;
        private readonly object _discardHandler;

        public GroupCallHarness()
        {
            _database = PhoneTestFixtures.CreateDatabase(out _store);
            Sender = new CapturingObjectMessageSender();

            var idGenerator = new Mock<IIdGenerator>();
            idGenerator
                .Setup(x => x.NextIdAsync(It.IsAny<IdType>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreatedGroupCallId);

            var channelAdminRightsChecker = new Mock<IChannelAdminRightsChecker>();

            var options = new Mock<IOptionsMonitor<MyTelegramMessengerServerOptions>>();
            options.Setup(x => x.CurrentValue).Returns(new MyTelegramMessengerServerOptions());

            var channelAppService = new Mock<IChannelAppService>();

            _createHandler = CreateHandler("CreateGroupCallHandler",
                idGenerator.Object, _database, new PeerHelper(), new FakeMessageAppService(), options.Object, channelAdminRightsChecker.Object);
            _joinHandler = CreateHandler("JoinGroupCallHandler",
                _database, new PeerHelper(), Sender, options.Object, channelAppService.Object);
            _editHandler = CreateHandler("EditGroupCallParticipantHandler",
                _database, new PeerHelper(), Sender);
            _leaveHandler = CreateHandler("LeaveGroupCallHandler",
                _database, new PeerHelper(), Sender);
            _discardHandler = CreateHandler("DiscardGroupCallHandler",
                _database, new PeerHelper(), Sender, new FakeMessageAppService(), channelAdminRightsChecker.Object);
        }

        public CapturingObjectMessageSender Sender { get; }

        public GroupCallDocument LoadCall()
        {
            var doc = _store.Documents(PhoneTestFixtures.GroupCallsCollectionName).Single();
            return BsonSerializer.Deserialize<GroupCallDocument>(doc);
        }

        public Task<IUpdates> CreateAsync(long creatorId)
            => InvokeAsync(_createHandler, creatorId, new RequestCreateGroupCall
            {
                Peer = new TInputPeerChannel { ChannelId = ChannelPeerId, AccessHash = 0 },
                RandomId = 12345,
                Title = "Conformance sync"
            });

        public Task<IUpdates> JoinAsync(long userId, IInputGroupCall call, int ssrc)
            => InvokeAsync(_joinHandler, userId, new RequestJoinGroupCall
            {
                Call = call,
                JoinAs = new TInputPeerSelf(),
                Params = new TDataJSON { Data = $"{{\"ssrc\":{ssrc}}}" }
            });

        public Task<IUpdates> EditSelfMutedAsync(long userId, IInputGroupCall call, bool muted)
            => InvokeAsync(_editHandler, userId, new RequestEditGroupCallParticipant
            {
                Call = call,
                Participant = new TInputPeerSelf(),
                Muted = muted
            });

        public Task<IUpdates> LeaveAsync(long userId, IInputGroupCall call, int ssrc)
            => InvokeAsync(_leaveHandler, userId, new RequestLeaveGroupCall
            {
                Call = call,
                Source = ssrc
            });

        public Task<IUpdates> DiscardAsync(long userId, IInputGroupCall call)
            => InvokeAsync(_discardHandler, userId, new RequestDiscardGroupCall
            {
                Call = call
            });

        private static async Task<IUpdates> InvokeAsync(object handler, long userId, IObject request)
        {
            var method = handler.GetType().GetMethod("HandleAsync", new[] { typeof(IRequestInput), typeof(IObject) })!;
            var input = PhoneTestFixtures.RequestInput(userId).Build();
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
            return (IUpdates)((TRpcResult)result).Result;
        }
    }
}
