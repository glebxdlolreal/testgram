using EventFlow.Queries;
using Moq;
using MyTelegram.Core;
using MyTelegram.Messenger.Services;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Queries;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Tests.Peers;

/// <summary>
/// Feature: <c>updateUserName</c> after <c>account.updateUsername</c>,
/// <c>account.toggleUsername</c> and <c>account.reorderUsernames</c>.
///
/// <para>
/// <c>account.updateUsername</c> answered with the new user object to the calling session only, so
/// every contact kept the previous username in their
/// <a href="https://corefork.telegram.org/api/peers#peer-info-database">peer info database</a>.
/// It is announced from a domain event handler, which runs alongside the read model update, so the
/// name is taken from the event rather than re-read, and the usernames vector too when the event
/// carries it. Disabled Fragment collectibles go to the owner's own sessions only.
/// </para>
/// </summary>
public class UsernameUpdateNotifierTests
{
    private const long UserId = 2_000_001;
    private const long ContactUserId = 2_000_002;
    private const long AuthKeyId = 777;

    [Fact]
    public async Task Contacts_are_told_about_the_new_username()
    {
        var (notifier, sender) = CreateNotifier();

        await notifier.NotifyUserNameChangedAsync(UserId, AuthKeyId);

        var update = sender.UpdateFor(ContactUserId);
        update.UserId.ShouldBe(UserId);
        update.Usernames!.Select(p => ((TUsername)p).Username).ShouldBe(["basic", "collectible"]);
    }

    [Fact]
    public async Task The_users_own_other_sessions_are_told_too()
    {
        var (notifier, sender) = CreateNotifier();

        await notifier.NotifyUserNameChangedAsync(UserId, AuthKeyId);

        sender.UpdateFor(UserId).UserId.ShouldBe(UserId);
        sender.ExcludeAuthKeyIdFor(UserId).ShouldBe(AuthKeyId);
    }

    [Fact]
    public async Task A_snapshot_wins_over_a_read_model_that_has_not_caught_up()
    {
        var (notifier, sender) = CreateNotifier();

        await notifier.NotifyUserNameChangedAsync(UserId, AuthKeyId,
            new UserNameSnapshot("New", "Name", "renamed"));

        var update = sender.UpdateFor(ContactUserId);
        update.FirstName.ShouldBe("New");
        update.LastName.ShouldBe("Name");
        // The editable username comes from the event; the Fragment one is kept.
        update.Usernames!.Select(p => ((TUsername)p).Username).ShouldBe(["renamed", "collectible"]);
    }

    [Fact]
    public async Task Clearing_the_username_leaves_only_the_fragment_ones()
    {
        var (notifier, sender) = CreateNotifier();

        await notifier.NotifyUserNameChangedAsync(UserId, AuthKeyId, new UserNameSnapshot("New", null, null));

        sender.UpdateFor(ContactUserId).Usernames!.Select(p => ((TUsername)p).Username).ShouldBe(["collectible"]);
    }

    [Fact]
    public async Task The_cached_user_is_dropped_first()
    {
        // Commands executed in this process never reach the cached read model, so the cached copy
        // would otherwise still hold the previous username list.
        var (notifier, _, userAppService) = CreateNotifierWithServices();

        await notifier.NotifyUserNameChangedAsync(UserId, AuthKeyId);

        userAppService.Verify(p => p.InvalidateCache(UserId), Times.Once);
    }

    [Fact]
    public async Task A_disabled_collectible_reaches_the_owner_but_not_the_contacts()
    {
        var (notifier, sender) = CreateNotifier();

        await notifier.NotifyUserNameChangedAsync(UserId, AuthKeyId, usernames:
        [
            new UsernameItem("basic", true, true),
            new UsernameItem("hidden", false, false)
        ]);

        sender.UpdateFor(UserId).Usernames!.Select(p => ((TUsername)p).Username).ShouldBe(["basic", "hidden"]);
        sender.UpdateFor(ContactUserId).Usernames!.Select(p => ((TUsername)p).Username).ShouldBe(["basic"]);
    }

    // ---- Fixtures ------------------------------------------------------------------------------------

    private static (UsernameUpdateNotifier, RecordingUsernameSender) CreateNotifier()
    {
        var (notifier, sender, _) = CreateNotifierWithServices();

        return (notifier, sender);
    }

    private static (UsernameUpdateNotifier, RecordingUsernameSender, Mock<IUserAppService>)
        CreateNotifierWithServices()
    {
        var userReadModel = new Mock<IUserReadModel>(MockBehavior.Loose);
        userReadModel.SetupGet(p => p.UserId).Returns(UserId);
        userReadModel.SetupGet(p => p.FirstName).Returns("Old");
        userReadModel.SetupGet(p => p.LastName).Returns("Name");
        userReadModel.SetupGet(p => p.Usernames).Returns(
        [
            new UsernameInfo { Username = "basic", Editable = true, Active = true },
            new UsernameInfo { Username = "collectible", Editable = false, Active = true }
        ]);

        var userAppService = new Mock<IUserAppService>(MockBehavior.Loose);
        userAppService.Setup(p => p.GetAsync(It.IsAny<long?>())).ReturnsAsync(userReadModel.Object);

        var queryProcessor = new Mock<IQueryProcessor>(MockBehavior.Loose);
        queryProcessor
            .Setup(p => p.ProcessAsync(It.IsAny<GetContactSelfUserIdListByTargetUserIdQuery>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<long>)[ContactUserId]);

        var sender = new RecordingUsernameSender();

        return (new UsernameUpdateNotifier(userAppService.Object, queryProcessor.Object, sender), sender,
            userAppService);
    }

    private sealed class RecordingUsernameSender : IObjectMessageSender
    {
        private readonly List<(long UserId, TUpdates Updates, long? ExcludeAuthKeyId)> _pushes = [];

        public TUpdateUserName UpdateFor(long userId) =>
            (TUpdateUserName)_pushes.Single(p => p.UserId == userId).Updates.Updates.Single();

        public long? ExcludeAuthKeyIdFor(long userId) => _pushes.Single(p => p.UserId == userId).ExcludeAuthKeyId;

        public Task PushMessageToPeerAsync<TData>(Peer peer, TData data, long? excludeAuthKeyId = null,
            long? excludeUserId = null, long? onlySendToUserId = null, long? onlySendToThisAuthKeyId = null,
            int pts = 0, int? qts = null, long globalSeqNo = 0, PushData? pushData = null,
            List<long>? excludeUserIds = null) where TData : IObject
        {
            _pushes.Add((peer.PeerId, (TUpdates)(object)data!, excludeAuthKeyId));

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
