using System.Reflection;
using Microsoft.Extensions.Options;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Moq;
using MyTelegram.Messenger;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;
using MyTelegram.Services.Services;

namespace MyTelegram.Services.Tests.Phone;

/// <summary>
/// Handler-level lifecycle tests for the group-call surface. Drives the real handlers over an in-memory
/// Mongo store through the full lifecycle:
///
///   create (<c>CreateGroupCallHandler</c>)
///     → join (<c>JoinGroupCallHandler</c>: SSRC assignment + <c>updateGroupCallConnection</c>)
///     → re-join replacement (same user, new SSRC, count unchanged)
///     → edit participant (<c>EditGroupCallParticipantHandler</c>)
///     → leave (<c>LeaveGroupCallHandler</c>: <c>left</c> flag)
///     → discard (<c>DiscardGroupCallHandler</c>: <c>groupCallDiscarded</c>).
///
/// Also covers the scheduled-call error paths: creating with a past <c>schedule_date</c>
/// (<c>SCHEDULE_DATE_INVALID</c>) and starting an already-started (non-scheduled) call
/// (<c>GROUPCALL_ALREADY_STARTED</c>).
///
/// Requirements: 11.6 (past schedule date), 12.7 (re-join replacement / count consistency),
/// 21.4 (start-already-started). Also exercises 11.1, 12.1, 13.2, 14.1, 15.1, 21.1.
/// </summary>
public class GroupCallLifecycleHandlerTests
{
    private const long CreatorId = 1;
    private const long JoinerId = 2;
    private const int CreatedCallId = 900;

    // ---- full lifecycle -------------------------------------------------------------------------

    [Fact]
    public async Task GroupCall_FullLifecycle_Create_Join_ReJoin_Edit_Leave_Discard()
    {
        var database = PhoneTestFixtures.CreateDatabase(out var store);
        var sender = new CapturingObjectMessageSender();

        // ---- create (R11.1) ---------------------------------------------------------------------
        var createHandler = CreateCreateHandler(database);
        var createUpdates = await InvokeAsync(createHandler, RequestInput(CreatorId), new RequestCreateGroupCall
        {
            Peer = new TInputPeerUser { UserId = CreatorId, AccessHash = 0 },
            RandomId = 12345,
            Title = "Team sync"
        });

        var created = LoadGroupCall(store);
        created.CallId.ShouldBe((long)CreatedCallId);
        created.Active.ShouldBeTrue();
        created.Title.ShouldBe("Team sync");
        // The create emits an updateGroupCall carrying an active groupCall constructor.
        SingleGroupCall(createUpdates).ShouldBeAssignableTo<MyTelegram.Schema.TGroupCall>();

        var inputCall = new TInputGroupCall { Id = created.CallId, AccessHash = created.AccessHash };

        // ---- join (R12.1): SSRC assignment + connection -----------------------------------------
        var joinHandler = CreateJoinHandler(database, sender);
        const int firstSsrc = 424242;
        sender.Clear();
        var joinUpdates = (TUpdates)await InvokeAsync(joinHandler, RequestInput(JoinerId), new RequestJoinGroupCall
        {
            Call = inputCall,
            JoinAs = new TInputPeerSelf(),
            Params = new TDataJSON { Data = $"{{\"ssrc\":{firstSsrc}}}" }
        });

        // The response carries the groupCall and the joining participant (with the assigned SSRC). There is
        // no connection update: an ordinary call needs a media server's transport, and none is deployed.
        joinUpdates.Updates.OfType<TUpdateGroupCall>().ShouldNotBeEmpty();
        joinUpdates.Updates.OfType<TUpdateGroupCallConnection>().ShouldBeEmpty();
        var joinedParticipant = joinUpdates.Updates
            .OfType<TUpdateGroupCallParticipants>().ShouldHaveSingleItem()
            .Participants.OfType<TGroupCallParticipant>().ShouldHaveSingleItem();
        joinedParticipant.Source.ShouldBe(firstSsrc);

        var afterJoin = LoadGroupCall(store);
        afterJoin.Participants.Count(p => !p.Left).ShouldBe(1);
        afterJoin.Participants.Single(p => !p.Left).Source.ShouldBe(firstSsrc);

        // R12.2 / Property 12: other subscribers (the creator) are notified, the joiner is not.
        sender.PushesToUser(CreatorId).ShouldContain(p => p.Carries<TUpdateGroupCallParticipants>());
        sender.PushesToUser(JoinerId).ShouldBeEmpty();

        // ---- re-join replacement (R12.7) --------------------------------------------------------
        const int secondSsrc = 787878;
        sender.Clear();
        await InvokeAsync(joinHandler, RequestInput(JoinerId), new RequestJoinGroupCall
        {
            Call = inputCall,
            JoinAs = new TInputPeerSelf(),
            Params = new TDataJSON { Data = $"{{\"ssrc\":{secondSsrc}}}" }
        });

        var afterReJoin = LoadGroupCall(store);
        // R12.7: the same user re-joining replaces the prior entry - no duplicate, count unchanged.
        afterReJoin.Participants.Count(p => !p.Left).ShouldBe(1);
        afterReJoin.Participants.Single(p => !p.Left).Source.ShouldBe(secondSsrc);

        // ---- edit participant (R15.1) -----------------------------------------------------------
        var editHandler = CreateEditHandler(database, sender);
        sender.Clear();
        var editUpdates = await InvokeAsync(editHandler, RequestInput(JoinerId), new RequestEditGroupCallParticipant
        {
            Call = inputCall,
            Participant = new TInputPeerSelf(),
            Muted = true,
            Volume = 5000
        });

        var editedParticipant = LoadGroupCall(store).Participants.Single(p => !p.Left);
        editedParticipant.Muted.ShouldBeTrue();
        editedParticipant.Volume.ShouldBe(5000);
        // R15.1: an updateGroupCallParticipants describing the change is returned and fanned out.
        SingleParticipantsUpdate(editUpdates);
        sender.PushesToUser(CreatorId).ShouldContain(p => p.Carries<TUpdateGroupCallParticipants>());

        // ---- leave (R13.2): left flag -----------------------------------------------------------
        var leaveHandler = CreateLeaveHandler(database, sender);
        sender.Clear();
        await InvokeAsync(leaveHandler, RequestInput(JoinerId), new RequestLeaveGroupCall
        {
            Call = inputCall,
            Source = secondSsrc
        });

        LoadGroupCall(store).Participants.Count(p => !p.Left).ShouldBe(0);
        // R13.2: remaining subscribers receive an updateGroupCallParticipants marking the participant left.
        var leftPush = sender.PushesToUser(CreatorId).ShouldHaveSingleItem();
        leftPush.Updates
            .OfType<TUpdateGroupCallParticipants>().ShouldHaveSingleItem()
            .Participants.OfType<TGroupCallParticipant>().ShouldAllBe(p => p.Left);

        // ---- discard (R14.1): groupCallDiscarded ------------------------------------------------
        var discardHandler = CreateDiscardHandler(database, sender);
        sender.Clear();
        var discardUpdates = await InvokeAsync(discardHandler, RequestInput(CreatorId), new RequestDiscardGroupCall
        {
            Call = inputCall
        });

        LoadGroupCall(store).Active.ShouldBeFalse();
        // R14.1: the terminating update carries a groupCallDiscarded constructor.
        SingleGroupCall(discardUpdates).ShouldBeOfType<TGroupCallDiscarded>();
    }

    // ---- scheduled-call error paths -------------------------------------------------------------

    [Fact]
    public async Task CreateScheduled_PastScheduleDate_ThrowsScheduleDateInvalid()
    {
        var database = PhoneTestFixtures.CreateDatabase(out _);
        var createHandler = CreateCreateHandler(database);

        var pastDate = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3600;
        var ex = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync(createHandler, RequestInput(CreatorId), new RequestCreateGroupCall
            {
                Peer = new TInputPeerUser { UserId = CreatorId, AccessHash = 0 },
                RandomId = 99,
                ScheduleDate = pastDate
            }));

        ex.Message.ShouldBe("SCHEDULE_DATE_INVALID"); // R11.6
    }

    [Fact]
    public async Task StartScheduled_WhenCallIsNotScheduled_ThrowsGroupCallAlreadyStarted()
    {
        var database = PhoneTestFixtures.CreateDatabase(out var store);

        // A plain (non-scheduled) active call has no schedule_date, so it is "already started".
        var createHandler = CreateCreateHandler(database);
        await InvokeAsync(createHandler, RequestInput(CreatorId), new RequestCreateGroupCall
        {
            Peer = new TInputPeerUser { UserId = CreatorId, AccessHash = 0 },
            RandomId = 77
        });
        var created = LoadGroupCall(store);
        created.ScheduleDate.ShouldBeNull();

        var startHandler = CreateStartHandler(database, new CapturingObjectMessageSender());
        var ex = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync(startHandler, RequestInput(CreatorId), new RequestStartScheduledGroupCall
            {
                Call = new TInputGroupCall { Id = created.CallId, AccessHash = created.AccessHash }
            }));

        ex.Message.ShouldBe("GROUPCALL_ALREADY_STARTED"); // R21.4
    }

    [Fact]
    public async Task StartScheduled_ActivatesCall_ClearsScheduleDate()
    {
        var database = PhoneTestFixtures.CreateDatabase(out var store);
        var sender = new CapturingObjectMessageSender();

        var futureDate = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600;
        var createHandler = CreateCreateHandler(database);
        await InvokeAsync(createHandler, RequestInput(CreatorId), new RequestCreateGroupCall
        {
            Peer = new TInputPeerUser { UserId = CreatorId, AccessHash = 0 },
            RandomId = 55,
            ScheduleDate = futureDate
        });
        var scheduled = LoadGroupCall(store);
        scheduled.ScheduleDate.ShouldBe(futureDate);

        var startHandler = CreateStartHandler(database, sender);
        var updates = await InvokeAsync(startHandler, RequestInput(CreatorId), new RequestStartScheduledGroupCall
        {
            Call = new TInputGroupCall { Id = scheduled.CallId, AccessHash = scheduled.AccessHash }
        });

        // R21.1: starting a scheduled call activates it, clears schedule_date and emits updateGroupCall.
        LoadGroupCall(store).ScheduleDate.ShouldBeNull();
        SingleGroupCall(updates).ShouldBeAssignableTo<MyTelegram.Schema.TGroupCall>();
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static IRequestInput RequestInput(long userId) => PhoneTestFixtures.RequestInput(userId).Build();

    private static GroupCallDocument LoadGroupCall(InMemoryMongoStore store)
    {
        var doc = store.Documents(PhoneTestFixtures.GroupCallsCollectionName).Single();
        return BsonSerializer.Deserialize<GroupCallDocument>(doc);
    }

    private static MyTelegram.Schema.IGroupCall SingleGroupCall(IUpdates updates)
    {
        var tUpdates = updates.ShouldBeOfType<TUpdates>();
        return tUpdates.Updates.OfType<TUpdateGroupCall>().ShouldHaveSingleItem().Call;
    }

    private static TUpdateGroupCallParticipants SingleParticipantsUpdate(IUpdates updates)
    {
        var tUpdates = updates.ShouldBeOfType<TUpdates>();
        return tUpdates.Updates.OfType<TUpdateGroupCallParticipants>().ShouldHaveSingleItem();
    }

    private static object CreateCreateHandler(IMongoDatabase database)
    {
        var idGenerator = new Mock<IIdGenerator>();
        idGenerator
            .Setup(x => x.NextIdAsync(It.IsAny<IdType>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreatedCallId);

        // A user-peer call never consults the admin-rights checker, but the handler still needs it.
        var channelAdminRightsChecker = new Mock<IChannelAdminRightsChecker>();

        var options = new Mock<IOptionsMonitor<MyTelegramMessengerServerOptions>>();
        options.Setup(x => x.CurrentValue).Returns(new MyTelegramMessengerServerOptions());

        return CreateHandler("CreateGroupCallHandler",
            idGenerator.Object,
            database,
            new PeerHelper(),
            new FakeMessageAppService(),
            options.Object,
            channelAdminRightsChecker.Object);
    }

    private static object CreateJoinHandler(IMongoDatabase database, IObjectMessageSender sender)
    {
        var options = new Mock<IOptionsMonitor<MyTelegramMessengerServerOptions>>();
        options.Setup(x => x.CurrentValue).Returns(new MyTelegramMessengerServerOptions());

        // The channel-membership gate is only consulted for channel-peer calls; unused here.
        var channelAppService = new Mock<IChannelAppService>();

        return CreateHandler("JoinGroupCallHandler",
            database, new PeerHelper(), sender, options.Object, channelAppService.Object);
    }

    private static object CreateEditHandler(IMongoDatabase database, IObjectMessageSender sender)
        => CreateHandler("EditGroupCallParticipantHandler", database, new PeerHelper(), sender);

    private static object CreateLeaveHandler(IMongoDatabase database, IObjectMessageSender sender)
        => CreateHandler("LeaveGroupCallHandler", database, new PeerHelper(), sender);

    private static object CreateDiscardHandler(IMongoDatabase database, IObjectMessageSender sender)
    {
        var channelAdminRightsChecker = new Mock<IChannelAdminRightsChecker>();
        return CreateHandler("DiscardGroupCallHandler",
            database, new PeerHelper(), sender, new FakeMessageAppService(), channelAdminRightsChecker.Object);
    }

    private static object CreateStartHandler(IMongoDatabase database, IObjectMessageSender sender)
        => CreateHandler("StartScheduledGroupCallHandler",
            database, new PeerHelper(), sender, new FakeMessageAppService());

    private static object CreateHandler(string handlerTypeName, params object[] args)
    {
        var assembly = typeof(GroupCallDocument).Assembly;
        var type = assembly.GetType($"MyTelegram.Messenger.Handlers.LatestLayer.Phone.{handlerTypeName}", throwOnError: true)!;
        return PhoneTestFixtures.CreateGroupCallHandler(type, args);
    }

    private static async Task<IUpdates> InvokeAsync(object handler, IRequestInput input, IObject request)
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
        var rpcResult = (TRpcResult)result;
        return (IUpdates)rpcResult.Result;
    }
}
