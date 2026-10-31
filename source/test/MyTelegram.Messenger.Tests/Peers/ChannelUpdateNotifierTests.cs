using Moq;
using MyTelegram.Core;
using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Messenger.Services;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Tests.Peers;

/// <summary>
/// Feature: the <c>updateChannel</c> fan-out behind the channel username methods.
///
/// <para>
/// <c>updateChannel</c> tells a client that its cached channel entry is stale, and the
/// <a href="https://corefork.telegram.org/api/peers#handling-updates">peer database</a> rules say the
/// client refetches from there. The channel constructor itself carries a per-recipient
/// <c>access_hash</c>, so the copy built for the caller must not be broadcast to the other members —
/// they get the bare update and fetch their own.
/// </para>
/// </summary>
public class ChannelUpdateNotifierTests
{
    private const long CallerUserId = 2_000_001;
    private const long CallerAuthKeyId = 777;
    private const long ChannelId = 800_000_000_001;

    [Fact]
    public async Task Members_get_a_bare_updateChannel_without_the_callers_channel_object()
    {
        var (notifier, sender) = CreateNotifier();

        await notifier.NotifyChannelChangedAsync(Input(), ChannelId);

        var pushed = sender.PushedTo(new Peer(PeerType.Channel, ChannelId));
        pushed.Updates.Count.ShouldBe(1);
        pushed.Updates[0].ShouldBeOfType<TUpdateChannel>().ChannelId.ShouldBe(ChannelId);
        pushed.Chats.Count.ShouldBe(0);
    }

    [Fact]
    public async Task The_caller_is_excluded_from_the_member_fan_out()
    {
        var (notifier, sender) = CreateNotifier();

        await notifier.NotifyChannelChangedAsync(Input(), ChannelId);

        sender.ExcludeUserIdFor(new Peer(PeerType.Channel, ChannelId)).ShouldBe(CallerUserId);
    }

    [Fact]
    public async Task The_callers_other_sessions_get_the_refreshed_channel()
    {
        var (notifier, sender) = CreateNotifier();

        await notifier.NotifyChannelChangedAsync(Input(), ChannelId);

        var pushed = sender.PushedTo(new Peer(PeerType.User, CallerUserId));
        pushed.Chats.Count.ShouldBe(1);
        pushed.Chats[0].Id.ShouldBe(ChannelId);
        // The session that made the call already knows: it got the rpc result.
        sender.ExcludeAuthKeyIdFor(new Peer(PeerType.User, CallerUserId)).ShouldBe(CallerAuthKeyId);
    }

    [Fact]
    public async Task The_cached_channel_is_dropped_before_it_is_read_back()
    {
        // The handlers write to the read model collection directly, so a stale cache entry would make
        // the update carry the pre-change username list.
        var (notifier, _, channelAppService) = CreateNotifierWithServices();

        await notifier.NotifyChannelChangedAsync(Input(), ChannelId);

        channelAppService.Verify(p => p.InvalidateCache(ChannelId), Times.Once);
    }

    [Fact]
    public async Task A_channel_that_cannot_be_converted_still_reaches_the_members()
    {
        var (notifier, sender) = CreateNotifier(withChannel: false);

        await notifier.NotifyChannelChangedAsync(Input(), ChannelId);

        sender.PushedTo(new Peer(PeerType.Channel, ChannelId)).Updates.Count.ShouldBe(1);
        sender.WasPushedTo(new Peer(PeerType.User, CallerUserId)).ShouldBeFalse();
    }

    // ---- Fixtures ------------------------------------------------------------------------------------

    private static IRequestInput Input()
    {
        var input = new Mock<IRequestInput>(MockBehavior.Loose);
        input.SetupGet(p => p.UserId).Returns(CallerUserId);
        input.SetupGet(p => p.PermAuthKeyId).Returns(CallerAuthKeyId);

        return input.Object;
    }

    private static (ChannelUpdateNotifier, RecordingMessageSender) CreateNotifier(bool withChannel = true)
    {
        var (notifier, sender, _) = CreateNotifierWithServices(withChannel);

        return (notifier, sender);
    }

    private static (ChannelUpdateNotifier, RecordingMessageSender, Mock<IChannelAppService>)
        CreateNotifierWithServices(bool withChannel = true)
    {
        var channelAppService = new Mock<IChannelAppService>(MockBehavior.Loose);

        var chatConverterService = new Mock<IChatConverterService>(MockBehavior.Loose);
        chatConverterService
            .Setup(p => p.GetChannelAsync(It.IsAny<IRequestWithAccessHashKeyId>(), ChannelId, It.IsAny<bool>(),
                It.IsAny<bool?>(), It.IsAny<int>(), It.IsAny<bool>()))
            .ReturnsAsync(withChannel ? new TChannel { Id = ChannelId, Title = "channel" } : null!);

        var sender = new RecordingMessageSender();

        return (new ChannelUpdateNotifier(channelAppService.Object, chatConverterService.Object, sender), sender,
            channelAppService);
    }

    /// <summary>
    /// Records what was pushed where. <see cref="IObjectMessageSender"/> takes generic arguments and
    /// optional parameters that Moq argument matchers cannot express readably, so this is a real
    /// implementation rather than a mock.
    /// </summary>
    private sealed class RecordingMessageSender : IObjectMessageSender
    {
        private readonly List<(Peer Peer, IObject Data, long? ExcludeAuthKeyId, long? ExcludeUserId)> _pushes = [];

        public TUpdates PushedTo(Peer peer) =>
            (TUpdates)_pushes.Single(p => p.Peer.PeerType == peer.PeerType && p.Peer.PeerId == peer.PeerId).Data;

        public bool WasPushedTo(Peer peer) =>
            _pushes.Any(p => p.Peer.PeerType == peer.PeerType && p.Peer.PeerId == peer.PeerId);

        public long? ExcludeUserIdFor(Peer peer) =>
            _pushes.Single(p => p.Peer.PeerType == peer.PeerType && p.Peer.PeerId == peer.PeerId).ExcludeUserId;

        public long? ExcludeAuthKeyIdFor(Peer peer) =>
            _pushes.Single(p => p.Peer.PeerType == peer.PeerType && p.Peer.PeerId == peer.PeerId).ExcludeAuthKeyId;

        public Task PushMessageToPeerAsync<TData>(Peer peer, TData data, long? excludeAuthKeyId = null,
            long? excludeUserId = null, long? onlySendToUserId = null, long? onlySendToThisAuthKeyId = null,
            int pts = 0, int? qts = null, long globalSeqNo = 0, PushData? pushData = null,
            List<long>? excludeUserIds = null) where TData : IObject
        {
            _pushes.Add((peer, data, excludeAuthKeyId, excludeUserId));

            return Task.CompletedTask;
        }

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
}
