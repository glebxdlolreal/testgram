using System.Reflection;
using EventFlow;
using EventFlow.Queries;
using Moq;
using MyTelegram.Core;
using MyTelegram.Domain.Aggregates.Channel;
using MyTelegram.Messenger.Extensions;
using MyTelegram.Messenger.Services.AntiSpam;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Queries;
using MyTelegram.Schema;
using MyTelegram.Schema.Channels;

namespace MyTelegram.Messenger.Tests.AntiSpam;

public class AntiSpamHandlerTests
{
    private const long Channel = 800000000001;
    private const long Caller = 2010001;

    [Theory]
    [InlineData(true, 199, false)]
    [InlineData(true, 200, true)]
    [InlineData(false, 1, true)]
    public async Task Toggle_checks_enable_threshold_but_allows_disable(bool enabled, int size, bool accepted)
    {
        var access = Access(size);
        var actor = new Mock<IAntiSpamActorService>();
        var bus = new Mock<ICommandBus>();
        var handler = Create("ToggleAntiSpamHandler", access.Object, actor.Object, new Mock<IAppConfigHelper>().Object, bus.Object);
        var request = new RequestToggleAntiSpam { Channel = new TInputChannel { ChannelId = Channel }, Enabled = enabled };
        if (accepted)
            (await Invoke(handler, request)).ShouldBeNull();
        else
            (await Should.ThrowAsync<RpcException>(() => Invoke(handler, request))).RpcError.Message.ShouldBe("PARTICIPANTS_TOO_FEW");
        bus.Verify(b => b.PublishAsync(It.IsAny<ToggleAntiSpamCommand>(), It.IsAny<CancellationToken>()), accepted ? Times.Once() : Times.Never());
        actor.Verify(a => a.EnsureAvailableAsync(It.IsAny<CancellationToken>()), accepted && enabled ? Times.Once() : Times.Never());
    }

    [Theory]
    [InlineData(0, true, false)]
    [InlineData(-1, true, false)]
    [InlineData(10, false, false)]
    [InlineData(10, true, true)]
    public async Task False_positive_requires_actual_antispam_deletion(int id, bool deletion, bool accepted)
    {
        var reports = new Mock<IAntiSpamReportStore>();
        reports.Setup(r => r.HasAntiSpamDeletionAsync(Channel, id, It.IsAny<CancellationToken>())).ReturnsAsync(deletion);
        var handler = Create("ReportAntiSpamFalsePositiveHandler", Access().Object, new Mock<IAntiSpamActorService>().Object, reports.Object);
        var request = new RequestReportAntiSpamFalsePositive { Channel = new TInputChannel { ChannelId = Channel }, MsgId = id };
        if (accepted) (await Invoke(handler, request)).ShouldBeOfType<TBoolTrue>();
        else (await Should.ThrowAsync<RpcException>(() => Invoke(handler, request))).RpcError.Message.ShouldBe("MSG_ID_INVALID");
        reports.Verify(r => r.SaveAsync(Caller, Channel, id, true, null, It.IsAny<CancellationToken>()), accepted ? Times.Once() : Times.Never());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task Spam_rejects_empty_or_unbounded_batch_before_loading_messages(int count)
    {
        var reports = new Mock<IAntiSpamReportStore>(MockBehavior.Strict);
        var query = new Mock<IQueryProcessor>(MockBehavior.Strict);
        var handler = Create("ReportSpamHandler", Access().Object, reports.Object, query.Object);
        var request = new RequestReportSpam { Channel = new TInputChannel { ChannelId = Channel }, Participant = new TInputPeerSelf(), Id = new TVector<int>(Enumerable.Range(1, count)) };
        (await Should.ThrowAsync<RpcException>(() => Invoke(handler, request))).RpcError.Message.ShouldBe("MSG_ID_INVALID");
        query.Verify(q => q.ProcessAsync(It.IsAny<GetMessagesByIdListQuery>(), It.IsAny<CancellationToken>()), Times.Never());
        reports.Verify(r => r.SaveAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task Spam_requires_channel_message_and_matching_visible_sender(bool wrongChannel, bool wrongSender, bool accepted)
    {
        var access = Access();
        access.Setup(a => a.ResolveParticipantAsync(It.IsAny<IRequestInput>(), It.IsAny<IInputPeer>()))
            .ReturnsAsync(new Peer(PeerType.User, 2010002));
        var message = new Mock<IMessageReadModel>();
        message.SetupGet(m => m.MessageId).Returns(42);
        message.SetupGet(m => m.OwnerPeerId).Returns(Channel);
        message.SetupGet(m => m.ToPeerId).Returns(wrongChannel ? Channel + 1 : Channel);
        message.SetupGet(m => m.ToPeerType).Returns(PeerType.Channel);
        message.SetupGet(m => m.SenderPeerId).Returns(wrongSender ? 2010003 : 2010002);
        var query = new Mock<IQueryProcessor>();
        query.Setup(q => q.ProcessAsync(It.IsAny<GetMessagesByIdListQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { message.Object });
        var reports = new Mock<IAntiSpamReportStore>();
        var handler = Create("ReportSpamHandler", access.Object, reports.Object, query.Object);
        var request = new RequestReportSpam { Channel = new TInputChannel { ChannelId = Channel }, Participant = new TInputPeerUser { UserId = 2010002 }, Id = new TVector<int> { 42, 42 } };
        if (accepted) (await Invoke(handler, request)).ShouldBeOfType<TBoolTrue>();
        else (await Should.ThrowAsync<RpcException>(() => Invoke(handler, request))).RpcError.Message.ShouldBe("MSG_ID_INVALID");
        reports.Verify(r => r.SaveAsync(Caller, Channel, 42, false, 2010002, It.IsAny<CancellationToken>()), accepted ? Times.Once() : Times.Never());
        query.Verify(q => q.ProcessAsync(It.IsAny<GetMessagesByIdListQuery>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>The handler enforces the minimum this instance advertises (10), not Telegram's 200.</summary>
    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    public async Task Toggle_enforces_the_advertised_group_size_minimum(int size, bool accepted)
    {
        var appConfig = new MyTelegram.Messenger.Services.Impl.AppConfigHelper();
        appConfig.GetInt32Value("telegram_antispam_group_size_min", 0).ShouldBe(10);
        var bus = new Mock<ICommandBus>();
        var handler = Create("ToggleAntiSpamHandler", Access(size).Object, new Mock<IAntiSpamActorService>().Object, appConfig, bus.Object);
        var request = new RequestToggleAntiSpam { Channel = new TInputChannel { ChannelId = Channel }, Enabled = true };
        if (accepted)
            (await Invoke(handler, request)).ShouldBeNull();
        else
            (await Should.ThrowAsync<RpcException>(() => Invoke(handler, request))).RpcError.Message.ShouldBe("PARTICIPANTS_TOO_FEW");
    }

    private static Mock<IAntiSpamAccessService> Access(int size = 200)
    {
        var channel = new Mock<IChannelReadModel>();
        channel.SetupGet(c => c.ChannelId).Returns(Channel);
        channel.SetupGet(c => c.ParticipantsCount).Returns(size);
        var access = new Mock<IAntiSpamAccessService>();
        access.Setup(a => a.RequireAdminAsync(It.IsAny<IRequestInput>(), It.IsAny<IInputChannel>(), It.IsAny<bool>())).ReturnsAsync(channel.Object);
        return access;
    }

    private static object Create(string name, params object[] args) => Activator.CreateInstance(
        typeof(AntiSpamAccessService).Assembly.GetType("MyTelegram.Messenger.Handlers.LatestLayer.Channels." + name)!, args)!;

    private static async Task<object?> Invoke(object handler, object request)
    {
        var input = new Mock<IRequestInput>();
        input.SetupGet(i => i.UserId).Returns(Caller);
        var method = handler.GetType().GetMethod("HandleCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(handler, [input.Object, request])!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task);
    }
}
