using System.Reflection;
using CsCheck;
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
/// Property-based tests for group-call SSRC (participant <c>Source</c>) allocation.
///
/// <para><b>Property 9: SSRC uniqueness</b> — no two distinct active participants share a
/// <see cref="GroupCallParticipantDoc.Source"/>; a colliding assignment is rejected with
/// <c>GROUPCALL_SSRC_DUPLICATE_MUCH</c> so the client retries with a new source.</para>
///
/// The property drives the real <c>JoinGroupCallHandler</c> over an in-memory Mongo store. It seeds a
/// group call with a set of active participants holding distinct sources, then makes a fresh user join
/// requesting a specific SSRC:
/// <list type="bullet">
///   <item>When the requested SSRC collides with an existing active participant (owned by a different
///     user), the join must be rejected with <c>GROUPCALL_SSRC_DUPLICATE_MUCH</c> and no participant may
///     be persisted.</item>
///   <item>When the requested SSRC is free, the join succeeds, the participant is added with that source,
///     and every active participant still has a distinct source.</item>
/// </list>
///
/// <b>Validates: Requirements 12.8</b>
/// </summary>
public class SsrcUniquenessPropertyTests
{
    private const long CreatorId = 1;
    private const long CallId = 700;
    private const long AccessHash = 55555;
    private const long JoiningUserId = 500;

    // Existing participants draw sources from [100_000, 199_999]; a "fresh" (guaranteed-free) source is
    // drawn from the disjoint range [200_000, 299_999]. This lets the generator deterministically pick a
    // colliding source (an existing one) or a non-colliding source (a fresh one).
    [Fact]
    public void Join_AssignsUniqueSource_OrRejectsCollisionWithDuplicateError()
    {
        var gen =
            from n in Gen.Int[1, 6]
            from sources in Gen.Int[100_000, 199_999].Array[n].Where(a => a.Distinct().Count() == a.Length)
            from useExisting in Gen.Bool
            from idx in Gen.Int[0, n - 1]
            from fresh in Gen.Int[200_000, 299_999]
            select (sources, candidate: useExisting ? sources[idx] : fresh, collides: useExisting);

        gen.Sample(t => RunScenario(t.sources, t.candidate, t.collides));
    }

    // ---- deterministic examples that complement the property -------------------------------------

    [Fact]
    public void Join_WithSsrcOfAnotherActiveParticipant_IsRejected()
    {
        RunScenario(existingSources: new[] { 123456, 654321 }, candidate: 123456, expectCollision: true);
    }

    [Fact]
    public void Join_WithFreeSsrc_IsAssignedAndKeepsSourcesUnique()
    {
        RunScenario(existingSources: new[] { 123456, 654321 }, candidate: 222222, expectCollision: false);
    }

    // A user re-joining with a source that only their own previous entry held is NOT a collision: the
    // previous entry is replaced, so no two distinct active participants ever share the source.
    [Fact]
    public async Task Rejoin_WithOwnSource_IsAllowed_AndDoesNotDuplicate()
    {
        var database = PhoneTestFixtures.CreateDatabase(out var store);
        var sender = new CapturingObjectMessageSender();

        const int selfSource = 314159;
        const int otherSource = 271828;
        var collection = database.GetCollection<GroupCallDocument>(PhoneTestFixtures.GroupCallsCollectionName);
        collection.InsertOne(new GroupCallDocument
        {
            Id = CallId,
            CallId = CallId,
            AccessHash = AccessHash,
            CreatorId = CreatorId,
            PeerId = CreatorId,
            PeerType = (int)PeerType.User,
            Active = true,
            Version = 1,
            Participants = new List<GroupCallParticipantDoc>
            {
                new() { UserId = JoiningUserId, PeerId = JoiningUserId, PeerType = (int)PeerType.User, Source = selfSource, Date = 1000 },
                new() { UserId = 100, PeerId = 100, PeerType = (int)PeerType.User, Source = otherSource, Date = 1001 }
            }
        });

        var handler = CreateJoinHandler(database, sender);
        var input = PhoneTestFixtures.RequestInput(JoiningUserId).Build();
        var request = BuildJoin(selfSource);

        await InvokeAsync(handler, input, request);

        var stored = LoadGroupCall(store);
        stored.Participants.Count(p => !p.Left).ShouldBe(2);
        stored.Participants.Single(p => !p.Left && p.UserId == JoiningUserId).Source.ShouldBe(selfSource);
        AssertSourcesUnique(stored);
    }

    // ---- scenario runner -------------------------------------------------------------------------

    private static void RunScenario(int[] existingSources, int candidate, bool expectCollision)
        => RunScenarioAsync(existingSources, candidate, expectCollision).GetAwaiter().GetResult();

    private static async Task RunScenarioAsync(int[] existingSources, int candidate, bool expectCollision)
    {
        var database = PhoneTestFixtures.CreateDatabase(out var store);
        var sender = new CapturingObjectMessageSender();
        SeedGroupCall(database, existingSources);

        var handler = CreateJoinHandler(database, sender);
        var input = PhoneTestFixtures.RequestInput(JoiningUserId).Build();
        var request = BuildJoin(candidate);

        if (expectCollision)
        {
            // Property 9: a colliding assignment is rejected with GROUPCALL_SSRC_DUPLICATE_MUCH.
            var ex = await Should.ThrowAsync<RpcException>(() => InvokeAsync(handler, input, request));
            ex.Message.ShouldBe("GROUPCALL_SSRC_DUPLICATE_MUCH");

            // The rejected join must not have been persisted; the participant set is unchanged and unique.
            var stored = LoadGroupCall(store);
            stored.Participants.Count(p => !p.Left).ShouldBe(existingSources.Length);
            AssertSourcesUnique(stored);
        }
        else
        {
            await InvokeAsync(handler, input, request);

            var stored = LoadGroupCall(store);
            var joined = stored.Participants.Single(p => !p.Left && p.UserId == JoiningUserId);
            joined.Source.ShouldBe(candidate);
            stored.Participants.Count(p => !p.Left).ShouldBe(existingSources.Length + 1);

            // Property 9: no two distinct active participants share a source.
            AssertSourcesUnique(stored);
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static RequestJoinGroupCall BuildJoin(int ssrc) => new()
    {
        Call = new TInputGroupCall { Id = CallId, AccessHash = AccessHash },
        JoinAs = new TInputPeerUser { UserId = JoiningUserId, AccessHash = 0 },
        Params = new TDataJSON { Data = $"{{\"ssrc\": {ssrc}}}" },
        Muted = false,
        VideoStopped = false
    };

    private static void SeedGroupCall(IMongoDatabase database, int[] sources)
    {
        var participants = new List<GroupCallParticipantDoc>();
        for (var i = 0; i < sources.Length; i++)
        {
            // Participant users are distinct from the creator (1) and the joining user (500), so their
            // sources are never "controlled by" the joining user - any match is a genuine collision.
            var userId = 100 + i;
            participants.Add(new GroupCallParticipantDoc
            {
                UserId = userId,
                PeerId = userId,
                PeerType = (int)PeerType.User,
                Source = sources[i],
                Date = 1000 + i
            });
        }

        var collection = database.GetCollection<GroupCallDocument>(PhoneTestFixtures.GroupCallsCollectionName);
        collection.InsertOne(new GroupCallDocument
        {
            Id = CallId,
            CallId = CallId,
            AccessHash = AccessHash,
            CreatorId = CreatorId,
            PeerId = CreatorId,
            PeerType = (int)PeerType.User,
            Active = true,
            Version = 1,
            Participants = participants
        });
    }

    private static void AssertSourcesUnique(GroupCallDocument call)
    {
        var activeSources = call.Participants.Where(p => !p.Left).Select(p => p.Source).ToList();
        activeSources.Distinct().Count().ShouldBe(activeSources.Count,
            "two distinct active participants must never share a Source");
    }

    private static GroupCallDocument LoadGroupCall(InMemoryMongoStore store)
    {
        var doc = store.Documents(PhoneTestFixtures.GroupCallsCollectionName).Single();
        return BsonSerializer.Deserialize<GroupCallDocument>(doc);
    }

    private static object CreateJoinHandler(IMongoDatabase database, IObjectMessageSender sender)
    {
        var optionsMonitor = new Mock<IOptionsMonitor<MyTelegramMessengerServerOptions>>();
        optionsMonitor
            .SetupGet(x => x.CurrentValue)
            .Returns(new MyTelegramMessengerServerOptions { WebRtcConnections = new List<WebRtcConnection>() });

        var channelAppService = new Mock<IChannelAppService>();

        var assembly = typeof(GroupCallDocument).Assembly;
        var type = assembly.GetType(
            "MyTelegram.Messenger.Handlers.LatestLayer.Phone.JoinGroupCallHandler",
            throwOnError: true)!;

        return PhoneTestFixtures.CreateGroupCallHandler(
            type,
            database,
            new PeerHelper(),
            sender,
            optionsMonitor.Object,
            channelAppService.Object)!;
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
}
