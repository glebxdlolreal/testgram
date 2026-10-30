using Moq;
using MyTelegram.Domain.Aggregates.Dialog;
using MyTelegram.Messenger.Services.Mentions;
using MyTelegram.ReadModel.Interfaces;

namespace MyTelegram.Messenger.Tests.Mentions;

/// <summary>
/// Feature: mentions — a deleted message gives its @ badge back.
///
/// <para>
/// The dialog counter is event-sourced, so a message that vanishes without ever being read would leave
/// the badge pointing at a mention the user can no longer reach. See
/// https://corefork.telegram.org/api/mentions
/// </para>
/// </summary>
public class MentionCleanupServiceTests
{
    private const long MentionedUserId = 100;
    private const long SenderUserId = 200;
    private const long ChannelId = 800000000001;

    [Fact]
    public async Task A_deleted_channel_mention_is_given_back()
    {
        var (service, intents, _) = CreateService();

        await service.OnMessagesDeletedAsync([ChannelMessage(10)]);

        intents.Verify(p => p.ReadAsync(MentionedUserId,
            new Peer(PeerType.Channel, ChannelId),
            It.Is<IReadOnlyCollection<int>>(ids => ids.Single() == 10),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_mention_that_was_already_read_is_not_given_back_twice()
    {
        var state = new MentionReadStateDocument { ReadMaxId = 10 };
        var (service, intents, _) = CreateService(state);

        await service.OnMessagesDeletedAsync([ChannelMessage(10)]);

        intents.Verify(p => p.ReadAsync(It.IsAny<long>(), It.IsAny<Peer>(),
            It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Only_the_mentioned_users_own_copy_of_a_private_message_counts()
    {
        var (service, intents, _) = CreateService();

        // The same private message exists twice: the sender's outbox copy and the recipient's inbox
        // copy. Counting both would take the badge down by two.
        await service.OnMessagesDeletedAsync([
            PrivateMessage(ownerPeerId: SenderUserId, toPeerId: MentionedUserId, messageId: 5),
            PrivateMessage(ownerPeerId: MentionedUserId, toPeerId: SenderUserId, messageId: 7)
        ]);

        intents.Verify(p => p.ReadAsync(MentionedUserId,
            new Peer(PeerType.User, SenderUserId),
            It.Is<IReadOnlyCollection<int>>(ids => ids.Single() == 7),
            It.IsAny<CancellationToken>()), Times.Once);
        intents.Verify(p => p.ReadAsync(It.IsAny<long>(), It.IsAny<Peer>(),
            It.Is<IReadOnlyCollection<int>>(ids => ids.Contains(5)),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_message_without_mentions_is_left_alone()
    {
        var (service, intents, readState) = CreateService();
        var message = new Mock<IMessageReadModel>();
        message.SetupGet(p => p.MentionedUserIds).Returns((List<long>?)null);

        await service.OnMessagesDeletedAsync([message.Object]);

        readState.Verify(p => p.GetAsync(It.IsAny<long>(), It.IsAny<Peer>()), Times.Never);
        intents.Verify(p => p.ReadAsync(It.IsAny<long>(), It.IsAny<Peer>(),
            It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static (MentionCleanupService Service, Mock<IMentionReadIntentService> Intents,
        Mock<IMentionReadStateService> ReadState) CreateService(MentionReadStateDocument? state = null)
    {
        var intents = new Mock<IMentionReadIntentService>();
        var readState = new Mock<IMentionReadStateService>();
        readState.Setup(p => p.GetAsync(It.IsAny<long>(), It.IsAny<Peer>())).ReturnsAsync(state);

        var service = new MentionCleanupService(intents.Object, readState.Object);

        return (service, intents, readState);
    }

    private static IMessageReadModel ChannelMessage(int messageId)
    {
        var message = new Mock<IMessageReadModel>();
        message.SetupGet(p => p.MessageId).Returns(messageId);
        message.SetupGet(p => p.OwnerPeerId).Returns(ChannelId);
        message.SetupGet(p => p.ToPeerType).Returns(PeerType.Channel);
        message.SetupGet(p => p.ToPeerId).Returns(ChannelId);
        message.SetupGet(p => p.MentionedUserIds).Returns([MentionedUserId]);

        return message.Object;
    }

    private static IMessageReadModel PrivateMessage(long ownerPeerId, long toPeerId, int messageId)
    {
        var message = new Mock<IMessageReadModel>();
        message.SetupGet(p => p.MessageId).Returns(messageId);
        message.SetupGet(p => p.OwnerPeerId).Returns(ownerPeerId);
        message.SetupGet(p => p.ToPeerType).Returns(PeerType.User);
        message.SetupGet(p => p.ToPeerId).Returns(toPeerId);
        message.SetupGet(p => p.MentionedUserIds).Returns([MentionedUserId]);

        return message.Object;
    }
}
