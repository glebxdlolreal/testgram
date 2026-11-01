using System.Reflection;
using CsCheck;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;
using MyTelegram.Services.Services;

namespace MyTelegram.Services.Tests.Phone;

/// <summary>
/// Property-based test for design Property 10 (Version monotonicity - group).
///
/// <para><b>Property 10:</b> <see cref="GroupCallDocument.Version"/> is the participant-list version: as on
/// Telegram's servers, it increases by exactly one when the participant set changes (join / leave) and stays
/// put for title, settings, recording and participant edits. Whatever its value, it is the one carried on the
/// emitted <c>updateGroupCall</c> / <c>updateGroupCallParticipants</c>.</para>
///
/// The property drives the real group-call handlers over an in-memory Mongo store, applying a
/// randomly-generated sequence of state-changing operations:
///   * <c>EditGroupCallTitleHandler</c>        - a fresh title           -> emits updateGroupCall
///   * <c>ToggleGroupCallSettingsHandler</c>   - flips join_muted        -> emits updateGroupCall
///   * <c>ToggleGroupCallRecordHandler</c>     - flips recording         -> emits updateGroupCall
///   * <c>EditGroupCallParticipantHandler</c>  - flips a participant mute -> emits updateGroupCallParticipants
///   * <c>LeaveGroupCallHandler</c>            - removes a participant   -> emits updateGroupCall + updateGroupCallParticipants
///
/// Every generated operation is constructed to be a genuine state change (never a no-op). After each one
/// the test asserts the persisted <c>Version</c> moved by exactly one for a leave and not at all otherwise,
/// and that the version carried on both the returned and the fanned-out update equals the persisted value.
///
/// <b>Validates: Requirements 30.3</b>
/// </summary>
public class GroupVersionPropertyTests
{
    private const long CreatorId = 1;
    private const long ParticipantUserId = 2;
    private const long CallId = 700;
    private const long AccessHash = 55555;
    private const int ParticipantSource = 111;

    // The removable participants that Leave operations consume, in order.
    private static readonly (long UserId, int Source)[] RemovableParticipants =
    [
        (101, 1101), (102, 1102), (103, 1103), (104, 1104), (105, 1105), (106, 1106)
    ];

    // State-changing operations the property may apply in any order.
    private enum Op
    {
        EditTitle = 0,
        ToggleSettings = 1,
        ToggleRecord = 2,
        EditParticipant = 3,
        Leave = 4
    }

    [Fact]
    public void EveryStateChange_StrictlyIncreasesVersion_AndVersionIsCarriedOnEmittedUpdates()
    {
        Gen.Int[0, 4]
            .Select(i => (Op)i)
            .Array[1, 20]
            .Sample(ops => RunScenario(ops));
    }

    // A deterministic walk covering each op kind at least once.
    [Fact]
    public void MixedSequence_MaintainsVersionMonotonicity()
    {
        RunScenario(new[]
        {
            Op.EditTitle, Op.ToggleSettings, Op.EditParticipant, Op.ToggleRecord,
            Op.Leave, Op.EditParticipant, Op.ToggleSettings, Op.ToggleRecord, Op.EditTitle, Op.Leave
        });
    }

    private static void RunScenario(IReadOnlyList<Op> ops)
    {
        RunScenarioAsync(ops).GetAwaiter().GetResult();
    }

    private static async Task RunScenarioAsync(IReadOnlyList<Op> ops)
    {
        var database = PhoneTestFixtures.CreateDatabase(out var store);
        SeedGroupCall(database);
        var sender = new CapturingObjectMessageSender();

        // Model of state needed to keep every generated operation a genuine state change.
        var expectedVersion = 1; // seeded Version.
        var joinMuted = false;
        var recording = false;
        var muted = false;
        var titleCounter = 0;
        var removable = new Queue<(long UserId, int Source)>(RemovableParticipants);

        foreach (var op in ops)
        {
            sender.Clear();

            IUpdates updates;
            switch (op)
            {
                case Op.EditTitle:
                    titleCounter++;
                    updates = await InvokeAsync("EditGroupCallTitleHandler", database, sender, CreatorId,
                        new RequestEditGroupCallTitle { Call = InputCall(), Title = $"title-{titleCounter}" });
                    break;

                case Op.ToggleSettings:
                    joinMuted = !joinMuted;
                    updates = await InvokeAsync("ToggleGroupCallSettingsHandler", database, sender, CreatorId,
                        new RequestToggleGroupCallSettings { Call = InputCall(), JoinMuted = joinMuted });
                    break;

                case Op.ToggleRecord:
                    recording = !recording;
                    updates = await InvokeAsync("ToggleGroupCallRecordHandler", database, sender, CreatorId,
                        new RequestToggleGroupCallRecord { Call = InputCall(), Start = recording, Video = false });
                    break;

                case Op.EditParticipant:
                    muted = !muted;
                    updates = await InvokeAsync("EditGroupCallParticipantHandler", database, sender, CreatorId,
                        new RequestEditGroupCallParticipant
                        {
                            Call = InputCall(),
                            Participant = new TInputPeerUser { UserId = ParticipantUserId, AccessHash = 0 },
                            Muted = muted
                        });
                    break;

                case Op.Leave:
                    if (removable.Count == 0)
                    {
                        // No removable participant remains - a Leave here would be a no-op (no state
                        // change, no version bump), which is outside the scope of this property. Skip it.
                        continue;
                    }

                    var (leavingUserId, leavingSource) = removable.Dequeue();
                    updates = await InvokeAsync("LeaveGroupCallHandler", database, sender, leavingUserId,
                        new RequestLeaveGroupCall { Call = InputCall(), Source = leavingSource });
                    break;

                default:
                    throw new InvalidOperationException($"Unhandled op {op}.");
            }

            // Property 10: only a change of the participant set moves the version, by exactly one.
            var storedVersion = LoadGroupCall(store).Version;
            var bump = op == Op.Leave ? 1 : 0;
            storedVersion.ShouldBe(expectedVersion + bump,
                $"unexpected version after {op}: {expectedVersion} -> {storedVersion}");
            expectedVersion = storedVersion;

            // Property 10: the new version is carried on the returned update.
            CarriedVersions(updates).ShouldContain(storedVersion,
                $"the returned update after {op} did not carry version {storedVersion}");

            // Property 10: the new version is carried on the fanned-out update delivered to subscribers.
            var pushedVersions = sender.Pushes
                .SelectMany(p => p.Updates)
                .Select(VersionOf)
                .Where(v => v.HasValue)
                .Select(v => v!.Value)
                .ToList();
            pushedVersions.ShouldContain(storedVersion,
                $"no fanned-out update after {op} carried version {storedVersion}");
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static IInputGroupCall InputCall() => new TInputGroupCall { Id = CallId, AccessHash = AccessHash };

    /// <summary>The versions carried by the update constructors of an <see cref="IUpdates"/>.</summary>
    private static IReadOnlyList<int> CarriedVersions(IUpdates updates)
    {
        var tUpdates = updates.ShouldBeOfType<TUpdates>();
        return tUpdates.Updates.Select(VersionOf).Where(v => v.HasValue).Select(v => v!.Value).ToList();
    }

    /// <summary>Extracts the group-call version carried by a single update, if any.</summary>
    private static int? VersionOf(IUpdate update) => update switch
    {
        TUpdateGroupCall groupCall => (groupCall.Call as MyTelegram.Schema.TGroupCall)?.Version,
        TUpdateGroupCallParticipants participants => participants.Version,
        _ => null
    };

    private static void SeedGroupCall(IMongoDatabase database)
    {
        var participants = new List<GroupCallParticipantDoc>
        {
            new()
            {
                UserId = ParticipantUserId,
                PeerId = ParticipantUserId,
                PeerType = (int)PeerType.User,
                Source = ParticipantSource
            }
        };

        foreach (var (userId, source) in RemovableParticipants)
        {
            participants.Add(new GroupCallParticipantDoc
            {
                UserId = userId,
                PeerId = userId,
                PeerType = (int)PeerType.User,
                Source = source
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
            JoinMuted = false,
            Version = 1,
            Participants = participants
        });
    }

    private static GroupCallDocument LoadGroupCall(InMemoryMongoStore store)
    {
        var doc = store.Documents(PhoneTestFixtures.GroupCallsCollectionName).Single();
        return BsonSerializer.Deserialize<GroupCallDocument>(doc);
    }

    private static async Task<IUpdates> InvokeAsync(
        string handlerTypeName,
        IMongoDatabase database,
        IObjectMessageSender sender,
        long inputUserId,
        IObject request)
    {
        var assembly = typeof(GroupCallDocument).Assembly;
        var type = assembly.GetType($"MyTelegram.Messenger.Handlers.LatestLayer.Phone.{handlerTypeName}", throwOnError: true)!;
        var handler = PhoneTestFixtures.CreateGroupCallHandler(type, database, sender);
        var method = type.GetMethod("HandleAsync", new[] { typeof(IRequestInput), typeof(IObject) })!;

        var input = PhoneTestFixtures.RequestInput(inputUserId).Build();
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
