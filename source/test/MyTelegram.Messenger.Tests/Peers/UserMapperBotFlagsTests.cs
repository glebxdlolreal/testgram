using Moq;
using MyTelegram.Converters.Mappers.LatestLayer;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Peers;

/// <summary>
/// Feature: the bot flags @BotFather sets.
///
/// <para>
/// <c>user.bot_nochats</c> is what makes clients hide "Add to Group", and <c>user.bot_inline_placeholder</c>
/// is what makes them send inline queries at all: a bot without one is never treated as an inline bot,
/// whatever its owner switched on.
/// </para>
/// </summary>
public class UserMapperBotFlagsTests
{
    [Fact]
    public void A_bot_with_groups_turned_off_is_nochats()
    {
        Map(bot: true, noChats: true).BotNochats.ShouldBeTrue();
        Map(bot: true, noChats: false).BotNochats.ShouldBeFalse();
    }

    [Fact]
    public void BotFather_itself_cannot_be_added_to_groups()
    {
        Map(bot: true, noChats: false, userId: MyTelegramConsts.BotFatherUserId).BotNochats.ShouldBeTrue();
    }

    [Fact]
    public void The_inline_placeholder_is_served_for_bots_only()
    {
        Map(bot: true, placeholder: "Search GIFs...").BotInlinePlaceholder.ShouldBe("Search GIFs...");
        Map(bot: false, placeholder: "Search GIFs...").BotInlinePlaceholder.ShouldBeNull();
    }

    [Fact]
    public void Bot_chat_history_is_the_negation_of_privacy_mode()
    {
        Map(bot: true, privacyMode: true).BotChatHistory.ShouldBeFalse();
        Map(bot: true, privacyMode: false).BotChatHistory.ShouldBeTrue();
        Map(bot: false).BotChatHistory.ShouldBeFalse();
    }

    private static TUser Map(bool bot, bool noChats = false, string? placeholder = null, long userId = 600000000123,
        bool privacyMode = false)
    {
        var readModel = new Mock<IUserReadModel>(MockBehavior.Loose);
        readModel.SetupGet(p => p.UserId).Returns(userId);
        readModel.SetupGet(p => p.FirstName).Returns("Bot");
        readModel.SetupGet(p => p.PhoneNumber).Returns(string.Empty);
        readModel.SetupGet(p => p.Bot).Returns(bot);
        readModel.SetupGet(p => p.BotNochats).Returns(noChats);
        readModel.SetupGet(p => p.BotInlinePlaceholder).Returns(placeholder);
        readModel.SetupGet(p => p.BotPrivacyMode).Returns(privacyMode);

        return new UserMapper().Map(readModel.Object, new TUser());
    }
}
