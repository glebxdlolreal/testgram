using System.Buffers;
using EventFlow.Queries;
using FsCheck;
using FsCheck.Xunit;
using MyTelegram;
using MyTelegram.Abstractions;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Stats;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Feature: stats-api, Property 1: Access checks are evaluated in a fixed order and return the first
/// failure.
///
/// For any stats request that violates an arbitrary subset of the ordered access checks
/// (caller-type rejection -> target resolution -> channel-kind -> joinability -> admin rights), the
/// handler returns the RPC error of the earliest violated check in that order; and for any request that
/// violates none of the checks, no access error is raised.
///
/// Validates: Requirements 1.3, 1.4, 1.5, 2.4, 3.4.
///
/// The shared <see cref="StatsGen.AccessCase"/> generator toggles each violable condition — caller type
/// (user/bot/anonymous), target resolution, required channel kind vs. actual kind, joinability
/// (private channel + non-member), and admin rights — independently, so a single property exercises the
/// full lattice of overlapping violations. The production <see cref="StatsAccessController"/> is driven
/// through hand-written in-memory fakes for its four collaborators (no MongoDB, matching the tasks.md
/// testing notes); the expected outcome is computed independently from the fixed precedence order and
/// compared against the RPC error the controller actually raises. Each run executes a minimum of 100
/// generated cases.
/// </summary>
[Properties(Arbitrary = new[] { typeof(StatsArbitraries) }, MaxTest = 100)]
public class OrderedAccessChecksPropertyTests
{
    [Property]
    public void Access_checks_return_the_first_violated_check_in_fixed_order(StatsAccessCaseFixture @case)
    {
        var channelAppService = new FakeChannelAppService(@case);
        var adminChecker = new FakeChannelAdminRightsChecker(@case);
        var peerHelper = new FakePeerHelper(@case);
        var controller = new StatsAccessController(
            channelAppService,
            adminChecker,
            peerHelper,
            new UnusedQueryProcessor());

        var input = BuildInput(@case);
        var requiredKind = @case.RequiredKind == StatsChannelKindFixture.Broadcast
            ? StatsChannelKind.BroadcastOnly
            : StatsChannelKind.MegagroupOnly;
        var inputChannel = new TInputChannel { ChannelId = @case.Channel?.ChannelId ?? 12345, AccessHash = 0 };

        var expectedError = ExpectedFirstError(@case, requiredKind);

        if (expectedError is null)
        {
            // No check is violated: the controller resolves and returns the channel without raising.
            var resolved = controller
                .ResolveChannelForStatsAsync(input, inputChannel, requiredKind, @case.CheckJoinable)
                .GetAwaiter().GetResult();

            resolved.ShouldNotBeNull();
            resolved.ChannelId.ShouldBe(@case.Channel!.ChannelId);
        }
        else
        {
            var ex = Should.Throw<RpcException>(() => controller
                .ResolveChannelForStatsAsync(input, inputChannel, requiredKind, @case.CheckJoinable)
                .GetAwaiter().GetResult());

            ex.RpcError.Message.ShouldBe(expectedError);
        }
    }

    /// <summary>
    /// Computes the RPC error string of the earliest violated check, independently of the controller,
    /// following the fixed order of Requirement 1.5: caller-type rejection -> target resolution ->
    /// channel-kind -> joinability -> admin rights. Returns <c>null</c> when no check is violated.
    /// </summary>
    private static string? ExpectedFirstError(StatsAccessCaseFixture @case, StatsChannelKind requiredKind)
    {
        // (1) Caller-type rejection: bot or anonymous callers are rejected with BOT_METHOD_INVALID.
        if (@case.Caller is CallerKindFixture.Bot or CallerKindFixture.Anonymous)
        {
            return "BOT_METHOD_INVALID";
        }

        // (2) Target resolution: an unresolved channel yields CHANNEL_INVALID.
        if (!@case.TargetResolves)
        {
            return "CHANNEL_INVALID";
        }

        var channel = @case.Channel!;

        // (3) Channel-kind check.
        if (requiredKind == StatsChannelKind.BroadcastOnly && !channel.IsBroadcast)
        {
            return "BROADCAST_REQUIRED";
        }

        if (requiredKind == StatsChannelKind.MegagroupOnly && !channel.IsMegagroup)
        {
            return "MEGAGROUP_REQUIRED";
        }

        // (4) Joinability (broadcast stats only): a private channel requires membership.
        if (@case.CheckJoinable && !channel.IsPublic && !@case.CallerIsMember)
        {
            return "CHANNEL_PRIVATE";
        }

        // (5) Admin rights.
        if (!@case.CallerIsAdmin)
        {
            return "CHAT_ADMIN_REQUIRED";
        }

        return null;
    }

    private static TestRequestInput BuildInput(StatsAccessCaseFixture @case)
    {
        // Anonymous connections carry no authenticated user (UserId <= 0); user/bot callers carry a
        // positive user id (the bot flag is surfaced through IPeerHelper.IsBotUser).
        var userId = @case.Caller == CallerKindFixture.Anonymous ? 0 : @case.CallerUserId;
        return new TestRequestInput(userId);
    }

    // ---- In-memory collaborators -------------------------------------------------------------

    /// <summary>
    /// Fake channel app service: <see cref="GetAsync(long?)"/> returns a populated read model exactly when
    /// the case's target resolves, and <see cref="IsChannelMemberAsync"/> reports the case's membership
    /// toggle. Only the members the Access_Controller calls are implemented.
    /// </summary>
    private sealed class FakeChannelAppService(StatsAccessCaseFixture @case) : IChannelAppService
    {
        public Task<IChannelReadModel?> GetAsync(long? id)
        {
            if (!@case.TargetResolves || @case.Channel is null)
            {
                return Task.FromResult<IChannelReadModel?>(null);
            }

            return Task.FromResult<IChannelReadModel?>(new FakeChannelReadModel(@case.Channel));
        }

        public Task<bool> IsChannelMemberAsync(long userId, long channelId) =>
            Task.FromResult(@case.CallerIsMember);

        public Task<IChannelReadModel> GetAsync(long id) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<IChannelReadModel>> GetListAsync(IEnumerable<long> ids) =>
            throw new NotSupportedException();
        public Task<IChannelFullReadModel?> GetChannelFullAsync(long channelId) => throw new NotSupportedException();
        public Task<bool> SendRpcErrorIfNotChannelMemberAsync(IRequestInput input, IChannelReadModel channelReadModel) =>
            throw new NotSupportedException();
        public Task<bool> SendRpcErrorIfNotChannelMemberAsync(IRequestInput input, long channelId) =>
            throw new NotSupportedException();
        public Task<bool> SendRpcErrorIfNoReadAccessAsync(IRequestInput input, IChannelReadModel channelReadModel) =>
            throw new NotSupportedException();
        public Task<bool> HasReadAccessAsync(long userId, IChannelReadModel channelReadModel) =>
            throw new NotSupportedException();

        public void InvalidateCache(long channelId) => throw new NotSupportedException();
    }

    /// <summary>Fake admin-rights checker returning the case's admin toggle.</summary>
    private sealed class FakeChannelAdminRightsChecker(StatsAccessCaseFixture @case) : IChannelAdminRightsChecker
    {
        public Task<bool> HasChatAdminRightAsync(long channelId, long userId, Func<ChatAdminRights, bool> checkAdminRightsFunc) =>
            Task.FromResult(@case.CallerIsAdmin);

        public Task CheckAdminRightAsync(IInputChannel channel, long userId, Func<ChatAdminRights, bool> checkAdminRightsFunc, RpcError? rpcError = null) =>
            throw new NotSupportedException();
        public Task CheckAdminRightAsync(long channelId, long userId, Func<ChatAdminRights, bool> checkAdminRightsFunc, RpcError? rpcError = null) =>
            throw new NotSupportedException();
        public Task ThrowIfNotChannelOwnerAsync(IInputChannel channel, long userId) => throw new NotSupportedException();
        public Task ThrowIfNotChannelOwnerAsync(long channelId, long userId) => throw new NotSupportedException();
        public long? GetChannelId(IInputChannel channel) => throw new NotSupportedException();
    }

    /// <summary>Fake peer helper: reports bot status for bot callers; nothing else is exercised here.</summary>
    private sealed class FakePeerHelper(StatsAccessCaseFixture @case) : IPeerHelper
    {
        public bool IsBotUser(long userId) => @case.Caller == CallerKindFixture.Bot;

        public Peer GetChannel(IInputChannel channel) => throw new NotSupportedException();
        public Peer? GetPeer(IInputPeer? peer, long selfUserId = 0) => throw new NotSupportedException();
        public Peer GetPeer(IInputUser userPeer, long selfUserId = 0) => throw new NotSupportedException();
        public PeerType GetPeerType(long peerId) => throw new NotSupportedException();
        public bool IsChannelPeer(long peerId) => throw new NotSupportedException();
        public bool IsUserPeer(long peerId) => throw new NotSupportedException();
        public IPeer ToPeer(Peer peer) => throw new NotSupportedException();
        public IPeer ToPeer(PeerType peerType, long peerId) => throw new NotSupportedException();
        public Peer GetPeer(long peerId) => throw new NotSupportedException();
        public bool IsEncryptedDialogPeer(long peerId) => throw new NotSupportedException();
    }

    /// <summary>A query processor the channel-resolution path never calls; present only to satisfy the ctor.</summary>
    private sealed class UnusedQueryProcessor : IQueryProcessor
    {
        public Task<TResult> ProcessAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// Minimal <see cref="IRequestInput"/> carrying only the authenticated user id the Access_Controller
    /// reads; all other members are inert defaults.
    /// </summary>
    private sealed class TestRequestInput(long userId) : IRequestInput
    {
        public long UserId { get; } = userId;
        public string ConnectionId => string.Empty;
        public ConnectionType ConnectionType => default;
        public long AuthKeyId => 0;
        public uint ObjectId { get; set; }
        public long PermAuthKeyId => 0;
        public long ReqMsgId => 0;
        public int SeqNumber => 0;
        public Guid RequestId => Guid.Empty;
        public long Date => 0;
        public DeviceType DeviceType { get; set; }
        public string ClientIp => string.Empty;
        public long SessionId => 0;
        public long AccessHashKeyId { get; set; }
        public int Layer { get; set; }
    }

    /// <summary>
    /// A settable <see cref="IChannelReadModel"/> for tests (the production read model exposes only private
    /// setters). Only the fields the Access_Controller reads — <see cref="Broadcast"/>,
    /// <see cref="MegaGroup"/>, <see cref="UserName"/>, <see cref="ChannelId"/> — carry meaningful values;
    /// the remainder are inert defaults.
    /// </summary>
    private sealed class FakeChannelReadModel : IChannelReadModel
    {
        public FakeChannelReadModel(StatsChannelFixture channel)
        {
            ChannelId = channel.ChannelId;
            Id = channel.ChannelId.ToString();
            Broadcast = channel.IsBroadcast;
            MegaGroup = channel.IsMegagroup;
            UserName = channel.UserName;
            CreatorId = channel.CreatorId;
            // Admin membership is decided by the injected IChannelAdminRightsChecker fake, so the concrete
            // admin list is irrelevant to this property and left empty.
            AdminList = new List<ChatAdmin>();
            ParticipantsCount = channel.ParticipantsCount;
        }

        public long ChannelId { get; }
        public string Id { get; }
        public bool Broadcast { get; }
        public bool MegaGroup { get; }
        public string? UserName { get; }
        public long CreatorId { get; }
        public List<ChatAdmin> AdminList { get; }
        public int? ParticipantsCount { get; }

        public string? About => null;
        public long AccessHash => 0;
        public List<long> Bots => new();
        public int Date => 0;
        public ChatBannedRights? DefaultBannedRights => null;
        public int LastSendDate => 0;
        public long LastSenderPeerId => 0;
        public int Pts => 0;
        public bool Signatures => false;
        public bool SlowModeEnabled => false;
        public string Title => string.Empty;
        public int TopMessageId => 0;
        public bool Verified => false;
        public bool Fake => false;
        public bool Scam => false;
        public long? LinkedChatId => null;
        public bool Forum => false;
        public bool ForumTabs => false;
        public long? PhotoId => null;
        public bool NoForwards => false;
        public PeerColor? Color => null;
        public PeerColor? ProfileColor => null;
        public long? BackgroundEmojiId => null;
        public int? Level => null;
        public bool HasLink => false;
        public bool IsDeleted => false;
        public EmojiStatus? EmojiStatus => null;
        public bool SignatureProfiles => false;
        public int? SubscriptionUntilDate => null;
        public bool HiddenPreHistory => false;
        public List<UsernameInfo>? Usernames => null;
        public bool ParticipantsHidden => false;
        public bool Autotranslation => false;
        public bool AntiSpam => false;
        public bool Gigagroup => false;
        public bool JoinToSend => false;
        public bool JoinRequest => false;
        public bool IsMonoforum => false;
        public bool BroadcastMessagesAllowed => false;
        public long? LinkedMonoforumId => null;
        public bool PaidReactionsEnabled => false;
        public string? MainProfileTab => null;
        public long? SendPaidMessagesStars => null;
    }
}
