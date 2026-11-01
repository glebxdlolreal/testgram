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
/// Handler-level tests for the RTMP / livestream group-call handlers
/// (<c>GetGroupCallStreamRtmpUrlHandler</c>, <c>GetGroupCallStreamChannelsHandler</c>).
///
/// Covers:
///   * Requirement 25.1 - RTMP <c>url</c> / <c>key</c> retrieval.
///   * Requirement 25.2 - stream-key rotation when <c>revoke</c> is set.
///   * Requirement 25.3 - stream-channel listing (channel / scale / last-timestamp).
///   * Requirement 25.5 / 25.6 / 25.7 - the <c>PEER_ID_INVALID</c>, <c>GROUPCALL_INVALID</c>,
///     and <c>GROUPCALL_JOIN_MISSING</c> error paths.
///
/// RTMP livestreams are always attached to a channel peer, so these tests drive the handler with a
/// channel peer (the channel-admin check is satisfied via a mocked <see cref="IChannelAdminRightsChecker"/>).
/// </summary>
public class StreamHandlerTests
{
    private const long AdminUserId = 1;
    private const long OutsiderUserId = 99;
    private const long ChannelId = 555;
    private const long CallId = 800;
    private const long AccessHash = 24680;

    // ---- RTMP URL retrieval : R25.1 --------------------------------------------------------------

    [Fact]
    public async Task GetRtmpUrl_ReturnsUrlAndKeyAndPersistsCredentialOnCall()
    {
        var database = PhoneTestFixtures.CreateDatabase(out var store);
        SeedRtmpGroupCall(database, rtmpStreamKey: "seed-key");

        var request = new RequestGetGroupCallStreamRtmpUrl { Peer = ChannelPeer(), Revoke = false };
        var result = await InvokeAsync(CreateRtmpHandler(database), AdminUserId, request);

        // R25.1: an RTMP url + key is returned; the existing key is preserved when not revoking.
        var rtmpUrl = result.ShouldBeOfType<TGroupCallStreamRtmpUrl>();
        rtmpUrl.Url.ShouldNotBeNullOrWhiteSpace();
        rtmpUrl.Key.ShouldBe("seed-key");

        // R25.1: the returned credential is persisted on the group call so later reads are consistent.
        var stored = LoadGroupCall(store);
        stored.RtmpStreamKey.ShouldBe("seed-key");
        stored.RtmpUrl.ShouldBe(rtmpUrl.Url);
    }

    [Fact]
    public async Task GetRtmpUrl_NonRevoke_ReturnsStableKeyAcrossCalls()
    {
        var database = PhoneTestFixtures.CreateDatabase(out _);
        SeedRtmpGroupCall(database, rtmpStreamKey: "stable-key");

        var first = (TGroupCallStreamRtmpUrl)await InvokeAsync(
            CreateRtmpHandler(database), AdminUserId,
            new RequestGetGroupCallStreamRtmpUrl { Peer = ChannelPeer(), Revoke = false });
        var second = (TGroupCallStreamRtmpUrl)await InvokeAsync(
            CreateRtmpHandler(database), AdminUserId,
            new RequestGetGroupCallStreamRtmpUrl { Peer = ChannelPeer(), Revoke = false });

        // R25.1: repeated retrieval without revoke returns the same (stable) stream key.
        first.Key.ShouldBe("stable-key");
        second.Key.ShouldBe(first.Key);
    }

    // ---- revoke rotation : R25.2 -----------------------------------------------------------------

    [Fact]
    public async Task GetRtmpUrl_Revoke_RotatesStreamKey()
    {
        var database = PhoneTestFixtures.CreateDatabase(out var store);
        SeedRtmpGroupCall(database, rtmpStreamKey: "original-key");

        var beforeRevoke = (TGroupCallStreamRtmpUrl)await InvokeAsync(
            CreateRtmpHandler(database), AdminUserId,
            new RequestGetGroupCallStreamRtmpUrl { Peer = ChannelPeer(), Revoke = false });
        beforeRevoke.Key.ShouldBe("original-key");

        var afterRevoke = (TGroupCallStreamRtmpUrl)await InvokeAsync(
            CreateRtmpHandler(database), AdminUserId,
            new RequestGetGroupCallStreamRtmpUrl { Peer = ChannelPeer(), Revoke = true });

        // R25.2: revoking produces a new (non-empty) key that differs from the previous one.
        afterRevoke.Key.ShouldNotBeNullOrWhiteSpace();
        afterRevoke.Key.ShouldNotBe(beforeRevoke.Key);

        // R25.2: the rotated key replaces the old credential on the group call.
        LoadGroupCall(store).RtmpStreamKey.ShouldBe(afterRevoke.Key);
    }

    [Fact]
    public async Task GetRtmpUrl_InvalidPeer_ThrowsPeerIdInvalid()
    {
        var database = PhoneTestFixtures.CreateDatabase(out _);

        // A null peer cannot be resolved -> PEER_ID_INVALID (R25.5).
        var request = new RequestGetGroupCallStreamRtmpUrl { Peer = null!, Revoke = false };
        var ex = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync(CreateRtmpHandler(database), AdminUserId, request));
        ex.Message.ShouldBe("PEER_ID_INVALID");
    }

    // ---- stream channels : R25.3 -----------------------------------------------------------------

    [Fact]
    public async Task GetStreamChannels_ReturnsChannelScaleAndTimestamp()
    {
        var database = PhoneTestFixtures.CreateDatabase(out _);
        SeedRtmpGroupCall(database, rtmpStreamKey: "key");

        var hls = new Mock<IHlsGroupCallStreamService>();
        hls.Setup(x => x.GetChannelsAsync(It.IsAny<GroupCallDocument>()))
            .ReturnsAsync(new List<HlsGroupCallStreamChannel> { new(1, 0, 1_700_000_000_000) });

        var request = new RequestGetGroupCallStreamChannels { Call = InputCall() };
        var result = await InvokeAsync(CreateChannelsHandler(database, hls.Object), AdminUserId, request);

        // R25.3: the channel / scale / last-timestamp are returned to the joined user.
        var channels = result.ShouldBeOfType<TGroupCallStreamChannels>();
        var channel = channels.Channels.ShouldHaveSingleItem().ShouldBeOfType<TGroupCallStreamChannel>();
        channel.Channel.ShouldBe(1);
        channel.Scale.ShouldBe(0);
        channel.LastTimestampMs.ShouldBe(1_700_000_000_000);
    }

    [Fact]
    public async Task GetStreamChannels_UnknownCall_ThrowsGroupCallInvalid()
    {
        var database = PhoneTestFixtures.CreateDatabase(out _);
        var hls = new Mock<IHlsGroupCallStreamService>();

        var request = new RequestGetGroupCallStreamChannels { Call = InputCall() };
        var ex = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync(CreateChannelsHandler(database, hls.Object), AdminUserId, request));
        ex.Message.ShouldBe("GROUPCALL_INVALID"); // R25.6
    }

    [Fact]
    public async Task GetStreamChannels_NotJoined_ThrowsGroupCallJoinMissing()
    {
        var database = PhoneTestFixtures.CreateDatabase(out _);
        // Call exists but the requesting user is neither the creator nor a participant.
        SeedRtmpGroupCall(database, rtmpStreamKey: "key");
        var hls = new Mock<IHlsGroupCallStreamService>();

        var request = new RequestGetGroupCallStreamChannels { Call = InputCall() };
        var ex = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync(CreateChannelsHandler(database, hls.Object), OutsiderUserId, request));
        ex.Message.ShouldBe("GROUPCALL_JOIN_MISSING"); // R25.7
    }

    [Fact]
    public async Task GetStreamChannels_OfACallThatIsNotAnRtmpStream_IsGroupCallInvalid()
    {
        // Telegram 2026-11-01: an ordinary channel video chat has no stream channels at all.
        var database = PhoneTestFixtures.CreateDatabase(out _);
        SeedRtmpGroupCall(database, rtmpStreamKey: "key", rtmpStream: false);
        var hls = new Mock<IHlsGroupCallStreamService>();

        var request = new RequestGetGroupCallStreamChannels { Call = InputCall() };
        var ex = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync(CreateChannelsHandler(database, hls.Object), AdminUserId, request));
        ex.Message.ShouldBe("GROUPCALL_INVALID");
        hls.Verify(x => x.GetChannelsAsync(It.IsAny<GroupCallDocument>()), Times.Never);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static IInputGroupCall InputCall() => new TInputGroupCall { Id = CallId, AccessHash = AccessHash };

    private static IInputPeer ChannelPeer() => new TInputPeerChannel { ChannelId = ChannelId, AccessHash = 0 };

    private static void SeedRtmpGroupCall(IMongoDatabase database, string? rtmpStreamKey, bool rtmpStream = true)
    {
        var collection = database.GetCollection<GroupCallDocument>(PhoneTestFixtures.GroupCallsCollectionName);
        collection.InsertOne(new GroupCallDocument
        {
            Id = CallId,
            CallId = CallId,
            AccessHash = AccessHash,
            CreatorId = AdminUserId,
            PeerId = ChannelId,
            PeerType = (int)PeerType.Channel,
            Active = true,
            RtmpStream = rtmpStream,
            RtmpStreamKey = rtmpStreamKey,
            Version = 1,
            Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        });
    }

    private static GroupCallDocument LoadGroupCall(InMemoryMongoStore store)
    {
        var doc = store.Documents(PhoneTestFixtures.GroupCallsCollectionName).Single();
        return BsonSerializer.Deserialize<GroupCallDocument>(doc);
    }

    private static object CreateRtmpHandler(IMongoDatabase database)
    {
        var options = new Mock<IOptionsMonitor<MyTelegramMessengerServerOptions>>();
        options.Setup(o => o.CurrentValue).Returns(new MyTelegramMessengerServerOptions());

        var adminRightsChecker = new Mock<IChannelAdminRightsChecker>();
        adminRightsChecker
            .Setup(x => x.CheckAdminRightAsync(
                It.IsAny<long>(),
                It.IsAny<long>(),
                It.IsAny<Func<ChatAdminRights, bool>>(),
                It.IsAny<RpcError?>()))
            .Returns(Task.CompletedTask);

        return CreateHandler(
            "GetGroupCallStreamRtmpUrlHandler",
            database,
            new PeerHelper(),
            options.Object,
            adminRightsChecker.Object);
    }

    private static object CreateChannelsHandler(IMongoDatabase database, IHlsGroupCallStreamService hlsService)
        => CreateHandler("GetGroupCallStreamChannelsHandler", database, hlsService);

    private static object CreateHandler(string handlerTypeName, params object[] args)
    {
        var assembly = typeof(GroupCallDocument).Assembly;
        var type = assembly.GetType($"MyTelegram.Messenger.Handlers.LatestLayer.Phone.{handlerTypeName}", throwOnError: true)!;
        return PhoneTestFixtures.CreateGroupCallHandler(type, args);
    }

    private static async Task<IObject> InvokeAsync(object handler, long userId, IObject request)
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
        return ((TRpcResult)result).Result;
    }
}
