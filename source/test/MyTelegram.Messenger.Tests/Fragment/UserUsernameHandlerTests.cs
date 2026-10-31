using EventFlow.Queries;
using Moq;
using MyTelegram.Domain.Aggregates.User;
using MyTelegram.Messenger.Services.Bots;
using MyTelegram.Messenger.Tests.Peers;
using MyTelegram.Messenger.Tests.SecretChat;
using MyTelegram.Queries;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Services.Services;
using AccountHandlers = MyTelegram.Messenger.Handlers.LatestLayer.Account;
using BotHandlers = MyTelegram.Messenger.Handlers.LatestLayer.Bots;

namespace MyTelegram.Messenger.Tests.Fragment;

/// <summary>
/// Feature: <c>account.toggleUsername</c>, <c>account.reorderUsernames</c>, <c>bots.toggleUsername</c>
/// and <c>bots.reorderUsernames</c>.
/// <para>
/// They used to rewrite <c>eventflow-userreadmodel</c> in place, so a read model rebuild lost every
/// change; the bot variants also pushed nothing and left the cached bot stale. They now validate the
/// edit and hand the whole new list to the user aggregate, whose event carries
/// <c>updateUserName</c>.
/// </para>
/// </summary>
public class UserUsernameHandlerTests
{
    private const long UserId = 2_000_001;
    private const long BotId = 7_000_001;
    private const long StrangerUserId = 2_000_002;

    [Fact]
    public async Task account_toggleUsername_enables_a_collectible_through_the_aggregate()
    {
        var commandBus = new RecordingCommandBus();
        var handler = new AccountHandlers.ToggleUsernameHandler(QueryProcessorFor(User()), commandBus);

        var result = await ChannelUsernameHandlerTests.InvokeAsync(handler, UserId,
            new MyTelegram.Schema.Account.RequestToggleUsername { Username = "Hidden", Active = true });

        result.ShouldBeOfType<TBoolTrue>();
        var command = commandBus.Published.OfType<SetUsernamesCommand>().Single();
        command.AggregateId.ShouldBe(MyTelegram.Domain.Aggregates.User.UserId.Create(UserId));
        command.Usernames.ShouldBe([
            new UsernameItem("basic", true, true),
            new UsernameItem("nft", false, true),
            new UsernameItem("hidden", false, true)
        ]);
    }

    [Fact]
    public async Task account_toggleUsername_to_the_current_state_is_USERNAME_NOT_MODIFIED()
    {
        var commandBus = new RecordingCommandBus();
        var handler = new AccountHandlers.ToggleUsernameHandler(QueryProcessorFor(User()), commandBus);

        var exception = await Should.ThrowAsync<RpcException>(() => ChannelUsernameHandlerTests.InvokeAsync(handler,
            UserId, new MyTelegram.Schema.Account.RequestToggleUsername { Username = "nft", Active = true }));

        exception.RpcError.Message.ShouldBe("USERNAME_NOT_MODIFIED");
        commandBus.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task account_reorderUsernames_with_a_disabled_username_is_ORDER_INVALID()
    {
        var commandBus = new RecordingCommandBus();
        var handler = new AccountHandlers.ReorderUsernamesHandler(QueryProcessorFor(User()), commandBus);

        var exception = await Should.ThrowAsync<RpcException>(() => ChannelUsernameHandlerTests.InvokeAsync(handler,
            UserId, new MyTelegram.Schema.Account.RequestReorderUsernames
            {
                Order = new TVector<string>("nft", "basic", "hidden")
            }));

        exception.RpcError.Message.ShouldBe("ORDER_INVALID");
        commandBus.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task account_reorderUsernames_reorders_through_the_aggregate()
    {
        var commandBus = new RecordingCommandBus();
        var handler = new AccountHandlers.ReorderUsernamesHandler(QueryProcessorFor(User()), commandBus);

        await ChannelUsernameHandlerTests.InvokeAsync(handler, UserId,
            new MyTelegram.Schema.Account.RequestReorderUsernames { Order = new TVector<string>("nft", "basic") });

        commandBus.Published.OfType<SetUsernamesCommand>().Single().Usernames.Select(p => p.Username)
            .ShouldBe(["nft", "basic", "hidden"]);
    }

    [Fact]
    public async Task bots_toggleUsername_by_the_owner_may_disable_the_basic_username()
    {
        var commandBus = new RecordingCommandBus();
        var handler = new BotHandlers.ToggleUsernameHandler(ResolverFor(ownerUserId: UserId), commandBus);

        await ChannelUsernameHandlerTests.InvokeAsync(handler, UserId,
            new MyTelegram.Schema.Bots.RequestToggleUsername
            {
                Bot = new TInputUser { UserId = BotId, AccessHash = 1 },
                Username = "basic",
                Active = false
            });

        var command = commandBus.Published.OfType<SetUsernamesCommand>().Single();
        command.AggregateId.ShouldBe(MyTelegram.Domain.Aggregates.User.UserId.Create(BotId));
        command.Usernames.Where(p => p.Active).Select(p => p.Username).ShouldBe(["nft"]);
    }

    [Fact]
    public async Task bots_toggleUsername_by_a_stranger_is_BOT_INVALID()
    {
        var commandBus = new RecordingCommandBus();
        var handler = new BotHandlers.ToggleUsernameHandler(ResolverFor(ownerUserId: UserId), commandBus);

        var exception = await Should.ThrowAsync<RpcException>(() => ChannelUsernameHandlerTests.InvokeAsync(handler,
            StrangerUserId, new MyTelegram.Schema.Bots.RequestToggleUsername
            {
                Bot = new TInputUser { UserId = BotId, AccessHash = 1 },
                Username = "nft",
                Active = false
            }));

        exception.RpcError.Message.ShouldBe("BOT_INVALID");
        commandBus.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task bots_reorderUsernames_reorders_the_bot()
    {
        var commandBus = new RecordingCommandBus();
        var handler = new BotHandlers.ReorderUsernamesHandler(ResolverFor(ownerUserId: UserId), commandBus);

        await ChannelUsernameHandlerTests.InvokeAsync(handler, UserId,
            new MyTelegram.Schema.Bots.RequestReorderUsernames
            {
                Bot = new TInputUser { UserId = BotId, AccessHash = 1 },
                Order = new TVector<string>("nft", "basic")
            });

        commandBus.Published.OfType<SetUsernamesCommand>().Single().Usernames.Select(p => p.Username)
            .ShouldBe(["nft", "basic", "hidden"]);
    }

    // ---- Fixtures ------------------------------------------------------------------------------------

    private static IUserReadModel User(long userId = UserId, bool bot = false)
    {
        var user = new Mock<IUserReadModel>(MockBehavior.Loose);
        user.SetupGet(p => p.UserId).Returns(userId);
        user.SetupGet(p => p.Bot).Returns(bot);
        user.SetupGet(p => p.UserName).Returns("basic");
        user.SetupGet(p => p.Usernames).Returns(
        [
            new UsernameInfo { Username = "basic", Editable = true, Active = true },
            new UsernameInfo { Username = "nft", Editable = false, Active = true },
            new UsernameInfo { Username = "hidden", Editable = false, Active = false }
        ]);

        return user.Object;
    }

    private static IQueryProcessor QueryProcessorFor(IUserReadModel user)
    {
        var queryProcessor = new Mock<IQueryProcessor>(MockBehavior.Loose);
        queryProcessor.Setup(p => p.ProcessAsync(It.Is<GetUserByIdQuery>(q => q.UserId == user.UserId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        return queryProcessor.Object;
    }

    private static IOwnedBotResolver ResolverFor(long ownerUserId)
    {
        var ownership = new Mock<IBotOwnershipChecker>(MockBehavior.Loose);
        ownership.Setup(p => p.IsOwnerAsync(BotId, ownerUserId)).ReturnsAsync(true);

        return new OwnedBotResolver(QueryProcessorFor(User(BotId, bot: true)),
            new Mock<IAccessHashHelper2>(MockBehavior.Loose).Object, ownership.Object);
    }
}
