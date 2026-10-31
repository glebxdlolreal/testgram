using System.Reflection;
using EventFlow.Queries;
using Moq;
using MyTelegram.Domain.Aggregates.Channel;
using MyTelegram.Messenger.Services.Impl;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Tests.SecretChat;
using MyTelegram.Queries;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Tests.Peers;

/// <summary>
/// Feature: the channel half of the username methods — <c>channels.toggleUsername</c>,
/// <c>channels.reorderUsernames</c> and <c>channels.deactivateAllUsernames</c>.
///
/// <para>
/// They used to take the channel id straight from the request and edit the read model without asking
/// who the caller was, which let anybody deactivate the usernames of any channel on the server. They
/// now validate the caller and the edit, then hand the whole new list to the channel aggregate, whose
/// event carries <c>updateChannel</c> and the admin log entry.
/// </para>
/// </summary>
public class ChannelUsernameHandlerTests
{
    private const long OwnerUserId = 2_000_001;
    private const long StrangerUserId = 2_000_002;
    private const long ChannelId = 800_000_000_001;

    [Fact]
    public async Task toggleUsername_deactivates_a_fragment_username_through_the_aggregate()
    {
        var commandBus = new RecordingCommandBus();
        var handler = CreateHandler("ToggleUsernameHandler", commandBus);

        var result = await InvokeAsync(handler, OwnerUserId, new MyTelegram.Schema.Channels.RequestToggleUsername
        {
            Channel = InputChannel(),
            Username = "collectible",
            Active = false
        });

        result.ShouldBeOfType<TBoolTrue>();
        var command = commandBus.Published.OfType<SetChannelUsernamesCommand>().Single();
        command.Usernames.ShouldBe([new UsernameItem("basic", true, true), new UsernameItem("collectible", false, false)]);
        command.OldUsernames!.Count(p => p.Active).ShouldBe(2);
    }

    [Fact]
    public async Task toggleUsername_of_the_basic_username_is_USERNAME_INVALID()
    {
        var commandBus = new RecordingCommandBus();
        var handler = CreateHandler("ToggleUsernameHandler", commandBus);

        var exception = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync(handler, OwnerUserId, new MyTelegram.Schema.Channels.RequestToggleUsername
            {
                Channel = InputChannel(),
                Username = "basic",
                Active = false
            }));

        exception.RpcError.Message.ShouldBe("USERNAME_INVALID");
        commandBus.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task toggleUsername_by_someone_who_is_not_the_owner_is_CHAT_ADMIN_REQUIRED()
    {
        var commandBus = new RecordingCommandBus();
        var handler = CreateHandler("ToggleUsernameHandler", commandBus);

        var exception = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync(handler, StrangerUserId, new MyTelegram.Schema.Channels.RequestToggleUsername
            {
                Channel = InputChannel(),
                Username = "collectible",
                Active = false
            }));

        exception.RpcError.Message.ShouldBe("CHAT_ADMIN_REQUIRED");
        commandBus.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task toggleUsername_with_a_forged_access_hash_is_rejected()
    {
        var commandBus = new RecordingCommandBus();
        var handler = CreateHandler("ToggleUsernameHandler", commandBus);

        var exception = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync(handler, OwnerUserId, new MyTelegram.Schema.Channels.RequestToggleUsername
            {
                Channel = new TInputChannel { ChannelId = ChannelId, AccessHash = 1 },
                Username = "collectible",
                Active = false
            }));

        exception.RpcError.Message.ShouldBe("CHANNEL_INVALID");
        commandBus.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task reorderUsernames_reorders_through_the_aggregate()
    {
        var commandBus = new RecordingCommandBus();
        var handler = CreateHandler("ReorderUsernamesHandler", commandBus);

        var result = await InvokeAsync(handler, OwnerUserId, new MyTelegram.Schema.Channels.RequestReorderUsernames
        {
            Channel = InputChannel(),
            Order = new TVector<string>("collectible", "basic")
        });

        result.ShouldBeOfType<TBoolTrue>();
        commandBus.Published.OfType<SetChannelUsernamesCommand>().Single().Usernames.Select(p => p.Username)
            .ShouldBe(["collectible", "basic"]);
    }

    [Fact]
    public async Task reorderUsernames_with_a_duplicate_is_ORDER_INVALID()
    {
        var commandBus = new RecordingCommandBus();
        var handler = CreateHandler("ReorderUsernamesHandler", commandBus);

        var exception = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync(handler, OwnerUserId, new MyTelegram.Schema.Channels.RequestReorderUsernames
            {
                Channel = InputChannel(),
                Order = new TVector<string>("basic", "basic")
            }));

        exception.RpcError.Message.ShouldBe("ORDER_INVALID");
        commandBus.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task reorderUsernames_by_someone_who_is_not_the_owner_is_CHAT_ADMIN_REQUIRED()
    {
        var commandBus = new RecordingCommandBus();
        var handler = CreateHandler("ReorderUsernamesHandler", commandBus);

        var exception = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync(handler, StrangerUserId, new MyTelegram.Schema.Channels.RequestReorderUsernames
            {
                Channel = InputChannel(),
                Order = new TVector<string>("collectible", "basic")
            }));

        exception.RpcError.Message.ShouldBe("CHAT_ADMIN_REQUIRED");
        commandBus.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task deactivateAllUsernames_keeps_the_basic_username()
    {
        var commandBus = new RecordingCommandBus();
        var handler = CreateHandler("DeactivateAllUsernamesHandler", commandBus);

        var result = await InvokeAsync(handler, OwnerUserId,
            new MyTelegram.Schema.Channels.RequestDeactivateAllUsernames { Channel = InputChannel() });

        result.ShouldBeOfType<TBoolTrue>();
        commandBus.Published.OfType<SetChannelUsernamesCommand>().Single().Usernames
            .Where(p => p.Active).Select(p => p.Username).ShouldBe(["basic"]);
    }

    [Fact]
    public async Task deactivateAllUsernames_by_someone_who_is_not_the_owner_is_CHAT_ADMIN_REQUIRED()
    {
        var commandBus = new RecordingCommandBus();
        var handler = CreateHandler("DeactivateAllUsernamesHandler", commandBus);

        var exception = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync(handler, StrangerUserId,
                new MyTelegram.Schema.Channels.RequestDeactivateAllUsernames { Channel = InputChannel() }));

        exception.RpcError.Message.ShouldBe("CHAT_ADMIN_REQUIRED");
        commandBus.Published.ShouldBeEmpty();
    }

    // ---- Fixtures ------------------------------------------------------------------------------------

    private static TInputChannel InputChannel() => new() { ChannelId = ChannelId, AccessHash = 4242 };

    private static object CreateHandler(string handlerName, RecordingCommandBus commandBus)
    {
        var channelReadModel = new Mock<IChannelReadModel>(MockBehavior.Loose);
        channelReadModel.SetupGet(p => p.ChannelId).Returns(ChannelId);
        channelReadModel.SetupGet(p => p.CreatorId).Returns(OwnerUserId);
        channelReadModel.SetupGet(p => p.AdminList).Returns([]);
        channelReadModel.SetupGet(p => p.UserName).Returns("basic");
        channelReadModel.SetupGet(p => p.Usernames).Returns(
        [
            new UsernameInfo { Username = "basic", Editable = true, Active = true },
            new UsernameInfo { Username = "collectible", Editable = false, Active = true }
        ]);

        var channelAppService = new Mock<IChannelAppService>(MockBehavior.Loose);
        channelAppService.Setup(p => p.GetAsync(It.IsAny<long?>())).ReturnsAsync(channelReadModel.Object);
        channelAppService.Setup(p => p.GetAsync(It.IsAny<long>())).ReturnsAsync(channelReadModel.Object);

        var queryProcessor = new Mock<IQueryProcessor>(MockBehavior.Loose);
        queryProcessor.Setup(p => p.ProcessAsync(It.IsAny<GetChannelByIdQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(channelReadModel.Object);

        var accessHashHelper = new Mock<IAccessHashHelper2>(MockBehavior.Loose);
        accessHashHelper
            .Setup(p => p.CheckAccessHashAsync(It.IsAny<IRequestWithAccessHashKeyId>(), It.IsAny<IInputChannel>()))
            .Returns((IRequestWithAccessHashKeyId _, IInputChannel channel) =>
                channel is TInputChannel { AccessHash: 4242 }
                    ? Task.CompletedTask
                    : throw new RpcException(RpcErrors.RpcErrors400.ChannelInvalid));

        var checker = new ChannelAdminRightsChecker(new Mock<IQueryProcessor>(MockBehavior.Loose).Object,
            channelAppService.Object);

        var handlerType = typeof(ChannelAdminRightsChecker).Assembly.GetType(
            $"MyTelegram.Messenger.Handlers.LatestLayer.Channels.{handlerName}", throwOnError: true)!;

        return Activator.CreateInstance(handlerType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [queryProcessor.Object, accessHashHelper.Object, checker, commandBus],
            culture: null)!;
    }

    internal static async Task<object?> InvokeAsync(object handler, long callerUserId, IObject request)
    {
        var input = new Mock<IRequestInput>(MockBehavior.Loose);
        input.SetupGet(p => p.UserId).Returns(callerUserId);

        var method = handler.GetType().GetMethod("HandleCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        Task task;
        try
        {
            task = (Task)method.Invoke(handler, [input.Object, request])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }

        await task;

        return task.GetType().GetProperty("Result")!.GetValue(task);
    }
}
