using EventFlow.Queries;
using Moq;
using MyTelegram.Messenger.Services.Bots;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Queries;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Bots;

/// <summary>
/// Feature: group privacy mode (@BotFather's /setprivacy).
///
/// <para>
/// "'Enable' - your bot will only receive messages that either start with the '/' symbol or mention the
/// bot by username." Bot admins and channels are not affected, and neither are bots that have it off —
/// which is every bot created before the setting existed.
/// </para>
/// </summary>
public class BotGroupPrivacyFilterTests
{
    private const long ChannelId = 1000;
    private const long PrivateBot = 600000000101;
    private const long OpenBot = 600000000102;
    private const long AdminBot = 600000000103;
    private const long Sender = 2010001;

    [Fact]
    public async Task A_plain_message_skips_the_bots_in_privacy_mode()
    {
        (await Excluded(Message("hello"))).ShouldBe([PrivateBot]);
    }

    [Fact]
    public async Task A_command_reaches_every_bot()
    {
        (await Excluded(Message("/start"))).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_mentioned_bot_gets_the_message()
    {
        (await Excluded(Message("hi @privatebot"), mentioned: [PrivateBot])).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_reply_to_the_bot_reaches_it()
    {
        var item = Message("thanks") with
        {
            InputReplyTo = new TInputReplyToMessage { ReplyToMsgId = 7 }
        };

        (await Excluded(item, replyToSender: PrivateBot)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Service_messages_and_channels_are_not_filtered()
    {
        (await Excluded(Message("x") with { MessageAction = new TMessageActionChatEditTitle { Title = "t" } }))
            .ShouldBeEmpty();
        (await Excluded(Message("hello"), broadcast: true)).ShouldBeEmpty();
    }

    private static MessageItem Message(string text) => new(
        new Peer(PeerType.Channel, ChannelId), new Peer(PeerType.Channel, ChannelId), new Peer(PeerType.User, Sender),
        Sender, 10, text, 0, 1, false);

    private static Task<List<long>> Excluded(MessageItem item, List<long>? mentioned = null, long? replyToSender = null,
        bool broadcast = false)
    {
        var members = new[]
        {
            Member(PrivateBot, isAdmin: false), Member(OpenBot, isAdmin: false), Member(AdminBot, isAdmin: true)
        };

        var queryProcessor = new Mock<IQueryProcessor>();
        queryProcessor.Setup(p => p.ProcessAsync(It.IsAny<GetBotMembersByChannelIdQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(members);
        queryProcessor.Setup(p => p.ProcessAsync(It.IsAny<GetMessageByIdQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(replyToSender == null ? null : Mock.Of<IMessageReadModel>(m => m.SenderPeerId == replyToSender.Value));

        var users = new Mock<IUserAppService>();
        users.Setup(p => p.GetListAsync(It.IsAny<IEnumerable<long>>()))
            .ReturnsAsync((IEnumerable<long> ids) => ids.Select(id => Mock.Of<IUserReadModel>(u =>
                u.UserId == id && u.Bot && u.BotPrivacyMode == (id == PrivateBot || id == AdminBot))).ToList());

        var channel = Mock.Of<IChannelReadModel>(c => c.ChannelId == ChannelId && c.Broadcast == broadcast &&
                                                      c.CreatorId == Sender && c.AdminList == new List<ChatAdmin>());

        return new BotGroupPrivacyFilter(queryProcessor.Object, users.Object).GetExcludedBotIdsAsync(channel, item, mentioned);
    }

    private static IChannelMemberReadModel Member(long userId, bool isAdmin) =>
        Mock.Of<IChannelMemberReadModel>(m => m.UserId == userId && m.IsBot && m.IsAdmin == isAdmin);
}
