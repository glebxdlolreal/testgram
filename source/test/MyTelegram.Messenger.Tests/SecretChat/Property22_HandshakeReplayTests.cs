using MyTelegram.Messenger.Tests.TestDoubles;
using EventFlow.Queries;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Domain.Aggregates.Updates;
using MyTelegram.EventFlow.MongoDB;
using MyTelegram.Messenger.Services.Entities;
using MyTelegram.EventFlow.MongoDB.ReadStores;
using MyTelegram.EventFlow.ReadStores;
using MyTelegram.Core;
using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Messenger.Handlers.LatestLayer.Updates;
using MyTelegram.Messenger.Services;
using MyTelegram.Messenger.Services.Caching;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.SecretChat;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.Queries;
using MyTelegram.QueryHandlers.MongoDB.Updates;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Schema.Updates;
using MyTelegram.Services.Services;
// The row type the production query handler is closed over: the MongoDB subclass, not the base impl.
using PersistedUpdatesReadModel = MyTelegram.ReadModel.MongoDB.UpdatesReadModel;

namespace MyTelegram.Messenger.Tests.SecretChat;

/// <summary>
/// Feature: secret-chats, Property 22: an offline device recovers the secret-chat handshake stream from
/// <c>updates.getDifference</c>, and that stream stays scoped to the device it was addressed to.
///
/// <para><b>The property.</b> <c>updateEncryption</c> — the only signal that a secret chat was requested,
/// accepted or discarded — has no qts box of its own. The live push reaches connected sessions only, so
/// without a durable row an offline device never learns the chat exists and
/// <c>SecretChatAccessResolver</c> keeps answering <c>ENCRYPTION_ID_INVALID</c> forever. The row must
/// therefore be persisted, replayed by <c>getDifference</c>, and delivered ONLY to the
/// Authorization_Key it was addressed to: the accept-side <c>encryptedChat</c> carries <c>g_b</c> and
/// <c>key_fingerprint</c>, which is key material for exactly one of the admin's devices.</para>
///
/// <para><b>Why this file exists.</b> The real <see cref="SecretChatUpdateDispatcher"/> had no test at
/// all — every other secret-chat property substitutes it wholesale via
/// <see cref="RecordingUpdateDispatcher"/>, so the persistence half of the fan-out was invisible. It was
/// in fact dead code: rows were stamped <see cref="UpdatesType.Updates"/> with <c>pts = 0</c>, and the
/// pts box filters <c>Pts &gt; MinPts</c>, so every row was dropped for any client with <c>pts >= 1</c>
/// (i.e. all of them), while <c>GetDifferenceHandler</c> declared its <c>userUpdates</c> collection and
/// never assigned it.</para>
///
/// <para><b>How this is tested.</b> Three layers, no mocks of the code under test:
/// the real <see cref="SecretChatUpdateDispatcher"/> over a recording command bus (what gets persisted);
/// the real <see cref="GetUpdatesByGlobalSeqNoQueryHandler"/> over a REAL <c>mongod</c> via
/// <see cref="EmbeddedMongoServer"/> (the device-scoping predicate is a nullable-<c>long</c> comparison,
/// exactly the shape LINQ-to-Mongo can reject at runtime only); and the real
/// <c>GetDifferenceHandler</c> (reached via <c>InternalsVisibleTo</c>) for the shared-cursor clamp.</para>
///
/// <para><b>Validates:</b> the Stage 4 defects of the secret-chat audit — dead offline replay (A) and
/// unenforced device scoping (B).</para>
/// </summary>
public class Property22_HandshakeReplayTests
{
    private const long OwnerId = 3003;
    private const long BoundDevice = 111;
    private const long OtherDevice = 222;

    // ==============================================================================================
    // What the dispatcher persists.
    // ==============================================================================================

    /// <summary>
    /// A device-targeted handshake update is persisted under the <see cref="UpdatesType.EncryptedUpdates"/>
    /// marker, scoped with <c>OnlySendToThisAuthKeyId</c>, and carries <c>pts = 0</c> — matching upstream,
    /// where <c>updateEncryption</c> has no pts. The marker is what keeps the replay query from also
    /// dragging back the <c>pts = 0</c> rows written by ~16 unrelated producers.
    /// </summary>
    [Fact]
    public async Task A_device_targeted_handshake_update_is_persisted_under_the_marker_and_scoped_to_that_device()
    {
        var (dispatcher, commandBus, sender) = BuildDispatcher();

        await dispatcher.PushToDeviceAsync(OwnerId, BoundDevice, UpdateEncryption());

        var command = commandBus.Published.ShouldHaveSingleItem().ShouldBeOfType<CreateUpdatesCommand>();
        command.OwnerPeerId.ShouldBe(OwnerId);
        command.UpdatesType.ShouldBe(UpdatesType.EncryptedUpdates);
        command.Pts.ShouldBe(0);
        command.OnlySendToThisAuthKeyId.ShouldBe(BoundDevice);
        command.ExcludeAuthKeyId.ShouldBeNull();
        command.Updates.ShouldHaveSingleItem().ShouldBeOfType<TUpdateEncryption>();

        // The live push still happens; persistence is in addition to it, not instead of it.
        sender.Pushed.ShouldHaveSingleItem().OnlySendToThisAuthKeyId.ShouldBe(BoundDevice);
    }

    /// <summary>
    /// A broadcast handshake update (the teardown, or a request that no device is bound to yet) is
    /// persisted with <c>ExcludeAuthKeyId</c> carried through, so the device that triggered it does not
    /// replay its own echo.
    /// </summary>
    [Fact]
    public async Task A_broadcast_handshake_update_carries_the_excluded_device_through_to_the_row()
    {
        var (dispatcher, commandBus, _) = BuildDispatcher();

        await dispatcher.PushToAllDevicesAsync(OwnerId, UpdateEncryption(), excludeAuthKeyId: OtherDevice);

        var command = commandBus.Published.ShouldHaveSingleItem().ShouldBeOfType<CreateUpdatesCommand>();
        command.UpdatesType.ShouldBe(UpdatesType.EncryptedUpdates);
        command.ExcludeAuthKeyId.ShouldBe(OtherDevice);
        command.OnlySendToThisAuthKeyId.ShouldBeNull();
    }

    /// <summary>
    /// <c>updateNewEncryptedMessage</c> is recovered from the qts box (<c>encrypted_messages</c>), so it
    /// must NOT also be written to the generic box: after a gap the client would receive it twice, once
    /// per box. A qts on the push is the marker for "this one already has a durable home".
    /// </summary>
    [Fact]
    public async Task An_update_that_already_has_a_qts_box_is_not_persisted_a_second_time()
    {
        var (dispatcher, commandBus, sender) = BuildDispatcher();

        await dispatcher.PushToDeviceAsync(OwnerId, BoundDevice,
            new TUpdateNewEncryptedMessage { Qts = 7, Message = new TEncryptedMessageService() }, qts: 7);

        commandBus.Published.ShouldBeEmpty("the qts box is this update's durable home");
        sender.Pushed.ShouldHaveSingleItem().Qts.ShouldBe(7);
    }

    // ==============================================================================================
    // What the replay query returns — real mongod, real LINQ-to-Mongo translation.
    // ==============================================================================================

    /// <summary>
    /// The replay predicate, decided by MongoDB rather than by LINQ-to-Objects: the marker narrows the
    /// stream, the cursor advances it, and the two nullable auth-key columns scope it to one device.
    /// <para>The zero-blast-radius pin is in here too: a <see cref="UpdatesType.Updates"/> row with
    /// <c>pts = 0</c> — the shape all ~16 unrelated producers write — must never be returned, or wiring
    /// the replay in would dump a user's entire lifetime backlog on their first getDifference.</para>
    /// </summary>
    [RequiresMongoDbFact]
    public async Task The_replay_query_returns_only_marked_rows_addressed_to_the_calling_device()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var collection = UpdatesCollection(mongo.Database);

        await collection.InsertManyAsync(
        [
            // Addressed to the calling device.
            Row(1, OwnerId, UpdatesType.EncryptedUpdates, onlySendTo: BoundDevice),
            // Broadcast to every device of the owner.
            Row(2, OwnerId, UpdatesType.EncryptedUpdates),
            // Broadcast, but the calling device is the one excluded (its own echo).
            Row(3, OwnerId, UpdatesType.EncryptedUpdates, exclude: BoundDevice),
            // Addressed to another device of the SAME user — this is the g_b / key_fingerprint leak.
            Row(4, OwnerId, UpdatesType.EncryptedUpdates, onlySendTo: OtherDevice),
            // Another user's handshake row.
            Row(5, OwnerId + 1, UpdatesType.EncryptedUpdates, onlySendTo: BoundDevice),
            // A generic pts = 0 row: the pre-existing shape the marker deliberately excludes.
            Row(6, OwnerId, UpdatesType.Updates),
            // Excluded device is someone else, so this one IS for the caller.
            Row(7, OwnerId, UpdatesType.EncryptedUpdates, exclude: OtherDevice)
        ]);

        var handler = new GetUpdatesByGlobalSeqNoQueryHandler(RealStore(mongo.Database));

        var rows = await handler.ExecuteQueryAsync(
            new GetUpdatesByGlobalSeqNoQuery(OwnerId, BoundDevice, MinGlobalSeqNo: 0, Limit: 100), default);

        rows.Select(p => p.GlobalSeqNo).ShouldBe([1L, 2L, 7L],
            "only marked rows of this user that are addressed to — and not excluded from — this device");
    }

    /// <summary>
    /// The cursor is exclusive and the page is limited and ordered by <c>GlobalSeqNo</c> ascending —
    /// without the sort a limited page would be an arbitrary subset and the cursor would step over
    /// whatever it did not happen to include.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task The_replay_query_pages_forward_from_the_cursor_in_global_seq_no_order()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var collection = UpdatesCollection(mongo.Database);

        // Seq 1..9 inserted in a scrambled order, so an unsorted query would visibly return them out of
        // order (and a limited unsorted page would return the wrong subset entirely).
        int[] insertionOrder = [7, 3, 9, 1, 5, 8, 2, 6, 4];
        await collection.InsertManyAsync(insertionOrder
            .Select(seq => Row(seq, OwnerId, UpdatesType.EncryptedUpdates, onlySendTo: BoundDevice)));

        var handler = new GetUpdatesByGlobalSeqNoQueryHandler(RealStore(mongo.Database));

        var firstPage = await handler.ExecuteQueryAsync(
            new GetUpdatesByGlobalSeqNoQuery(OwnerId, BoundDevice, MinGlobalSeqNo: 0, Limit: 4), default);

        firstPage.Select(p => p.GlobalSeqNo).ShouldBe([1L, 2L, 3L, 4L], "lowest first, capped at the limit");

        var secondPage = await handler.ExecuteQueryAsync(
            new GetUpdatesByGlobalSeqNoQuery(OwnerId, BoundDevice,
                MinGlobalSeqNo: firstPage.Max(p => p.GlobalSeqNo), Limit: 4), default);

        secondPage.Select(p => p.GlobalSeqNo).ShouldBe([5L, 6L, 7L, 8L], "the cursor is exclusive");
    }

    // ==============================================================================================
    // What updates.getDifference does with the replayed stream.
    // ==============================================================================================

    /// <summary>
    /// End to end through the real <c>GetDifferenceHandler</c>: the replayed handshake updates reach the
    /// caller, and the response is NOT forced into the slice form when neither stream was truncated.
    /// </summary>
    [Fact]
    public async Task Replayed_handshake_updates_reach_the_caller_through_get_difference()
    {
        var fixture = new DifferenceFixture();
        fixture.UserUpdates.Add(ReadModelWith(globalSeqNo: 4, UpdateEncryption()));

        await fixture.InvokeAsync(BoundDevice);

        fixture.Converter.UpdateList.ShouldHaveSingleItem().ShouldBeOfType<TUpdateEncryption>();
        fixture.Converter.UpdatesTruncated.ShouldBeFalse();
        fixture.Ack.Calls.ShouldHaveSingleItem().GlobalSeqNo.ShouldBe(4);
    }

    /// <summary>
    /// A caller with no permanent Authorization_Key is skipped entirely. The predicate compares
    /// <c>ExcludeAuthKeyId != PermAuthKeyId</c>, so an unset key of 0 would match every excluded row —
    /// and a device with no permanent key cannot hold a secret chat in the first place.
    /// </summary>
    [Fact]
    public async Task A_caller_without_a_permanent_auth_key_never_issues_the_replay_query()
    {
        var fixture = new DifferenceFixture();
        fixture.UserUpdates.Add(ReadModelWith(globalSeqNo: 4, UpdateEncryption()));

        await fixture.InvokeAsync(permAuthKeyId: 0);

        fixture.QueryProcessor.Executed.ShouldNotContain(nameof(GetUpdatesByGlobalSeqNoQuery));
        fixture.Converter.UpdateList.ShouldBeEmpty();
    }

    /// <summary>
    /// The regression this clamp exists for. One <c>GlobalSeqNo</c> cursor is shared by two streams that
    /// truncate independently. A FULL channel page means channel rows above its top were cut off; if the
    /// handshake stream's higher seq were advertised as the cursor, every one of those channel updates
    /// would be skipped past permanently.
    /// <para>Re-delivering a handful of <c>updateEncryption</c> rows next round is idempotent, so the
    /// cursor must clamp down to the truncated stream's own maximum.</para>
    /// </summary>
    [Fact]
    public async Task A_truncated_channel_page_caps_the_shared_cursor_at_the_channel_maximum()
    {
        var fixture = new DifferenceFixture();

        // A full channel page (limit is honoured below), topping out well under the handshake stream.
        for (var i = 1; i <= DifferenceFixture.Limit; i++)
        {
            fixture.ChannelUpdates.Add(ReadModelWith(globalSeqNo: i, new TUpdateDeleteMessages()));
        }

        // The handshake stream is short but sits far ahead — the plain max would jump the whole channel tail.
        fixture.UserUpdates.Add(ReadModelWith(globalSeqNo: 9_000, UpdateEncryption()));

        await fixture.InvokeAsync(BoundDevice);

        fixture.Ack.Calls.ShouldHaveSingleItem().GlobalSeqNo.ShouldBe(DifferenceFixture.Limit,
            "the cursor must not step past the cut-off channel tail");
        fixture.Converter.UpdatesTruncated.ShouldBeTrue("a truncated stream must force the slice form");
    }

    /// <summary>
    /// The mirror image: a truncated HANDSHAKE page must not be stepped over by a higher channel seq
    /// either. Symmetric because both streams share one cursor and both are capped by the same limit.
    /// </summary>
    [Fact]
    public async Task A_truncated_handshake_page_caps_the_shared_cursor_at_the_handshake_maximum()
    {
        var fixture = new DifferenceFixture();

        for (var i = 1; i <= DifferenceFixture.Limit; i++)
        {
            fixture.UserUpdates.Add(ReadModelWith(globalSeqNo: i, UpdateEncryption()));
        }

        fixture.ChannelUpdates.Add(ReadModelWith(globalSeqNo: 9_000, new TUpdateDeleteMessages()));

        await fixture.InvokeAsync(BoundDevice);

        fixture.Ack.Calls.ShouldHaveSingleItem().GlobalSeqNo.ShouldBe(DifferenceFixture.Limit);
        fixture.Converter.UpdatesTruncated.ShouldBeTrue();
    }

    // ---- builders ---------------------------------------------------------------------------------

    private static IUpdatesReadModel ReadModelWith(long globalSeqNo, IUpdate update)
    {
        return new StubUpdatesReadModel
        {
            OwnerPeerId = OwnerId,
            UpdatesType = UpdatesType.Updates,
            GlobalSeqNo = globalSeqNo,
            Updates = [update]
        };
    }


    private static (SecretChatUpdateDispatcher Dispatcher, RecordingCommandBus CommandBus,
        RecordingObjectMessageSender Sender) BuildDispatcher()
    {
        var commandBus = new RecordingCommandBus();
        var sender = new RecordingObjectMessageSender();

        return (new SecretChatUpdateDispatcher(sender, commandBus, new FakeIdGenerator()), commandBus, sender);
    }

    private static TUpdateEncryption UpdateEncryption()
    {
        return new TUpdateEncryption
        {
            Chat = new TEncryptedChatDiscarded { Id = 5 },
            Date = 1_700_000_000
        };
    }

    private static IMongoCollection<PersistedUpdatesReadModel> UpdatesCollection(IMongoDatabase database)
    {
        // The collection name the production QueryOnlyReadModelDescriptionProvider derives for this
        // read model (no MongoDbCollectionName attribute => "eventflow-" + lowercased type name).
        return database.GetCollection<PersistedUpdatesReadModel>("eventflow-updatesreadmodel");
    }

    /// <summary>
    /// The PRODUCTION Mongo read-model store, so the predicate under test is translated by the same code
    /// path that serves a live getDifference — including the collection-name derivation.
    /// </summary>
    private static IQueryOnlyReadModelStore<PersistedUpdatesReadModel> RealStore(IMongoDatabase database)
    {
        return new MongoDbQueryOnlyReadModelStore<PersistedUpdatesReadModel>(
            new QueryOnlyReadModelDescriptionProvider(),
            new MongoDbContext(database),
            NullLogger<MongoDbQueryOnlyReadModelStore<PersistedUpdatesReadModel, IMongoDbContext>>.Instance);
    }

    /// <summary>
    /// A row in the shape <c>UpdatesReadModel.ApplyAsync</c> writes. Built as a BSON document and
    /// deserialised by the driver, because every property on the read model has a private setter — which
    /// also means the field names here are pinned to the real persisted schema rather than to a test DTO.
    /// </summary>
    private static PersistedUpdatesReadModel Row(long globalSeqNo,
        long ownerPeerId,
        UpdatesType updatesType,
        long? onlySendTo = null,
        long? exclude = null,
        int pts = 0)
    {
        var document = new BsonDocument
        {
            ["_id"] = $"{ownerPeerId}-{globalSeqNo}-{Guid.NewGuid():N}",
            ["OwnerPeerId"] = ownerPeerId,
            ["ChannelId"] = 0L,
            ["ExcludeAuthKeyId"] = exclude.HasValue ? exclude.Value : BsonNull.Value,
            ["ExcludeUserId"] = BsonNull.Value,
            ["OnlySendToUserId"] = BsonNull.Value,
            ["OnlySendToThisAuthKeyId"] = onlySendTo.HasValue ? onlySendTo.Value : BsonNull.Value,
            ["UpdatesType"] = (int)updatesType,
            ["MessageId"] = BsonNull.Value,
            ["Pts"] = pts,
            ["Date"] = 1_700_000_000,
            ["GlobalSeqNo"] = globalSeqNo,
            ["Updates"] = BsonNull.Value,
            ["Users"] = BsonNull.Value,
            ["Chats"] = BsonNull.Value
        };

        return MongoDB.Bson.Serialization.BsonSerializer.Deserialize<PersistedUpdatesReadModel>(document);
    }
}

/// <summary>
/// Captures what <see cref="SecretChatUpdateDispatcher"/> hands to the live transport. Only the
/// addressing arguments matter here; the persistence half is asserted through the command bus.
/// </summary>
internal sealed record PushedObject(Peer Peer, IObject Data, long? ExcludeAuthKeyId,
    long? OnlySendToThisAuthKeyId, int? Qts);

internal sealed class RecordingObjectMessageSender : IObjectMessageSender
{
    public List<PushedObject> Pushed { get; } = [];

    public Task PushMessageToPeerAsync<TData>(Peer peer,
        TData data,
        long? excludeAuthKeyId = null,
        long? excludeUserId = null,
        long? onlySendToUserId = null,
        long? onlySendToThisAuthKeyId = null,
        int pts = 0,
        int? qts = null,
        long globalSeqNo = 0,
        PushData? pushData = null,
        List<long>? excludeUserIds = null) where TData : IObject
    {
        Pushed.Add(new PushedObject(peer, data, excludeAuthKeyId, onlySendToThisAuthKeyId, qts));

        return Task.CompletedTask;
    }

    // The dispatcher uses PushMessageToPeerAsync only; the rest of the (large) transport surface is not
    // reachable from the code under test, so reaching it is a test-authoring bug rather than a scenario.
    public Task PushSessionMessageToAuthKeyIdAsync<TData>(long authKeyId, TData data, int pts = 0, int? qts = null,
        long globalSeqNo = 0) where TData : IObject => throw new NotSupportedException();

    public Task SendFileDataToPeerAsync<TData>(RequestInfo requestInfo, TData data) where TData : IObject =>
        throw new NotSupportedException();

    public Task SendMessageToPeerAsync<TData>(RequestInfo requestInfo, TData data) where TData : IObject =>
        throw new NotSupportedException();

    public Task SendRpcMessageToClientAsync<TData>(RequestInfo requestInfo, TData data, int pts = 0)
        where TData : IObject => throw new NotSupportedException();

    public Task SendRpcMessageToClientAsync<TData>(string connectionId, long tempAuthKeyId, long sessionId,
        long reqMsgId, TData data, int pts = 0, long permAuthKeyId = 0) where TData : IObject =>
        throw new NotSupportedException();

    public Task SendRpcMessageToClientAsync<TData>(RequestInfo requestInfo, TData data, long authKeyId,
        long permAuthKeyId, long userId, int pts = 0) where TData : IObject => throw new NotSupportedException();
}

/// <summary>An <see cref="IUpdatesReadModel"/> with settable properties (the real one has private setters).</summary>
internal sealed class StubUpdatesReadModel : IUpdatesReadModel
{
    public long OwnerPeerId { get; set; }
    public long ChannelId { get; set; }
    public long? ExcludeAuthKeyId { get; set; }
    public long? ExcludeUserId { get; set; }
    public long? OnlySendToUserId { get; set; }
    public long? OnlySendToThisAuthKeyId { get; set; }
    public UpdatesType UpdatesType { get; set; }
    public int? MessageId { get; set; }
    public int Pts { get; set; }
    public int Date { get; set; }
    public long GlobalSeqNo { get; set; }
    public IList<IUpdate>? Updates { get; set; }
    public List<long>? Users { get; set; }
    public List<long>? Chats { get; set; }
}

/// <summary>
/// Drives the REAL <c>GetDifferenceHandler</c> (internal, reached via <c>InternalsVisibleTo</c>) with the
/// two replayed streams under test supplied directly, so the assertions are about the handler's own
/// cursor arithmetic and truncation reporting rather than about the query handlers underneath it.
/// </summary>
internal sealed class DifferenceFixture
{
    /// <summary>The handler clamps pts_total_limit to this, so a "full page" is exactly this many rows.</summary>
    public const int Limit = MyTelegramConsts.DefaultPtsTotalLimit;

    public List<IUpdatesReadModel> UserUpdates { get; } = [];
    public List<IUpdatesReadModel> ChannelUpdates { get; } = [];
    public RecordingAckCacheService Ack { get; } = new();
    public RecordingDifferenceConverterService Converter { get; } = new();
    public DifferenceQueryProcessor QueryProcessor { get; }

    public DifferenceFixture()
    {
        QueryProcessor = new DifferenceQueryProcessor(UserUpdates, ChannelUpdates);
    }

    public async Task InvokeAsync(long permAuthKeyId)
    {
        var handler = new GetDifferenceHandler(new NoBotQtsCounter(),
            new StubMessageAppService(),
            new StubPtsHelper(),
            QueryProcessor,
            Ack,
            Converter,
            new InMemorySecretChatMessageStore());

        await handler.HandleAsync(SecretChatTestHarness.Input(3003, permAuthKeyId),
            new RequestGetDifference { Pts = 1, Date = 1_700_000_000, Qts = 0 });
    }
}

internal sealed record AckCall(int Pts, long GlobalSeqNo, Peer ToPeer, bool IsFromGetDifference);

internal sealed class RecordingAckCacheService : IAckCacheService
{
    public List<AckCall> Calls { get; } = [];

    public Task AddRpcPtsToCacheAsync(long reqMsgId, int pts, long globalSeqNo, Peer toPeer,
        bool isFromGetDifference = false)
    {
        Calls.Add(new AckCall(pts, globalSeqNo, toPeer, isFromGetDifference));

        return Task.CompletedTask;
    }

    public Task AddMsgIdToCacheAsync(long msgId, int ptsOrQts, long globalSeqNo, Peer toPeer, bool isQts = false) =>
        Task.CompletedTask;

    public void AddRpcMsgIdToCache(long msgId, long reqMsgId)
    {
    }

    public bool TryGetPts(long msgId, out AckCacheItem? ackCacheItem)
    {
        ackCacheItem = null;

        return false;
    }

    public bool TryGetRpcPtsCache(long msgId, out AckCacheItem? ackRpcCacheItem)
    {
        ackRpcCacheItem = null;

        return false;
    }
}

/// <summary>
/// Captures the arguments the handler computes. The real converter is covered by its own tests; what is
/// under test here is WHICH updates and WHICH truncation flag reach it.
/// </summary>
internal sealed class RecordingDifferenceConverterService : IDifferenceConverterService
{
    public IList<IUpdate> UpdateList { get; private set; } = [];
    public bool UpdatesTruncated { get; private set; }
    public bool EncryptedMessagesTruncated { get; private set; }
    public int SecretChatQts { get; private set; }

    public IDifference ToDifference(IRequestWithAccessHashKeyId request,
        GetMessageOutput output,
        IPtsReadModel? pts,
        int cachedPts,
        int limit,
        IList<IUpdate> updateList,
        IList<IChat> chatListFromUpdates,
        IReadOnlyCollection<IEncryptedMessageReadModel>? encryptedMessageReadModels,
        int secretChatQts = 0,
        bool encryptedMessagesTruncated = false,
        bool updatesTruncated = false,
        int layer = 0)
    {
        UpdateList = updateList;
        UpdatesTruncated = updatesTruncated;
        EncryptedMessagesTruncated = encryptedMessagesTruncated;
        SecretChatQts = secretChatQts;

        return new TDifferenceEmpty();
    }

    public IChannelDifference ToChannelDifference(IRequestWithAccessHashKeyId request,
        GetMessageOutput output,
        bool isChannelMember,
        IList<IUpdate> updatesList,
        int updatesMaxPts = 0,
        bool resetLeftToFalse = false,
        int timeoutSeconds = 30,
        int layer = 0) => throw new NotSupportedException();
}

/// <summary>
/// Answers exactly the queries <c>GetDifferenceHandler</c> issues, and records which ones were reached so
/// a test can assert that the replay query was SKIPPED. Unknown queries throw rather than returning a
/// default, so the fixture cannot silently drift from the handler.
/// </summary>
internal sealed class DifferenceQueryProcessor(
    List<IUpdatesReadModel> userUpdates,
    List<IUpdatesReadModel> channelUpdates) : IQueryProcessor
{
    public List<string> Executed { get; } = [];

    public Task<TResult> ProcessAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken)
    {
        Executed.Add(query.GetType().Name);

        object result = query switch
        {
            GetPtsByPeerIdQuery => null!,
            GetPtsByPermAuthKeyIdQuery => null!,
            GetChannelIdListByMemberUserIdQuery => (IReadOnlyCollection<long>)[],
            GetUpdatesByGlobalSeqNoQuery q => (IReadOnlyCollection<IUpdatesReadModel>)userUpdates
                .Where(p => p.GlobalSeqNo > q.MinGlobalSeqNo)
                .Take(q.Limit)
                .ToList(),
            GetUpdatesQuery => (IReadOnlyCollection<IUpdatesReadModel>)[],
            GetChannelUpdatesByGlobalSeqNoQuery q => (IReadOnlyCollection<IUpdatesReadModel>)channelUpdates
                .Where(p => p.GlobalSeqNo > q.MinGlobalSeqNo)
                .Take(q.Limit)
                .ToList(),
            _ => throw new NotSupportedException($"Unexpected query type {query.GetType().Name}")
        };

        return Task.FromResult((TResult)result);
    }
}

internal sealed class StubPtsHelper : IPtsHelper
{
    public int GetCachedPts(long ownerId) => 0;

    public Task<int> IncrementPtsAsync(long ownerId, int currentPts, int ptsCount = 1, long permAuthKeyId = 0,
        int newUnreadCount = 0) => throw new NotSupportedException();

    public Task<int> IncrementQtsAsync(long ownerId, int currentQts, int qtsCount = 1, long permAuthKeyId = 0) =>
        throw new NotSupportedException();

    // Fully qualified: MyTelegram.Services.Services also declares a PtsCacheItem, and both namespaces are
    // in scope here. The global:: prefix is required because the enclosing namespace is
    // MyTelegram.Messenger.Tests, so a bare "Messenger.Services..." binds relative to that.
    public Task<global::MyTelegram.Messenger.Services.Caching.PtsCacheItem> GetPtsForUserAsync(long userId) =>
        throw new NotSupportedException();

    public Task<global::MyTelegram.Messenger.Services.Caching.PtsCacheItem> GetPtsForAuthKeyIdAsync(long userId,
        long permAuthKeyId) => throw new NotSupportedException();

    public Task<bool> UpdatePtsForAuthKeyIdAsync(long userId, long permAuthKeyId, int pts, bool forceUpdate) =>
        throw new NotSupportedException();

    public Task SyncCachedPtsToReadModelAsync(long ownerId) => throw new NotSupportedException();
}

/// <summary>
/// Only <c>GetChannelDifferenceAsync</c> is on the path under test, and its result feeds the converter —
/// which is itself recorded — so an empty output is enough. Everything else throws, so a future change to
/// the handler that starts depending on the message layer cannot pass silently.
/// </summary>
internal sealed class StubMessageAppService : IMessageAppService
{
    public Task<GetMessageOutput> GetChannelDifferenceAsync(GetDifferenceInput input) =>
        Task.FromResult(new GetMessageOutput());

    public void CheckBotPermission(long requestUserId, Peer toPeer) => throw new NotSupportedException();

    public Task CheckSendAsAsync(long requestUserId, Peer toPeer, Peer? sendAs) => throw new NotSupportedException();

    public Task<Peer?> GetAnonymousSendAsPeerAsync(long channelId, long userId) => throw new NotSupportedException();

    public Task<GetMessageOutput> GetDifferenceAsync(GetDifferenceInput input) => throw new NotSupportedException();

    public Task<GetMessageOutput> GetHistoryAsync(GetHistoryInput input) => throw new NotSupportedException();

    public Task<GetMessageOutput> GetMessagesAsync(GetMessagesInput input) => throw new NotSupportedException();

    public Task<GetMessageOutput> GetRepliesAsync(GetRepliesInput input) => throw new NotSupportedException();

    public Task<GetMessageOutput> SearchAsync(SearchInput input) => throw new NotSupportedException();

    public Task<GetMessageOutput> SearchGlobalAsync(SearchGlobalInput input) => throw new NotSupportedException();

    public Task SendMessageAsync(List<SendMessageInput> inputs) => throw new NotSupportedException();

    public Task<SearchPostsResult> SearchPostsAsync(long selfUserId, SearchPostsQuery searchPostsQuery) =>
        throw new NotSupportedException();

    public (HashSet<long> userIds, HashSet<long> channelIds) GetExtraPeerIds(
        IReadOnlyCollection<IMessageReadModel> messageReadModels) => throw new NotSupportedException();

    public Task<bool> CanSendAsPeerAsync(long channelId, long userId) => throw new NotSupportedException();

    public Task<MessageEntityProcessingResult> ProcessMessageEntitiesAsync(string? message,
        IEnumerable<IMessageEntity>? entities,
        Peer toPeer) => throw new NotSupportedException();

    public List<string> GetHashtags(string? message) => throw new NotSupportedException();

    public Task<bool> IsValidSendAsPeerAsync(long requestUserId, Peer toPeer, Peer? sendAsPeer) =>
        throw new NotSupportedException();
}
