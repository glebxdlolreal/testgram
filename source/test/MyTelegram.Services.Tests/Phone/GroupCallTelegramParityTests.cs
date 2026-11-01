using System.Reflection;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Moq;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;
using MyTelegram.Services.Services;

namespace MyTelegram.Services.Tests.Phone;

/// <summary>
/// Group-call behaviour pinned to what Telegram's production servers do (captured with two real accounts on
/// 2026-11-01): per-session access hashes, join_muted and admin-mute rules, who may record / retitle / start /
/// invite, the invitation service message, and leave.
/// </summary>
public class GroupCallTelegramParityTests
{
    private const long AdminUserId = 1;
    private const long MemberUserId = 2;
    private const long OtherMemberUserId = 3;
    private const long BotUserId = 600_000_000_001;
    private const long ChannelId = 1_073_741_900;
    private const long CallId = 800;
    private const long StoredAccessHash = 4242;

    // ---- access hash -----------------------------------------------------------------------------

    [Fact]
    public async Task PerSessionAccessHash_FromGetGroupCall_IsAccepted()
    {
        var harness = Harness.Create();
        var hash = SessionHash(MemberUserId);

        var result = await harness.InvokeAsync("GetGroupCallHandler", MemberUserId,
            new RequestGetGroupCall { Call = new TInputGroupCall { Id = CallId, AccessHash = hash }, Limit = 10 });

        result.ShouldBeOfType<MyTelegram.Schema.Phone.TGroupCall>().Call.ShouldBeOfType<MyTelegram.Schema.TGroupCall>()
            .AccessHash.ShouldBe(hash);
    }

    [Fact]
    public async Task StoredAccessHash_FromChatFull_IsStillAccepted()
    {
        var harness = Harness.Create();

        var result = await harness.InvokeAsync("GetGroupCallHandler", MemberUserId,
            new RequestGetGroupCall { Call = new TInputGroupCall { Id = CallId, AccessHash = StoredAccessHash }, Limit = 10 });

        result.ShouldBeOfType<MyTelegram.Schema.Phone.TGroupCall>();
    }

    [Fact]
    public async Task AnotherUsersAccessHash_IsRejectedAsGroupCallInvalid()
    {
        var harness = Harness.Create();

        var ex = await Should.ThrowAsync<RpcException>(() => harness.InvokeAsync("GetGroupCallHandler", MemberUserId,
            new RequestGetGroupCall { Call = new TInputGroupCall { Id = CallId, AccessHash = SessionHash(AdminUserId) }, Limit = 10 }));

        ex.Message.ShouldBe("GROUPCALL_INVALID");
    }

    // ---- join_muted and admin mute -----------------------------------------------------------------

    [Fact]
    public async Task JoinMutedCall_MemberJoinsMutedAndCannotUnmuteThemselves()
    {
        var harness = Harness.Create(joinMuted: true);

        var join = await harness.JoinAsync(MemberUserId, muted: false);
        var joined = SingleParticipant(join);
        joined.Muted.ShouldBeTrue();
        joined.CanSelfUnmute.ShouldBeFalse();
        joined.JustJoined.ShouldBeTrue();
        joined.Versioned.ShouldBeTrue();

        // Telegram does not refuse the self-unmute; it just leaves the participant muted.
        var selfUnmute = await harness.EditAsync(MemberUserId, new TInputPeerSelf(), muted: false);
        SingleParticipant(selfUnmute).Muted.ShouldBeTrue();
        harness.Participant(MemberUserId).Muted.ShouldBeTrue();
    }

    [Fact]
    public async Task JoinMutedCall_AdminJoinsAndMayUnmute()
    {
        var harness = Harness.Create(joinMuted: true);

        var joined = SingleParticipant(await harness.JoinAsync(AdminUserId, muted: true));
        joined.Muted.ShouldBeTrue();
        joined.CanSelfUnmute.ShouldBeTrue();

        SingleParticipant(await harness.EditAsync(AdminUserId, new TInputPeerSelf(), muted: false)).Muted.ShouldBeFalse();
    }

    [Fact]
    public async Task ChannelVideoChat_EveryoneJoinsMuted_OnlyAdminsMayUnmute_AndJoinMutedIsFixed()
    {
        // Telegram 2026-11-01, a broadcast channel's (non-RTMP) video chat.
        var harness = Harness.Create(broadcast: true);

        var adminJoin = await harness.JoinAsync(AdminUserId, muted: false);
        var admin = SingleParticipant(adminJoin);
        admin.Muted.ShouldBeTrue();
        admin.CanSelfUnmute.ShouldBeTrue();

        var member = SingleParticipant(await harness.JoinAsync(MemberUserId, muted: false));
        member.Muted.ShouldBeTrue();
        member.CanSelfUnmute.ShouldBeFalse();

        var call = adminJoin.ShouldBeOfType<TUpdates>().Updates.OfType<TUpdateGroupCall>().Single().Call.ShouldBeOfType<MyTelegram.Schema.TGroupCall>();
        call.JoinMuted.ShouldBeTrue();
        call.CanChangeJoinMuted.ShouldBeFalse();
        call.StreamDcId.ShouldBeNull();
        call.ListenersHidden.ShouldBeFalse();
    }

    [Fact]
    public async Task RtmpStream_ListenersAreHiddenAndUnversioned_AndOnlyManagersGetTheStreamKey()
    {
        // Telegram 2026-11-01, an RTMP stream in a broadcast channel.
        var harness = Harness.Create(broadcast: true, rtmp: true);

        var adminJoin = (TUpdates)await harness.JoinAsync(AdminUserId, muted: false);
        var adminParams = adminJoin.Updates.OfType<TUpdateGroupCallConnection>().Single().Params.Data;
        adminParams.ShouldContain("\"stream\":true");
        adminParams.ShouldContain("\"rtmp_stream_url\":\"rtmp://stream.example/live\"");
        adminParams.ShouldContain("\"rtmp_stream_key\":\"stream-key\"");
        var admin = SingleParticipant(adminJoin);
        admin.Muted.ShouldBeTrue();
        admin.CanSelfUnmute.ShouldBeFalse();

        harness.ClearPushes();
        var memberJoin = (TUpdates)await harness.JoinAsync(MemberUserId, muted: true);
        memberJoin.Updates.OfType<TUpdateGroupCallConnection>().Single().Params.Data.ShouldBe("{\"stream\":true,\"rtmp\":true}");
        var member = SingleParticipant(memberJoin);
        member.JustJoined.ShouldBeTrue();
        member.Versioned.ShouldBeFalse();
        member.CanSelfUnmute.ShouldBeFalse();
        harness.Pushes.SelectMany(p => p.Data is TUpdates u ? u.Updates : []).OfType<TUpdateGroupCallParticipants>()
            .ShouldBeEmpty("a listener's join is not announced to the others");

        var call = memberJoin.Updates.OfType<TUpdateGroupCall>().Single().Call.ShouldBeOfType<MyTelegram.Schema.TGroupCall>();
        call.ListenersHidden.ShouldBeTrue();
        call.ParticipantsCount.ShouldBe(0);
        call.StreamDcId.ShouldBe(1);
        call.CanChangeJoinMuted.ShouldBeFalse();
    }

    [Fact]
    public async Task AdminUnmute_OnlyAllowsSpeaking_ThenTheParticipantUnmutesThemselves()
    {
        var harness = Harness.Create(joinMuted: true);
        await harness.JoinAsync(MemberUserId, muted: false);
        await harness.EditAsync(MemberUserId, new TInputPeerSelf(), raiseHand: true);

        var allowed = SingleParticipant(await harness.EditAsync(AdminUserId, MemberInputPeer(), muted: false));
        allowed.Muted.ShouldBeTrue("an admin unmute does not unmute, it lets the participant speak");
        allowed.CanSelfUnmute.ShouldBeTrue();
        allowed.RaiseHandRating.ShouldBeNull("letting someone speak lowers their hand");

        var unmuted = SingleParticipant(await harness.EditAsync(MemberUserId, new TInputPeerSelf(), muted: false));
        unmuted.Muted.ShouldBeFalse();
        unmuted.CanSelfUnmute.ShouldBeFalse("can_self_unmute is only set on a muted participant");
    }

    [Fact]
    public async Task AdminMute_TakesAwayTheRightToUnmute()
    {
        var harness = Harness.Create();
        await harness.JoinAsync(MemberUserId, muted: false);

        var muted = SingleParticipant(await harness.EditAsync(AdminUserId, MemberInputPeer(), muted: true));
        muted.Muted.ShouldBeTrue();
        muted.CanSelfUnmute.ShouldBeFalse();

        SingleParticipant(await harness.EditAsync(MemberUserId, new TInputPeerSelf(), muted: false)).Muted.ShouldBeTrue();
    }

    [Fact]
    public async Task ParticipantEdits_DoNotChangeTheParticipantListVersion()
    {
        var harness = Harness.Create();
        await harness.JoinAsync(MemberUserId, muted: false);
        var versionAfterJoin = harness.Load().Version;

        await harness.EditAsync(MemberUserId, new TInputPeerSelf(), muted: true);
        await harness.EditAsync(MemberUserId, new TInputPeerSelf(), volume: 5000);

        harness.Load().Version.ShouldBe(versionAfterJoin);
        var participant = harness.Participant(MemberUserId);
        participant.Volume.ShouldBe(5000);
        participant.VolumeBySelf.ShouldBeTrue();
    }

    [Fact]
    public async Task SelfVolume_IsNotVolumeByAdmin()
    {
        var harness = Harness.Create();
        await harness.JoinAsync(MemberUserId, muted: false);

        var before = SingleParticipant(await harness.EditAsync(MemberUserId, new TInputPeerSelf(), muted: true));
        before.VolumeByAdmin.ShouldBeTrue();

        SingleParticipant(await harness.EditAsync(MemberUserId, new TInputPeerSelf(), volume: 5000))
            .VolumeByAdmin.ShouldBeFalse();
    }

    // ---- scheduled calls and permissions -------------------------------------------------------------

    [Fact]
    public async Task JoiningAScheduledCall_IsForbiddenUntilItStarts()
    {
        var harness = Harness.Create(scheduled: true);

        var ex = await Should.ThrowAsync<RpcException>(() => harness.JoinAsync(MemberUserId, muted: true));

        ex.Message.ShouldBe("GROUPCALL_FORBIDDEN");
    }

    [Fact]
    public async Task Member_CannotStartAScheduledCall_ButTheAdminCan()
    {
        var harness = Harness.Create(scheduled: true);

        var ex = await Should.ThrowAsync<RpcException>(() => harness.InvokeAsync("StartScheduledGroupCallHandler",
            MemberUserId, new RequestStartScheduledGroupCall { Call = harness.InputCall() }));
        ex.Message.ShouldBe("GROUPCALL_FORBIDDEN");
        harness.Load().ScheduleDate.ShouldNotBeNull();

        await harness.InvokeAsync("StartScheduledGroupCallHandler", AdminUserId,
            new RequestStartScheduledGroupCall { Call = harness.InputCall() });
        harness.Load().ScheduleDate.ShouldBeNull();
    }

    [Fact]
    public async Task Member_MaySubscribeToAScheduledCall()
    {
        var harness = Harness.Create(scheduled: true);

        await harness.InvokeAsync("ToggleGroupCallStartSubscriptionHandler", MemberUserId,
            new RequestToggleGroupCallStartSubscription { Call = harness.InputCall(), Subscribed = true });

        harness.Load().ScheduleStartSubscriberIds.ShouldContain(MemberUserId);
    }

    [Fact]
    public async Task Member_CannotRecordOrRetitleTheCall()
    {
        var harness = Harness.Create();

        var record = await Should.ThrowAsync<RpcException>(() => harness.InvokeAsync("ToggleGroupCallRecordHandler",
            MemberUserId, new RequestToggleGroupCallRecord { Call = harness.InputCall(), Start = true, Title = "x" }));
        record.Message.ShouldBe("GROUPCALL_FORBIDDEN");

        var title = await Should.ThrowAsync<RpcException>(() => harness.InvokeAsync("EditGroupCallTitleHandler",
            MemberUserId, new RequestEditGroupCallTitle { Call = harness.InputCall(), Title = "hack" }));
        title.Message.ShouldBe("GROUPCALL_FORBIDDEN");

        var stored = harness.Load();
        stored.RecordStartDate.ShouldBeNull();
        stored.Title.ShouldBeNull();
    }

    // ---- invitations ---------------------------------------------------------------------------------

    [Fact]
    public async Task Invite_ByAUserWhoIsNotInTheCall_IsForbidden()
    {
        var harness = Harness.Create();

        var ex = await Should.ThrowAsync<RpcException>(() => harness.InviteAsync(MemberUserId, OtherMemberUserId));

        ex.Message.ShouldBe("GROUPCALL_FORBIDDEN");
        harness.MessageApp.SentMessages.ShouldBeEmpty();
    }

    [Fact]
    public async Task Invite_OfABot_IsUserIdInvalid()
    {
        var harness = Harness.Create();
        await harness.JoinAsync(MemberUserId, muted: true);

        var ex = await Should.ThrowAsync<RpcException>(() => harness.InviteAsync(MemberUserId, BotUserId));

        ex.Message.ShouldBe("USER_ID_INVALID");
    }

    [Fact]
    public async Task Invite_OfSomeoneAlreadyInTheCall_IsUserAlreadyParticipant()
    {
        var harness = Harness.Create();
        await harness.JoinAsync(MemberUserId, muted: true);
        await harness.JoinAsync(OtherMemberUserId, muted: true);

        var ex = await Should.ThrowAsync<RpcException>(() => harness.InviteAsync(MemberUserId, OtherMemberUserId));

        ex.Message.ShouldBe("USER_ALREADY_PARTICIPANT");
    }

    [Fact]
    public async Task Invite_PostsAnInviteServiceMessageFromTheInviter()
    {
        var harness = Harness.Create();
        await harness.JoinAsync(MemberUserId, muted: true);

        await harness.InviteAsync(MemberUserId, OtherMemberUserId);

        harness.Load().InvitedUserIds.ShouldContain(OtherMemberUserId);
        var message = harness.MessageApp.SentMessages.ShouldHaveSingleItem();
        message.RequestInfo.UserId.ShouldBe(MemberUserId);
        message.ToPeer.ShouldBe(new Peer(PeerType.Channel, ChannelId));
        var action = message.MessageAction.ShouldBeOfType<TMessageActionInviteToGroupCall>();
        action.Users.ShouldBe([OtherMemberUserId]);
        action.Call.ShouldBeOfType<TInputGroupCall>().Id.ShouldBe(CallId);
    }

    // ---- leave ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Leave_WhenNotJoined_ReturnsEmptyUpdates()
    {
        var harness = Harness.Create();

        var updates = await harness.InvokeAsync("LeaveGroupCallHandler", MemberUserId,
            new RequestLeaveGroupCall { Call = harness.InputCall(), Source = 1 });

        updates.ShouldBeOfType<TUpdates>().Updates.ShouldBeEmpty();
    }

    [Fact]
    public async Task Leave_PushesAVersionedLeftParticipant_AnswersUnversioned_AndBumpsTheVersion()
    {
        var harness = Harness.Create();
        await harness.JoinAsync(MemberUserId, muted: true, ssrc: 222222);
        var versionAfterJoin = harness.Load().Version;
        harness.ClearPushes();

        var updates = await harness.InvokeAsync("LeaveGroupCallHandler", MemberUserId,
            new RequestLeaveGroupCall { Call = harness.InputCall(), Source = 222222 });

        var left = SingleParticipant(updates);
        left.Left.ShouldBeTrue();
        left.Versioned.ShouldBeFalse("Telegram answers the leaver with an unversioned entry");
        left.VideoJoined.ShouldBeFalse();
        left.Volume.ShouldBeNull();
        harness.Pushes
            .SelectMany(push => push.Updates.OfType<TUpdateGroupCallParticipants>())
            .SelectMany(update => update.Participants.OfType<TGroupCallParticipant>())
            .ShouldAllBe(participant => participant.Left && participant.Versioned);
        harness.Load().Version.ShouldBe(versionAfterJoin + 1);
        harness.Load().Participants.ShouldNotContain(p => p.UserId == MemberUserId);
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private static long SessionHash(long userId) => new FakeAccessHashHelper2().GenerateAccessHash(
        userId,
        PhoneTestFixtures.DefaultAccessHashKeyId(userId),
        CallId,
        AccessHashType.GroupCall);

    private static IInputPeer MemberInputPeer() => new TInputPeerUser { UserId = MemberUserId, AccessHash = 0 };

    private static TGroupCallParticipant SingleParticipant(IObject updates) => updates.ShouldBeOfType<TUpdates>()
        .Updates.OfType<TUpdateGroupCallParticipants>().ShouldHaveSingleItem()
        .Participants.OfType<TGroupCallParticipant>().ShouldHaveSingleItem();

    private sealed class Harness
    {
        private readonly IMongoDatabase _database;
        private readonly InMemoryMongoStore _store;
        private readonly CapturingObjectMessageSender _sender = new();
        private readonly IChannelAdminRightsChecker _adminRights = AdminOnlyRightsChecker();

        private Harness(IMongoDatabase database, InMemoryMongoStore store)
        {
            _database = database;
            _store = store;
        }

        public FakeMessageAppService MessageApp { get; } = new();

        public IReadOnlyList<CapturedPush> Pushes => _sender.Pushes;

        public void ClearPushes() => _sender.Clear();

        public static Harness Create(bool joinMuted = false, bool scheduled = false, bool broadcast = false, bool rtmp = false)
        {
            var database = PhoneTestFixtures.CreateDatabase(out var store);
            database.GetCollection<GroupCallDocument>(PhoneTestFixtures.GroupCallsCollectionName).InsertOne(
                new GroupCallDocument
                {
                    Id = CallId,
                    CallId = CallId,
                    AccessHash = StoredAccessHash,
                    CreatorId = AdminUserId,
                    PeerId = ChannelId,
                    PeerType = (int)PeerType.Channel,
                    Active = true,
                    JoinMuted = joinMuted || broadcast,
                    Broadcast = broadcast,
                    RtmpStream = rtmp,
                    RtmpUrl = rtmp ? "rtmp://stream.example/live" : null,
                    RtmpStreamKey = rtmp ? "stream-key" : null,
                    ScheduleDate = scheduled ? 2_000_000_000 : null,
                    Version = 1
                });

            return new Harness(database, store);
        }

        public TInputGroupCall InputCall() => new() { Id = CallId, AccessHash = StoredAccessHash };

        public GroupCallDocument Load() => BsonSerializer.Deserialize<GroupCallDocument>(
            _store.Documents(PhoneTestFixtures.GroupCallsCollectionName).Single());

        public GroupCallParticipantDoc Participant(long userId) => Load().Participants.Single(p => p.UserId == userId);

        public Task<IObject> JoinAsync(long userId, bool muted, int? ssrc = null) => InvokeAsync(
            "JoinGroupCallHandler",
            userId,
            new RequestJoinGroupCall
            {
                Call = InputCall(),
                JoinAs = new TInputPeerSelf(),
                Muted = muted,
                VideoStopped = true,
                Params = new TDataJSON { Data = $"{{\"ssrc\":{ssrc ?? 100000 + (int)userId}}}" }
            });

        public Task<IObject> EditAsync(long userId, IInputPeer participant, bool? muted = null, int? volume = null,
            bool? raiseHand = null) => InvokeAsync(
            "EditGroupCallParticipantHandler",
            userId,
            new RequestEditGroupCallParticipant
            {
                Call = InputCall(),
                Participant = participant,
                Muted = muted,
                Volume = volume,
                RaiseHand = raiseHand
            });

        public Task<IObject> InviteAsync(long userId, long invitedUserId) => InvokeAsync(
            "InviteToGroupCallHandler",
            userId,
            new RequestInviteToGroupCall
            {
                Call = InputCall(),
                Users = new TVector<IInputUser>(new TInputUser { UserId = invitedUserId, AccessHash = 0 })
            });

        public async Task<IObject> InvokeAsync(string handlerTypeName, long userId, IObject request)
        {
            var type = typeof(GroupCallDocument).Assembly.GetType(
                $"MyTelegram.Messenger.Handlers.LatestLayer.Phone.{handlerTypeName}", throwOnError: true)!;
            var handler = PhoneTestFixtures.CreateGroupCallHandler(type, _database, _sender, MessageApp, _adminRights);
            var method = type.GetMethod("HandleAsync", [typeof(IRequestInput), typeof(IObject)])!;

            object taskObj;
            try
            {
                taskObj = method.Invoke(handler, [PhoneTestFixtures.RequestInput(userId).Build(), request])!;
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }

            return ((TRpcResult)await (Task<IObject>)taskObj).Result;
        }

        /// <summary>Only <see cref="AdminUserId"/> holds admin rights in the call's channel.</summary>
        private static IChannelAdminRightsChecker AdminOnlyRightsChecker()
        {
            var checker = new Mock<IChannelAdminRightsChecker>();
            checker
                .Setup(x => x.HasChatAdminRightAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<Func<ChatAdminRights, bool>>()))
                .ReturnsAsync((long _, long userId, Func<ChatAdminRights, bool> _) => userId == AdminUserId);
            checker
                .Setup(x => x.CheckAdminRightAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<Func<ChatAdminRights, bool>>(), It.IsAny<RpcError?>()))
                .Returns((long _, long userId, Func<ChatAdminRights, bool> _, RpcError? error) =>
                {
                    if (userId != AdminUserId)
                    {
                        (error ?? RpcErrors.RpcErrors400.ChatAdminRequired).ThrowRpcError();
                    }

                    return Task.CompletedTask;
                });
            return checker.Object;
        }
    }
}
