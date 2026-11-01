using System.Reflection;
using System.Text.Json;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;

namespace MyTelegram.Services.Tests.Phone;

/// <summary>
/// The messenger's side of the SFU: joining allocates a connection there and answers with Telegram's
/// connection JSON, leaving and discarding release it, and checkGroupCall reports what the SFU holds.
/// </summary>
public class GroupCallSfuTests
{
    private const long CreatorId = 1;
    private const long JoinerId = 2;
    private const long CallId = 900;
    private const long AccessHash = 4321;
    private const string CameraParams =
        """{"ufrag":"cl","pwd":"clientpwd","fingerprints":[{"hash":"sha-256","fingerprint":"01:02","setup":"passive"}],"ssrc":222222,"ssrc-groups":[{"semantics":"SIM","sources":[1000,1002,-2]},{"semantics":"FID","sources":[1000,1001]},{"semantics":"FID","sources":[1002,1003]},{"semantics":"FID","sources":[-2,-1]}]}""";
    private const string ScreenParams =
        """{"ufrag":"sc","pwd":"screenpwd","fingerprints":[{"hash":"sha-256","fingerprint":"03:04","setup":"passive"}],"ssrc":333333,"ssrc-groups":[{"semantics":"FID","sources":[5000,5001]}]}""";
    private const string JoinParams =
        """{"ufrag":"cl","pwd":"clientpwd","fingerprints":[{"hash":"sha-256","fingerprint":"01:02","setup":"passive"}],"ssrc":222222,"ssrc-groups":[]}""";

    [Fact]
    public async Task Join_AllocatesOnTheSfu_AndAnswersWithTelegramsConnectionJson()
    {
        var harness = new Harness();

        var updates = await harness.JoinAsync(JoinerId, JoinParams);

        harness.Sfu.Joins.ShouldHaveSingleItem().ShouldBe((CallId, JoinerId.ToString(), JoinParams));
        var connection = updates.ShouldBeOfType<TUpdates>().Updates.OfType<TUpdateGroupCallConnection>().ShouldHaveSingleItem();
        connection.Presentation.ShouldBeFalse();
        using var json = JsonDocument.Parse(connection.Params.Data);
        var transport = json.RootElement.GetProperty("transport");
        transport.GetProperty("ufrag").GetString().ShouldBe("sfuufrag");
        transport.GetProperty("candidates")[0].GetProperty("port").GetString().ShouldBe("32000");
        var audio = json.RootElement.GetProperty("audio");
        audio.GetProperty("payload-types")[0].GetProperty("id").GetInt32().ShouldBe(111);
        audio.GetProperty("rtp-hdrexts").EnumerateArray().Select(e => e.GetProperty("id").GetInt32()).ShouldBe([1, 2, 3]);
        json.RootElement.TryGetProperty("stream", out _).ShouldBeFalse();
        var video = json.RootElement.GetProperty("video");
        video.GetProperty("endpoint").GetString().ShouldBe("endpoint-2");
        video.GetProperty("server_sources")[0].GetInt32().ShouldBe(-1931371821);
        harness.Load().Participants.Single().VideoEndpoint.ShouldBe("endpoint-2");
    }

    [Fact]
    public async Task ACameraParticipant_IsShownWithItsEndpointAndSsrcGroups_UntilTheCameraStops()
    {
        var harness = new Harness();
        await harness.JoinAsync(JoinerId, CameraParams, videoStopped: false);

        var participants = await harness.InvokeAsync("GetGroupParticipantsHandler", CreatorId,
            new RequestGetGroupParticipants { Call = Harness.InputCall(), Ids = new TVector<IInputPeer>(), Sources = new TVector<int>(), Offset = string.Empty, Limit = 10 });
        var video = participants.ShouldBeOfType<TGroupParticipants>().Participants.OfType<TGroupCallParticipant>()
            .Single().Video.ShouldBeOfType<TGroupCallParticipantVideo>();
        video.Endpoint.ShouldBe("endpoint-2");
        video.Paused.ShouldBeFalse();
        var groups = video.SourceGroups.OfType<TGroupCallParticipantVideoSourceGroup>().ToList();
        groups.Select(g => g.Semantics).ShouldBe(["SIM", "FID", "FID", "FID"]);
        groups[0].Sources.ShouldBe([1000, 1002, -2]);

        await harness.InvokeAsync("EditGroupCallParticipantHandler", JoinerId,
            new RequestEditGroupCallParticipant { Call = Harness.InputCall(), Participant = new TInputPeerSelf(), VideoStopped = true });
        var afterStop = await harness.InvokeAsync("GetGroupParticipantsHandler", CreatorId,
            new RequestGetGroupParticipants { Call = Harness.InputCall(), Ids = new TVector<IInputPeer>(), Sources = new TVector<int>(), Offset = string.Empty, Limit = 10 });
        afterStop.ShouldBeOfType<TGroupParticipants>().Participants.OfType<TGroupCallParticipant>().Single().Video.ShouldBeNull();
    }

    [Fact]
    public async Task AnAudioOnlyParticipant_HasNoVideo()
    {
        var harness = new Harness();
        await harness.JoinAsync(JoinerId, JoinParams, videoStopped: false);

        harness.Load().Participants.Single().VideoEndpoint.ShouldNotBeNull();
        (await harness.ParticipantAsync()).Video.ShouldBeNull();
    }

    [Fact]
    public async Task ScreenSharing_IsItsOwnSfuConnection_ShownAsThePresentation_AndReleasedOnStop()
    {
        var harness = new Harness();
        await harness.JoinAsync(JoinerId, JoinParams);

        var joined = await harness.InvokeAsync("JoinGroupCallPresentationHandler", JoinerId,
            new RequestJoinGroupCallPresentation { Call = Harness.InputCall(), Params = new TDataJSON { Data = ScreenParams } });

        harness.Sfu.Joins.Last().Key.ShouldBe($"{JoinerId}:presentation");
        joined.ShouldBeOfType<TUpdates>().Updates.OfType<TUpdateGroupCallConnection>().ShouldHaveSingleItem().Presentation.ShouldBeTrue();
        var presentation = (await harness.ParticipantAsync()).Presentation.ShouldBeOfType<TGroupCallParticipantVideo>();
        presentation.Endpoint.ShouldBe($"endpoint-{JoinerId}:presentation");

        await harness.InvokeAsync("LeaveGroupCallPresentationHandler", JoinerId,
            new RequestLeaveGroupCallPresentation { Call = Harness.InputCall() });

        harness.Sfu.Leaves.ShouldContain((CallId, $"{JoinerId}:presentation"));
        (await harness.ParticipantAsync()).Presentation.ShouldBeNull();
    }

    [Fact]
    public async Task Join_WithParamsTheSfuCannotUse_IsDataJsonInvalid_AndNotRecorded()
    {
        var harness = new Harness();
        harness.Sfu.JoinFailure = new GroupCallJoinParamsInvalidException("no fingerprint");

        var ex = await Should.ThrowAsync<RpcException>(() => harness.JoinAsync(JoinerId, JoinParams));

        ex.Message.ShouldBe("DATA_JSON_INVALID");
        harness.Load().Participants.ShouldBeEmpty();
    }

    [Fact]
    public async Task Join_WhenTheSfuIsDown_IsInternal_AndNotRecorded()
    {
        var harness = new Harness();
        harness.Sfu.JoinFailure = new HttpRequestException("connection refused");

        var ex = await Should.ThrowAsync<RpcException>(() => harness.JoinAsync(JoinerId, JoinParams));

        ex.Message.ShouldBe("INTERNAL");
        harness.Load().Participants.ShouldBeEmpty();
    }

    [Fact]
    public async Task Leave_ReleasesTheSfuConnection()
    {
        var harness = new Harness();
        await harness.JoinAsync(JoinerId, JoinParams);

        await harness.InvokeAsync("LeaveGroupCallHandler", JoinerId,
            new RequestLeaveGroupCall { Call = Harness.InputCall(), Source = 222222 });

        harness.Sfu.Leaves.ShouldHaveSingleItem().ShouldBe((CallId, JoinerId.ToString()));
    }

    [Fact]
    public async Task Discard_EndsTheCallOnTheSfu()
    {
        var harness = new Harness();

        await harness.InvokeAsync("DiscardGroupCallHandler", CreatorId,
            new RequestDiscardGroupCall { Call = Harness.InputCall() });

        harness.Sfu.Ends.ShouldHaveSingleItem().ShouldBe(CallId);
    }

    [Fact]
    public async Task CheckGroupCall_ReportsWhatTheSfuHolds()
    {
        var harness = new Harness();
        await harness.JoinAsync(JoinerId, JoinParams);
        harness.Sfu.Sources.Add(333333);

        var result = await harness.InvokeAsync("CheckGroupCallHandler", JoinerId,
            new RequestCheckGroupCall { Call = Harness.InputCall(), Sources = new TVector<int>(222222, 333333) });

        // 222222 is recorded as joined, but the SFU does not hold it: the client has to rejoin.
        result.ShouldBeOfType<TVector<int>>().ShouldBe([333333]);
    }

    private sealed class Harness
    {
        private readonly IMongoDatabase _database;
        private readonly InMemoryMongoStore _store;
        private readonly CapturingObjectMessageSender _sender = new();
        private readonly FakeMessageAppService _messageApp = new();

        public Harness()
        {
            _database = PhoneTestFixtures.CreateDatabase(out _store);
            _database.GetCollection<GroupCallDocument>(PhoneTestFixtures.GroupCallsCollectionName).InsertOne(new GroupCallDocument
            {
                Id = CallId,
                CallId = CallId,
                AccessHash = AccessHash,
                CreatorId = CreatorId,
                PeerId = CreatorId,
                PeerType = (int)PeerType.User,
                Active = true,
                Version = 1
            });
        }

        public FakeGroupCallMediaServer Sfu { get; } = new();

        public static TInputGroupCall InputCall() => new() { Id = CallId, AccessHash = AccessHash };

        public GroupCallDocument Load() => BsonSerializer.Deserialize<GroupCallDocument>(
            _store.Documents(PhoneTestFixtures.GroupCallsCollectionName).Single());

        public async Task<TGroupCallParticipant> ParticipantAsync()
        {
            var result = await InvokeAsync("GetGroupParticipantsHandler", CreatorId, new RequestGetGroupParticipants
            {
                Call = InputCall(),
                Ids = new TVector<IInputPeer>(),
                Sources = new TVector<int>(),
                Offset = string.Empty,
                Limit = 10
            });
            return result.ShouldBeOfType<TGroupParticipants>().Participants.OfType<TGroupCallParticipant>().Single();
        }

        public Task<IObject> JoinAsync(long userId, string paramsJson, bool videoStopped = true) => InvokeAsync("JoinGroupCallHandler", userId,
            new RequestJoinGroupCall
            {
                Call = InputCall(),
                JoinAs = new TInputPeerSelf(),
                Muted = true,
                VideoStopped = videoStopped,
                Params = new TDataJSON { Data = paramsJson }
            });

        public async Task<IObject> InvokeAsync(string handlerTypeName, long userId, IObject request)
        {
            var type = typeof(GroupCallDocument).Assembly.GetType(
                $"MyTelegram.Messenger.Handlers.LatestLayer.Phone.{handlerTypeName}", throwOnError: true)!;
            var handler = PhoneTestFixtures.CreateGroupCallHandler(type, _database, _sender, _messageApp, Sfu);
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
    }
}
