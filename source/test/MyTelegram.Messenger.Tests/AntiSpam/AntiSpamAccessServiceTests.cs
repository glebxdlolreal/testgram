using EventFlow.Queries;
using Moq;
using MyTelegram.Core;
using MyTelegram.Abstractions;
using MyTelegram.Messenger.Services;
using MyTelegram.Messenger.Services.AntiSpam;
using MyTelegram.Queries;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Tests.AntiSpam;

public class AntiSpamAccessServiceTests
{
    private const long Channel = 800000000001;
    private const long Caller = 2010001;

    [Theory]
    [InlineData(false, false, false, true, true, true, false, "CHANNEL_INVALID")]
    [InlineData(true, true, false, true, true, true, false, "BOT_METHOD_INVALID")]
    [InlineData(true, false, true, true, true, true, false, "CHANNEL_PRIVATE")]
    [InlineData(true, false, false, false, true, true, false, "CHAT_ADMIN_REQUIRED")]
    [InlineData(true, false, false, true, false, true, false, "CHAT_ADMIN_REQUIRED")]
    [InlineData(true, false, false, true, true, false, false, "CHANNEL_INVALID")]
    [InlineData(true, false, false, true, true, true, true, "CHANNEL_INVALID")]
    [InlineData(true, false, false, true, true, true, false, null)]
    public async Task Checks_caller_hash_membership_rights_and_group_kind(bool hashValid, bool bot, bool left,
        bool admin, bool delete, bool mega, bool giga, string? error)
    {
        var fixture = Create(hashValid, bot, left, admin, delete, mega, giga);
        var request = new TInputChannel { ChannelId = Channel, AccessHash = 123 };
        if (error == null) (await fixture.Service.RequireAdminAsync(Input(), request)).ChannelId.ShouldBe(Channel);
        else (await Should.ThrowAsync<RpcException>(() => fixture.Service.RequireAdminAsync(Input(), request))).RpcError.Message.ShouldBe(error);
    }

    [Fact]
    public async Task From_message_constructor_must_use_context_resolver()
    {
        var fixture = Create();
        var container = new TInputPeerSelf();
        fixture.Resolver.Setup(r => r.ResolveChannelIdAsync(It.IsAny<IRequestInput>(), container, 42, Channel)).ReturnsAsync(Channel);
        var result = await fixture.Service.RequireAdminAsync(Input(), new TInputChannelFromMessage { ChannelId = Channel, Peer = container, MsgId = 42 });
        result.ChannelId.ShouldBe(Channel);
        fixture.Resolver.Verify(r => r.ResolveChannelIdAsync(It.IsAny<IRequestInput>(), container, 42, Channel), Times.Once);
    }

    private static (AntiSpamAccessService Service, Mock<IFromMessagePeerResolver> Resolver) Create(
        bool hashValid = true, bool bot = false, bool left = false, bool admin = true, bool delete = true, bool mega = true, bool giga = false)
    {
        var query = new Mock<IQueryProcessor>();
        var user = new Mock<IUserReadModel>();
        user.SetupGet(u => u.Bot).Returns(bot);
        query.Setup(q => q.ProcessAsync(It.IsAny<GetUserByIdQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync(user.Object);
        var channel = new Mock<IChannelReadModel>();
        channel.SetupGet(c => c.ChannelId).Returns(Channel);
        channel.SetupGet(c => c.MegaGroup).Returns(mega);
        channel.SetupGet(c => c.Gigagroup).Returns(giga);
        query.Setup(q => q.ProcessAsync(It.IsAny<GetChannelByIdQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync(channel.Object);
        var member = new Mock<IChannelMemberReadModel>();
        member.SetupGet(m => m.IsAdmin).Returns(admin);
        member.SetupGet(m => m.AdminRights).Returns(delete ? 8 : 0);
        member.SetupGet(m => m.Left).Returns(left);
        query.Setup(q => q.ProcessAsync(It.IsAny<GetChannelMemberByUserIdQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync(member.Object);
        var hashes = new Mock<IAccessHashHelper2>();
        hashes.Setup(h => h.IsAccessHashValidAsync(It.IsAny<IRequestWithAccessHashKeyId>(), Channel, 123, AccessHashType.Channel)).Returns(new ValueTask<bool>(hashValid));
        var resolver = new Mock<IFromMessagePeerResolver>(MockBehavior.Strict);
        return (new AntiSpamAccessService(query.Object, hashes.Object, resolver.Object, new PeerHelper()), resolver);
    }

    private static IRequestInput Input()
    {
        var input = new Mock<IRequestInput>();
        input.SetupGet(i => i.UserId).Returns(Caller);
        return input.Object;
    }
}
