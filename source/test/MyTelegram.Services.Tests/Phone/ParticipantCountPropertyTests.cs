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
/// Property-based test for design Property 8 (Participant-count consistency).
///
/// <para><b>Property 8: Participant-count consistency</b> — the <c>participants_count</c> carried on the
/// emitted <c>groupCall</c> always equals the number of non-<c>Left</c> participants recorded on the
/// <see cref="GroupCallDocument"/>; re-joining the same user/peer replaces the prior entry rather than
/// duplicating it, so a re-join never increases the count.</para>
///
/// The property drives the real <c>JoinGroupCallHandler</c> and <c>LeaveGroupCallHandler</c> over an
/// in-memory Mongo store, applying randomly-generated sequences of join / re-join / leave operations for a
/// small pool of users. After every operation it:
///   * reloads the persisted document and computes the "truth" count as <c>Participants.Count(p => !p.Left)</c>;
///   * asserts the <c>participants_count</c> on the emitted <c>updateGroupCall</c> equals that truth count
///     (Requirement 17.3 / the count reflects currently-joined participants);
///   * tracks an independent oracle of currently-joined users and asserts the count matches it, so a
///     re-join of an already-joined user never grows the count (Requirement 12.7 — replacement, not
///     duplication);
///   * asserts the stored non-left participants map one-to-one onto distinct controlling users.
///
/// <b>Validates: Requirements 12.7, 17.3</b>
/// </summary>
public class ParticipantCountPropertyTests
{
    private const long CallId = 700;
    private const long CreatorId = 1;
    private const long AccessHash = 55555;

    // A small pool of joining users. Each user has a deterministic, distinct SSRC so that concurrent
    // participants never collide (which would otherwise be rejected with GROUPCALL_SSRC_DUPLICATE_MUCH),
    // while a re-join by the same user reuses their own SSRC (a self-controlled entry, so no collision).
    private static readonly long[] UserPool = { 2, 3, 4 };

    private static int SourceFor(long userId) => 100_000 + (int)userId;

    private sealed record Op(bool IsJoin, int UserIndex);

    [Fact]
    public void Count_AlwaysEqualsNonLeftParticipants_AndReJoinNeverIncreasesCount()
    {
        var opGen =
            from isJoin in Gen.Bool
            from userIndex in Gen.Int[0, UserPool.Length - 1]
            select new Op(isJoin, userIndex);

        opGen.Array[1, 14].Sample(ops => RunScenario(ops), iter: 100);
    }

    // A deterministic example: repeatedly re-joining the same user keeps the count pinned at one, and a
    // leave then drops it back to zero.
    [Fact]
    public void ReJoiningSameUser_KeepsCountAtOne_ThenLeaveClearsIt()
    {
        RunScenario(new[]
        {
            new Op(true, 0),   // user 2 joins        -> count 1
            new Op(true, 0),   // user 2 re-joins     -> count still 1 (replacement)
            new Op(true, 0),   // user 2 re-joins     -> count still 1
            new Op(false, 0)   // user 2 leaves       -> count 0
        });
    }

    private static void RunScenario(IReadOnlyList<Op> ops)
    {
        RunScenarioAsync(ops).GetAwaiter().GetResult();
    }

    private static async Task RunScenarioAsync(IReadOnlyList<Op> ops)
    {
        var database = PhoneTestFixtures.CreateDatabase(out var store);
        var sender = new CapturingObjectMessageSender();
        var peerHelper = new PeerHelper();

        var optionsMonitor = new Mock<IOptionsMonitor<MyTelegramMessengerServerOptions>>();
        optionsMonitor.Setup(x => x.CurrentValue).Returns(new MyTelegramMessengerServerOptions());

        // The channel-membership gate is only consulted for channel-peer calls; this is a user-peer call so
        // it is never invoked, but the handler still requires the dependency.
        var channelAppService = new Mock<IChannelAppService>();

        var joinHandler = CreateHandler("JoinGroupCallHandler",
            database, peerHelper, sender, optionsMonitor.Object, channelAppService.Object);
        var leaveHandler = CreateHandler("LeaveGroupCallHandler",
            database, peerHelper, sender);

        var collection = database.GetCollection<GroupCallDocument>(PhoneTestFixtures.GroupCallsCollectionName);
        await collection.InsertOneAsync(new GroupCallDocument
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

        // Oracle: the set of users currently joined (i.e. holding a non-left participant entry).
        var joined = new HashSet<long>();

        foreach (var op in ops)
        {
            var userId = UserPool[op.UserIndex];
            var input = PhoneTestFixtures.RequestInput(userId).Build();
            var wasJoined = joined.Contains(userId);
            var countBefore = CurrentCount(store);

            IUpdates updates;
            if (op.IsJoin)
            {
                updates = await InvokeAsync(joinHandler, input, new RequestJoinGroupCall
                {
                    Call = new TInputGroupCall { Id = CallId, AccessHash = AccessHash },
                    JoinAs = new TInputPeerSelf(),
                    Params = new TDataJSON { Data = $"{{\"ssrc\":{SourceFor(userId)}}}" }
                });
                joined.Add(userId);
            }
            else
            {
                updates = await InvokeAsync(leaveHandler, input, new RequestLeaveGroupCall
                {
                    Call = new TInputGroupCall { Id = CallId, AccessHash = AccessHash },
                    Source = SourceFor(userId)
                });
                joined.Remove(userId);
            }

            var document = LoadGroupCall(store);
            var nonLeftCount = document.Participants.Count(p => !p.Left);

            // Property 8 / R17.3: the participants_count on the emitted groupCall equals the number of
            // non-left participants recorded on the document.
            var emittedCount = TryGetEmittedParticipantsCount(updates);
            if (emittedCount.HasValue)
            {
                emittedCount.Value.ShouldBe(nonLeftCount,
                    $"emitted participants_count ({emittedCount}) != non-left participants ({nonLeftCount}) after {(op.IsJoin ? "join" : "leave")} of {userId}");
            }

            // The oracle of currently-joined users must match the recorded non-left participants exactly.
            nonLeftCount.ShouldBe(joined.Count,
                $"non-left participant count ({nonLeftCount}) != joined-user oracle ({joined.Count}) after {(op.IsJoin ? "join" : "leave")} of {userId}");

            // No duplicate participant for the same user/peer: each non-left participant maps to a distinct
            // controlling user, and every joined user is present exactly once.
            var controllingUsers = document.Participants
                .Where(p => !p.Left)
                .Select(ControllingUserId)
                .ToList();
            controllingUsers.Distinct().Count().ShouldBe(controllingUsers.Count,
                "a non-left participant is duplicated for the same user");
            controllingUsers.ToHashSet().SetEquals(joined).ShouldBeTrue();

            // R12.7 / Property 8: re-joining an already-joined user replaces (does not duplicate) the entry,
            // so the count must not increase.
            if (op.IsJoin && wasJoined)
            {
                var countAfter = CurrentCount(store);
                countAfter.ShouldBe(countBefore,
                    $"re-join of already-joined user {userId} changed the count: {countBefore} -> {countAfter}");
            }
        }
    }

    private static long ControllingUserId(GroupCallParticipantDoc participant)
        => participant.UserId != 0 ? participant.UserId : participant.PeerId;

    private static int CurrentCount(InMemoryMongoStore store)
        => LoadGroupCall(store).Participants.Count(p => !p.Left);

    private static GroupCallDocument LoadGroupCall(InMemoryMongoStore store)
    {
        var doc = store.Documents(PhoneTestFixtures.GroupCallsCollectionName).Single();
        return BsonSerializer.Deserialize<GroupCallDocument>(doc);
    }

    private static int? TryGetEmittedParticipantsCount(IUpdates updates)
    {
        if (updates is not TUpdates tUpdates)
        {
            return null;
        }

        var groupCallUpdate = tUpdates.Updates.OfType<TUpdateGroupCall>().FirstOrDefault();
        return groupCallUpdate?.Call is MyTelegram.Schema.TGroupCall groupCall ? groupCall.ParticipantsCount : null;
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

    private static object CreateHandler(string handlerTypeName, params object[] args)
    {
        var assembly = typeof(GroupCallDocument).Assembly;
        var type = assembly.GetType($"MyTelegram.Messenger.Handlers.LatestLayer.Phone.{handlerTypeName}", throwOnError: true)!;
        return PhoneTestFixtures.CreateGroupCallHandler(type, args);
    }
}
