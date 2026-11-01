using System.Reflection;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;
using MyTelegram.Services.Services;

namespace MyTelegram.Services.Tests.Phone;

/// <summary>
/// Handler-level tests for the group-call settings / recording / title toggles
/// (<c>ToggleGroupCallSettingsHandler</c>, <c>ToggleGroupCallRecordHandler</c>, <c>EditGroupCallTitleHandler</c>).
///
/// Covers Requirements 16.1-16.3 (title), 19.1-19.4 (settings + join_muted + no-op), and
/// 20.1-20.5 (recording audio/video + no-op), including the "no-op rejection emits no update"
/// invariant (design Property 11).
/// </summary>
public class GroupCallToggleHandlerTests
{
    private const long AdminUserId = 1;
    private const long ParticipantUserId = 2;
    private const long CallId = 500;
    private const long AccessHash = 12345;

    // ---- ToggleGroupCallSettings (R19) -----------------------------------------------------------

    [Fact]
    public async Task ToggleSettings_JoinMuted_AppliesAndEmitsUpdateGroupCall()
    {
        var database = PhoneTestFixtures.CreateDatabase(out var store);
        SeedGroupCall(database, joinMuted: false);
        var sender = new CapturingObjectMessageSender();

        var request = new RequestToggleGroupCallSettings { Call = InputCall(), JoinMuted = true };
        var updates = await InvokeAsync("ToggleGroupCallSettingsHandler", database, sender, request);

        // R19.1 / R19.2: setting is applied and the default join-muted state is persisted.
        var stored = LoadGroupCall(store);
        stored.JoinMuted.ShouldBeTrue();
        stored.Version.ShouldBe(1, "settings do not change the participant set, so the version stays (as on Telegram)");

        // R19.1: an updateGroupCall is returned.
        SingleGroupCallUpdate(updates).Call.ShouldBeAssignableTo<MyTelegram.Schema.TGroupCall>();

        // R19.2 / Property 12: the update is delivered to other subscribers, not the originator.
        sender.PushesToUser(ParticipantUserId).ShouldContain(p => p.Carries<TUpdateGroupCall>());
        sender.PushesToUser(AdminUserId).ShouldBeEmpty();
    }

    [Fact]
    public async Task ToggleSettings_NoChange_ThrowsNotModifiedAndEmitsNoUpdate()
    {
        var database = PhoneTestFixtures.CreateDatabase(out var store);
        SeedGroupCall(database, joinMuted: true);
        var sender = new CapturingObjectMessageSender();

        // Requesting the already-current value must be rejected (R19.4).
        var request = new RequestToggleGroupCallSettings { Call = InputCall(), JoinMuted = true };
        var ex = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync("ToggleGroupCallSettingsHandler", database, sender, request));
        ex.Message.ShouldBe("GROUPCALL_NOT_MODIFIED");

        // Property 11: a no-op emits no update and does not mutate state (version unchanged).
        sender.Pushes.ShouldBeEmpty();
        LoadGroupCall(store).Version.ShouldBe(1);
    }

    [Fact]
    public async Task ToggleSettings_UnknownCall_ThrowsGroupCallInvalid()
    {
        var database = PhoneTestFixtures.CreateDatabase(out _);
        var sender = new CapturingObjectMessageSender();

        var request = new RequestToggleGroupCallSettings { Call = InputCall(), JoinMuted = true };
        var ex = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync("ToggleGroupCallSettingsHandler", database, sender, request));
        ex.Message.ShouldBe("GROUPCALL_INVALID"); // R19.3
    }

    // ---- ToggleGroupCallRecord (R20) -------------------------------------------------------------

    [Fact]
    public async Task ToggleRecord_StartWithVideo_RecordsAudioAndVideo()
    {
        var database = PhoneTestFixtures.CreateDatabase(out var store);
        SeedGroupCall(database, joinMuted: false);
        var sender = new CapturingObjectMessageSender();

        var request = new RequestToggleGroupCallRecord { Call = InputCall(), Start = true, Video = true, Title = "Standup" };
        var updates = await InvokeAsync("ToggleGroupCallRecordHandler", database, sender, request);

        // R20.1: recording is active. R20.2: the video flag records both audio and video.
        var stored = LoadGroupCall(store);
        stored.RecordStartDate.ShouldNotBeNull();
        stored.RecordVideoActive.ShouldBeTrue();
        stored.RecordTitle.ShouldBe("Standup");

        SingleGroupCallUpdate(updates); // R20.1: updateGroupCall returned.
        sender.PushesToUser(ParticipantUserId).ShouldContain(p => p.Carries<TUpdateGroupCall>());
    }

    [Fact]
    public async Task ToggleRecord_StartAudioOnly_DoesNotActivateVideoRecording()
    {
        var database = PhoneTestFixtures.CreateDatabase(out var store);
        SeedGroupCall(database, joinMuted: false);
        var sender = new CapturingObjectMessageSender();

        // R20.2: video flag NOT set -> audio only; record_video_active must stay false even though
        // recording is active.
        var request = new RequestToggleGroupCallRecord { Call = InputCall(), Start = true, Video = false };
        await InvokeAsync("ToggleGroupCallRecordHandler", database, sender, request);

        var stored = LoadGroupCall(store);
        stored.RecordStartDate.ShouldNotBeNull();
        stored.RecordVideoActive.ShouldBeFalse();
    }

    [Fact]
    public async Task ToggleRecord_Stop_ClearsRecordingState()
    {
        var database = PhoneTestFixtures.CreateDatabase(out var store);
        SeedGroupCall(database, joinMuted: false, recording: true, recordVideoActive: true);
        var sender = new CapturingObjectMessageSender();

        var request = new RequestToggleGroupCallRecord { Call = InputCall(), Start = false };
        await InvokeAsync("ToggleGroupCallRecordHandler", database, sender, request);

        // R20.1: stopping recording clears the recording state.
        var stored = LoadGroupCall(store);
        stored.RecordStartDate.ShouldBeNull();
        stored.RecordVideoActive.ShouldBeFalse();
    }

    [Fact]
    public async Task ToggleRecord_StartWhenAlreadyRecording_ThrowsNotModifiedAndEmitsNoUpdate()
    {
        var database = PhoneTestFixtures.CreateDatabase(out var store);
        SeedGroupCall(database, joinMuted: false, recording: true);
        var sender = new CapturingObjectMessageSender();

        // R20.5: requesting the already-current recording state is rejected.
        var request = new RequestToggleGroupCallRecord { Call = InputCall(), Start = true };
        var ex = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync("ToggleGroupCallRecordHandler", database, sender, request));
        ex.Message.ShouldBe("GROUPCALL_NOT_MODIFIED");

        // Property 11: no update emitted, state unchanged.
        sender.Pushes.ShouldBeEmpty();
        LoadGroupCall(store).Version.ShouldBe(1);
    }

    [Fact]
    public async Task ToggleRecord_StopWhenNotRecording_ThrowsNotModified()
    {
        var database = PhoneTestFixtures.CreateDatabase(out _);
        SeedGroupCall(database, joinMuted: false, recording: false);
        var sender = new CapturingObjectMessageSender();

        var request = new RequestToggleGroupCallRecord { Call = InputCall(), Start = false };
        var ex = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync("ToggleGroupCallRecordHandler", database, sender, request));
        ex.Message.ShouldBe("GROUPCALL_NOT_MODIFIED"); // R20.5
        sender.Pushes.ShouldBeEmpty();
    }

    [Fact]
    public async Task ToggleRecord_EndedCall_ThrowsGroupCallForbidden()
    {
        var database = PhoneTestFixtures.CreateDatabase(out _);
        SeedGroupCall(database, joinMuted: false, active: false);
        var sender = new CapturingObjectMessageSender();

        var request = new RequestToggleGroupCallRecord { Call = InputCall(), Start = true };
        var ex = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync("ToggleGroupCallRecordHandler", database, sender, request));
        ex.Message.ShouldBe("GROUPCALL_FORBIDDEN"); // R20.4
    }

    [Fact]
    public async Task ToggleRecord_UnknownCall_ThrowsGroupCallInvalid()
    {
        var database = PhoneTestFixtures.CreateDatabase(out _);
        var sender = new CapturingObjectMessageSender();

        var request = new RequestToggleGroupCallRecord { Call = InputCall(), Start = true };
        var ex = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync("ToggleGroupCallRecordHandler", database, sender, request));
        ex.Message.ShouldBe("GROUPCALL_INVALID"); // R20.3
    }

    // ---- EditGroupCallTitle (R16) ----------------------------------------------------------------

    [Fact]
    public async Task EditTitle_AppliesAndEmitsUpdateGroupCall()
    {
        var database = PhoneTestFixtures.CreateDatabase(out var store);
        SeedGroupCall(database, joinMuted: false);
        var sender = new CapturingObjectMessageSender();

        var request = new RequestEditGroupCallTitle { Call = InputCall(), Title = "Weekly sync" };
        var updates = await InvokeAsync("EditGroupCallTitleHandler", database, sender, request);

        // R16.1: the title is set and an updateGroupCall is returned.
        LoadGroupCall(store).Title.ShouldBe("Weekly sync");
        SingleGroupCallUpdate(updates);

        // R16.2: the update is delivered to participants.
        sender.PushesToUser(ParticipantUserId).ShouldContain(p => p.Carries<TUpdateGroupCall>());
    }

    [Fact]
    public async Task EditTitle_UnknownCall_ThrowsGroupCallInvalid()
    {
        var database = PhoneTestFixtures.CreateDatabase(out _);
        var sender = new CapturingObjectMessageSender();

        var request = new RequestEditGroupCallTitle { Call = InputCall(), Title = "x" };
        var ex = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync("EditGroupCallTitleHandler", database, sender, request));
        ex.Message.ShouldBe("GROUPCALL_INVALID"); // R16.3
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static IInputGroupCall InputCall() => new TInputGroupCall { Id = CallId, AccessHash = AccessHash };

    private static void SeedGroupCall(
        IMongoDatabase database,
        bool joinMuted,
        bool active = true,
        bool recording = false,
        bool recordVideoActive = false)
    {
        var collection = database.GetCollection<GroupCallDocument>(PhoneTestFixtures.GroupCallsCollectionName);
        collection.InsertOne(new GroupCallDocument
        {
            Id = CallId,
            CallId = CallId,
            AccessHash = AccessHash,
            CreatorId = AdminUserId,
            PeerId = AdminUserId,
            PeerType = (int)PeerType.User,
            Active = active,
            JoinMuted = joinMuted,
            Version = 1,
            RecordStartDate = recording ? 1000 : null,
            RecordVideoActive = recordVideoActive,
            Participants = new List<GroupCallParticipantDoc>
            {
                new() { UserId = ParticipantUserId, PeerId = ParticipantUserId, PeerType = (int)PeerType.User, Source = 111 }
            }
        });
    }

    private static GroupCallDocument LoadGroupCall(InMemoryMongoStore store)
    {
        var doc = store.Documents(PhoneTestFixtures.GroupCallsCollectionName).Single();
        return BsonSerializer.Deserialize<GroupCallDocument>(doc);
    }

    private static TUpdateGroupCall SingleGroupCallUpdate(IUpdates updates)
    {
        var tUpdates = updates.ShouldBeOfType<TUpdates>();
        return tUpdates.Updates.OfType<TUpdateGroupCall>().ShouldHaveSingleItem();
    }

    private static async Task<IUpdates> InvokeAsync(
        string handlerTypeName,
        IMongoDatabase database,
        IObjectMessageSender sender,
        IObject request)
    {
        var assembly = typeof(GroupCallDocument).Assembly;
        var type = assembly.GetType($"MyTelegram.Messenger.Handlers.LatestLayer.Phone.{handlerTypeName}", throwOnError: true)!;
        var handler = PhoneTestFixtures.CreateGroupCallHandler(type, database, sender);
        var method = type.GetMethod("HandleAsync", new[] { typeof(IRequestInput), typeof(IObject) })!;

        var input = PhoneTestFixtures.RequestInput(AdminUserId).Build();
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
