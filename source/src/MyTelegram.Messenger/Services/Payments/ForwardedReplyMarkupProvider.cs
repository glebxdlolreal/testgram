using MyTelegram.Domain.Shared;

namespace MyTelegram.Messenger.Services.Payments;

/// <summary>
/// What a forwarded invoice keeps of its keyboard. An invoice sent with a <c>start_param</c> is not
/// paid from a forwarded copy: the copy's Pay button becomes a URL button that deep-links to the bot
/// with that parameter, keeping the button's text (<c>https://t.me/PremiumBot?start=1</c> under a
/// forwarded @PremiumBot invoice, measured on real Telegram 2026-10-31). Every other forwarded message
/// is sent without a keyboard, as before.
/// See https://core.telegram.org/bots/api#sendinvoice (start_parameter)
/// </summary>
public sealed class ForwardedReplyMarkupProvider(IUserAppService userAppService)
    : IForwardedReplyMarkupProvider, ITransientDependency
{
    public async Task<IReplyMarkup?> GetForwardedReplyMarkupAsync(MessageItem original)
    {
        if (original.Media is not TMessageMediaInvoice { StartParam: { Length: > 0 } startParam } ||
            original.ReplyMarkup is not TReplyInlineMarkup { Rows.Count: > 0 } markup)
        {
            return null;
        }

        var buy = markup.Rows.SelectMany(row => row.Buttons ?? []).OfType<TKeyboardButtonBuy>().FirstOrDefault();
        if (buy == null)
        {
            return null;
        }

        var bot = await userAppService.GetAsync(original.SenderUserId);
        if (bot is not { Bot: true } || string.IsNullOrEmpty(bot.UserName))
        {
            return null;
        }

        return new TReplyInlineMarkup
        {
            Rows = new TVector<IKeyboardButtonRow>(new TKeyboardButtonRow
            {
                Buttons = new TVector<IKeyboardButton>(new TKeyboardButtonUrl
                {
                    Text = buy.Text,
                    Url = $"https://t.me/{bot.UserName}?start={Uri.EscapeDataString(startParam)}"
                })
            })
        };
    }
}
