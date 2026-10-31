using Moq;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Payments;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Premium;

/// <summary>
/// A forwarded invoice with a start_param links back to its bot instead of offering to pay, keeping
/// the Pay button's text (real Telegram, forwarded @PremiumBot invoice, 2026-10-31).
/// </summary>
public class ForwardedInvoiceMarkupTests
{
    private const long BotId = 600900000003;

    [Fact]
    public async Task The_pay_button_becomes_a_deep_link_with_the_start_param()
    {
        var markup = await Provider().GetForwardedReplyMarkupAsync(Item("1"));

        var button = markup.ShouldBeOfType<TReplyInlineMarkup>().Rows.ShouldHaveSingleItem().Buttons.ShouldHaveSingleItem()
            .ShouldBeOfType<TKeyboardButtonUrl>();
        button.Text.ShouldBe("Заплатить 299,00 RUB");
        button.Url.ShouldBe("https://t.me/PremiumBot?start=1");
    }

    [Fact]
    public async Task Without_a_start_param_or_an_invoice_nothing_is_kept()
    {
        (await Provider().GetForwardedReplyMarkupAsync(Item(string.Empty))).ShouldBeNull();
        (await Provider().GetForwardedReplyMarkupAsync(Item("1") with { Media = null })).ShouldBeNull();
    }

    private static ForwardedReplyMarkupProvider Provider()
    {
        var bot = new Mock<IUserReadModel>();
        bot.SetupGet(u => u.Bot).Returns(true);
        bot.SetupGet(u => u.UserName).Returns("PremiumBot");
        var users = new Mock<IUserAppService>();
        users.Setup(u => u.GetAsync(BotId)).ReturnsAsync(bot.Object);
        return new ForwardedReplyMarkupProvider(users.Object);
    }

    private static MessageItem Item(string startParam) => new(
        new Peer(PeerType.User, 2010001),
        new Peer(PeerType.User, 2010001),
        new Peer(PeerType.User, BotId),
        BotId,
        10,
        string.Empty,
        0,
        1,
        false,
        Media: new TMessageMediaInvoice
        {
            Title = "Telegram Premium", Description = string.Empty, Currency = "RUB", TotalAmount = 29900,
            StartParam = startParam
        },
        ReplyMarkup: new TReplyInlineMarkup
        {
            Rows = new TVector<IKeyboardButtonRow>(new TKeyboardButtonRow
            {
                Buttons = new TVector<IKeyboardButton>(new TKeyboardButtonBuy { Text = "Заплатить 299,00 RUB" })
            })
        });
}
