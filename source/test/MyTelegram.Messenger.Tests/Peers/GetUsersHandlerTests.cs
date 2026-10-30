using System.Reflection;
using Moq;
using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Messenger.Services;
using MyTelegram.Schema;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Tests.Peers;

/// <summary>
/// Feature: <c>users.getUsers</c>, the bulk refresh of the user half of the
/// <a href="https://corefork.telegram.org/api/peers#peer-info-database">peer info database</a>.
///
/// <para>
/// The reply is positional — the client matches it against the ids it sent — so an id that resolves
/// to nothing has to come back as <c>userEmpty</c> and never as some other user. In particular
/// <c>inputUserEmpty</c> asks for nothing and used to be answered with the caller themselves.
/// </para>
/// <para>
/// The <c>access_hash</c> is what proves the caller ever legitimately received a user, so it is
/// checked here — except for the handful of built-in service peers documented as fetchable with a
/// zero hash. See https://corefork.telegram.org/api/peers#manual-refreshes
/// </para>
/// </summary>
public class GetUsersHandlerTests
{
    private const long CallerUserId = 2_000_001;
    private const long OtherUserId = 2_000_002;
    private const long ValidAccessHash = 1234;

    [Fact]
    public async Task inputUserEmpty_answers_userEmpty_and_not_the_caller()
    {
        var handler = CreateHandler();

        var users = await InvokeAsync(handler, new TInputUserEmpty());

        users.Count.ShouldBe(1);
        users[0].ShouldBeOfType<TUserEmpty>().Id.ShouldBe(0);
    }

    [Fact]
    public async Task inputUserSelf_resolves_to_the_caller()
    {
        var handler = CreateHandler();

        var users = await InvokeAsync(handler, new TInputUserSelf());

        users.Count.ShouldBe(1);
        users[0].Id.ShouldBe(CallerUserId);
    }

    [Fact]
    public async Task A_user_with_a_valid_access_hash_resolves()
    {
        var handler = CreateHandler();

        var users = await InvokeAsync(handler,
            new TInputUser { UserId = OtherUserId, AccessHash = ValidAccessHash });

        users.Count.ShouldBe(1);
        users[0].Id.ShouldBe(OtherUserId);
    }

    [Fact]
    public async Task A_user_with_a_forged_access_hash_is_rejected()
    {
        var handler = CreateHandler();

        var exception = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync(handler, new TInputUser { UserId = OtherUserId, AccessHash = 9999 }));

        exception.RpcError.Message.ShouldBe("USER_ID_INVALID");
    }

    [Fact]
    public async Task The_service_notifications_user_is_fetchable_with_a_zero_access_hash()
    {
        var handler = CreateHandler();

        var users = await InvokeAsync(handler,
            new TInputUser { UserId = MyTelegramConsts.NotificationServiceUserId, AccessHash = 0 });

        users.Count.ShouldBe(1);
        users[0].Id.ShouldBe(MyTelegramConsts.NotificationServiceUserId);
    }

    [Fact]
    public async Task An_unresolvable_id_stays_userEmpty_so_the_positions_still_line_up()
    {
        // The converter is set up to return nothing for OtherUserId here.
        var handler = CreateHandler(resolvesUsers: false);

        var users = await InvokeAsync(handler, new TInputUserSelf(),
            new TInputUser { UserId = OtherUserId, AccessHash = ValidAccessHash });

        users.Count.ShouldBe(2);
        users[0].ShouldBeOfType<TUserEmpty>().Id.ShouldBe(CallerUserId);
        users[1].ShouldBeOfType<TUserEmpty>().Id.ShouldBe(OtherUserId);
    }

    [Fact]
    public async Task inputUserFromMessage_goes_through_the_context_check()
    {
        var fromMessageResolver = new Mock<IFromMessagePeerResolver>(MockBehavior.Strict);
        fromMessageResolver
            .Setup(p => p.ResolveUserIdAsync(It.IsAny<IRequestInput>(), It.IsAny<IInputPeer>(), 34, OtherUserId))
            .ReturnsAsync(OtherUserId);

        var handler = CreateHandler(fromMessageResolver: fromMessageResolver);

        var users = await InvokeAsync(handler, new TInputUserFromMessage
        {
            Peer = new TInputPeerChannel { ChannelId = 800_000_000_001, AccessHash = 1 },
            MsgId = 34,
            UserId = OtherUserId
        });

        users.Count.ShouldBe(1);
        users[0].Id.ShouldBe(OtherUserId);
        fromMessageResolver.VerifyAll();
    }

    // ---- Fixtures ------------------------------------------------------------------------------------

    private static object CreateHandler(bool resolvesUsers = true,
        Mock<IFromMessagePeerResolver>? fromMessageResolver = null)
    {
        var userConverterService = new Mock<IUserConverterService>(MockBehavior.Loose);
        userConverterService
            .Setup(p => p.GetUserListAsync(It.IsAny<IRequestWithAccessHashKeyId>(), It.IsAny<List<long>>(),
                It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<int>()))
            .ReturnsAsync((IRequestWithAccessHashKeyId _, List<long> ids, bool _, bool _, int _) =>
                resolvesUsers
                    ? ids.Select(id => (ILayeredUser)new TUser { Id = id, FirstName = "user" }).ToList()
                    : []);

        var accessHashHelper = new Mock<IAccessHashHelper2>(MockBehavior.Loose);
        accessHashHelper
            .Setup(p => p.CheckAccessHashAsync(It.IsAny<IRequestWithAccessHashKeyId>(), It.IsAny<long>(),
                It.IsAny<long>(), It.IsAny<AccessHashType?>()))
            .Returns((IRequestWithAccessHashKeyId _, long _, long accessHash, AccessHashType? _) =>
                accessHash == ValidAccessHash
                    ? Task.CompletedTask
                    : throw new RpcException(RpcErrors.RpcErrors400.UserIdInvalid));

        var type = typeof(IUserConverterService).Assembly.GetType(
            "MyTelegram.Messenger.Handlers.LatestLayer.Users.GetUsersHandler", throwOnError: true)!;

        return Activator.CreateInstance(type,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args:
            [
                userConverterService.Object,
                accessHashHelper.Object,
                (fromMessageResolver ?? new Mock<IFromMessagePeerResolver>(MockBehavior.Loose)).Object,
                new Mock<MyTelegram.Messenger.Services.AntiSpam.IAntiSpamActorService>().Object
            ],
            culture: null)!;
    }

    private static async Task<TVector<IUser>> InvokeAsync(object handler, params IInputUser[] ids)
    {
        var input = new Mock<IRequestInput>(MockBehavior.Loose);
        input.SetupGet(p => p.UserId).Returns(CallerUserId);

        var request = new MyTelegram.Schema.Users.RequestGetUsers { Id = new TVector<IInputUser>(ids) };
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

        return (TVector<IUser>)task.GetType().GetProperty("Result")!.GetValue(task)!;
    }
}
