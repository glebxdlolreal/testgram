using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Handlers.LatestLayer.Payments;
using MyTelegram.Messenger.Options;
using MyTelegram.Messenger.Services.Payments;

namespace MyTelegram.Messenger.Services.Premium;

/// <summary>
/// @PremiumBot's commands beyond buying a subscription, answered the way real Telegram's bot answers
/// them (measured 2026-10-31 with and without Premium): the subscription status, <c>/stop</c> and
/// <c>/resume</c> for recurring payments, <c>/upgrade</c>, <c>/gift</c>, <c>/stars</c> and prepaid
/// Premium and Stars giveaways.
/// </summary>
/// <remarks>
/// Every purchase ends in an invoice link (<c>t.me/$…</c>) made out to the user it was offered to,
/// paid by card through <see cref="PayAsync"/>. Real Telegram's payment forms are in English
/// whatever the chat language, and so are these.
/// </remarks>
public sealed partial class PremiumBotService
{
    private const string DialogCollectionName = "premium-bot-dialogs";
    private const string GiveawayCallbackPrefix = "give/";
    private const string StarsGiveawayCallbackPrefix = "stargive/";

    // Which reply-keyboard question the user is answering.
    private const string StateResume = "resume";
    private const string StateStopConfirm = "stop-confirm";
    private const string StateStopReason = "stop-reason";
    private const string StateGift = "gift";
    private const string StateStars = "stars";
    private const string StateGiveawayPremium = "giveaway-premium";
    private const string StateGiveawayStars = "giveaway-stars";

    // keyboardButtonRequestPeer ids, as real Telegram numbers them.
    private const int ButtonGiftRecipient = 1;
    private const int ButtonChannel = 2;
    private const int ButtonGroup = 3;
    private const int ButtonStarsRecipient = 4;

    private const int MaxGiftRecipients = 10;

    /// <summary>Winner counts offered for a prepaid Premium giveaway, as on real Telegram.</summary>
    private static readonly int[] GiveawayQuantities = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 25, 50, 100];

    private static readonly string[] StopReasonsRu =
    [
        "Мало использую Premium-функции",
        "Не хватает нужных функций",
        "Слишком дорого",
        "Технические неполадки",
        "Другое (расскажите, что именно)"
    ];

    private static readonly string[] StopReasonsEn =
    [
        "I don't use Premium features much",
        "Missing features I need",
        "Too expensive",
        "Technical issues",
        "Other (tell us what)"
    ];

    private sealed record Dialog(string State, string? Data, int MessageId);

    private async Task HandleCommandAsync(long userId, string name, string parameter, bool russian)
    {
        // A new command abandons whatever the keyboard was asking.
        await SetDialogAsync(userId, null);

        switch (name)
        {
            case "start":
                if (await HasPremiumAsync(userId))
                {
                    await SendStatusAsync(userId, russian);
                    return;
                }

                int.TryParse(parameter, NumberStyles.None, CultureInfo.InvariantCulture, out var requestedMonths);
                await OfferAsync(userId, requestedMonths, russian);
                return;

            case "upgrade":
                await UpgradeAsync(userId, russian);
                return;

            case "stop":
                await StopAsync(userId, russian);
                return;

            case "resume":
                await ResumeAsync(userId, russian);
                return;

            case "gift":
                await StartGiftAsync(userId, russian);
                return;

            case "stars":
                await StartStarsAsync(userId, russian);
                return;

            case "giveaway":
                await StartGiveawayAsync(userId, stars: false, russian);
                return;

            case "stars_giveaway":
                await StartGiveawayAsync(userId, stars: true, russian);
                return;

            default:
                await SendNotUnderstoodAsync(userId, russian);
                return;
        }
    }

    private async Task HandleReplyAsync(long userId, string text, bool russian)
    {
        var state = (await GetDialogAsync(userId))?.State;
        switch (state)
        {
            case StateResume when Either(text, ResumeAgree):
                await SetDialogAsync(userId, null);
                if (await premiumAutoRenewService.SetEnabledAsync(userId, true))
                {
                    await SendAsync(userId, russian ? "Спасибо. Ваша подписка возобновлена." : "Thank you. Your subscription has been resumed.",
                        markup: new TReplyKeyboardHide());
                }
                else
                {
                    await SendCannotResumeAsync(userId, russian);
                }

                return;

            case StateResume or StateStopReason when Either(text, Cancel):
            case StateStopConfirm when Either(text, StopKeep):
                await SetDialogAsync(userId, null);
                await SendStatusAsync(userId, russian);
                return;

            case StateStopConfirm when Either(text, StopConfirm):
                await SetDialogAsync(userId, StateStopReason);
                await SendStopReasonsAsync(userId, russian);
                return;

            case StateStopReason when (StopReasonsRu.Contains(text) || StopReasonsEn.Contains(text)):
                await SetDialogAsync(userId, null);
                await premiumAutoRenewService.SetEnabledAsync(userId, false, text);
                await SendAsync(userId, russian
                        ? "Регулярные платежи отключены. Действие подписки завершится по окончании текущего периода.\n\nВы можете в любой момент возобновить подписку, отправив команду /start."
                        : "Recurring payments have been stopped. Your subscription will end when the current period expires.\n\nYou can resume your subscription at any time by sending the /start command.",
                    markup: new TReplyKeyboardHide());
                return;

            case StateGift:
                // Typed instead of picking someone: real Telegram explains how to share a contact.
                await SendAsync(userId, russian
                    ? "Пожалуйста, отправьте контакт получателя подарка как вложение. Для этого:\n\n" +
                      "В мобильных приложениях: нажмите на значок 📎 > найдите вкладку Контакт и выберите нужного пользователя.\n\n" +
                      "В Telegram Desktop: откройте чат с пользователем и нажмите на верхнюю панель с его именем. Пролистайте меню, нажмите на пункт Поделиться контактом и выберите @PremiumBot.\n\n" +
                      "В Telegram для macOS: откройте чат с пользователем и нажмите на верхнюю панель с его именем. Нажмите (…) Ещё > Отправить и выберите @PremiumBot."
                    : "Please send the gift recipient's contact as an attachment. To do this:\n\n" +
                      "In the mobile apps: tap 📎 > open the Contact tab and choose the user.\n\n" +
                      "In Telegram Desktop: open the chat with the user and click the top bar with their name. Scroll the menu, click Share Contact and choose @PremiumBot.\n\n" +
                      "In Telegram for macOS: open the chat with the user and click the top bar with their name. Click (…) More > Send and choose @PremiumBot.");
                return;

            case StateStars when Either(text, StarsBuy):
                await SetDialogAsync(userId, null);
                await SendStarsPacksAsync(userId, recipient: null, russian);
                return;

            case StateStars when Either(text, Cancel):
                await SetDialogAsync(userId, null);
                await SendAsync(userId, russian ? "Команда stars отменена." : "The stars command has been cancelled.",
                    markup: new TReplyKeyboardHide());
                return;

            case StateGiveawayPremium or StateGiveawayStars:
                // Typed instead of picking a chat with the buttons.
                await SendNoChatSelectedAsync(userId, russian);
                return;

            default:
                await SendNotUnderstoodAsync(userId, russian);
                return;
        }
    }

    public async Task HandleRequestedPeerAsync(IRequestInput input, int buttonId, IReadOnlyList<Peer> peers)
    {
        try
        {
            var userId = input.UserId;
            var russian = await IsRussianAsync(input);
            var state = (await GetDialogAsync(userId))?.State;

            switch (state)
            {
                case StateGift when buttonId == ButtonGiftRecipient:
                    await OfferGiftAsync(userId, peers.Where(p => p.PeerType == PeerType.User).Select(p => p.PeerId).ToList(), russian);
                    return;

                case StateStars when buttonId == ButtonStarsRecipient && peers.Count == 1 && peers[0].PeerType == PeerType.User:
                    var recipient = await queryProcessor.ProcessAsync(new GetUserByIdQuery(peers[0].PeerId));
                    if (recipient is not { Bot: false } || recipient.IsDeleted == true)
                    {
                        await SendNotUnderstoodAsync(userId, russian);
                        return;
                    }

                    await SetDialogAsync(userId, null);
                    await SendStarsPacksAsync(userId, recipient, russian);
                    return;

                case StateGiveawayPremium or StateGiveawayStars when buttonId is ButtonChannel or ButtonGroup:
                    var channel = peers.Count == 1 && peers[0].PeerType == PeerType.Channel
                        ? await queryProcessor.ProcessAsync(new GetChannelByIdQuery(peers[0].PeerId))
                        : null;
                    // The button asked for chats the user administers with these rights; what the client
                    // sends back is not to be trusted.
                    if (channel == null || !IsGiveawayAdmin(channel, userId))
                    {
                        await SendNoChatSelectedAsync(userId, russian);
                        return;
                    }

                    await SendAsync(userId, "⚡️", markup: new TReplyKeyboardHide());
                    var messageId = (int)await idGenerator.NextIdAsync(IdType.MessageId, BotUserId);
                    await SetDialogAsync(userId, state, channel.ChannelId.ToString(CultureInfo.InvariantCulture), messageId);
                    var (text, entities, markup) = state == StateGiveawayPremium
                        ? GiveawayTerms(channel, russian)
                        : StarsGiveawayPools(channel, russian);
                    await SendAsync(userId, text, entities, markup, messageId);
                    return;

                default:
                    await SendNotUnderstoodAsync(userId, russian);
                    return;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "PremiumBot failed to answer a chosen chat {UserId}", input.UserId);
        }
    }

    /// <summary>A contact shared while the bot is asking for a gift recipient.</summary>
    public async Task HandleContactAsync(IRequestInput input, long contactUserId)
    {
        try
        {
            var russian = await IsRussianAsync(input);
            if ((await GetDialogAsync(input.UserId))?.State != StateGift || contactUserId <= 0)
            {
                await SendNotUnderstoodAsync(input.UserId, russian);
                return;
            }

            await OfferGiftAsync(input.UserId, [contactUserId], russian);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "PremiumBot failed to answer a contact {UserId}", input.UserId);
        }
    }

    public async Task NotifyRenewalAsync(PremiumRenewalOutcome outcome)
    {
        var russian = await IsRussianAsync(outcome.UserId);
        if (outcome.Succeeded)
        {
            // Real Telegram's renewal receipt.
            var price = FormatPrice(outcome.Amount, outcome.Currency, russian);
            await SendAsync(outcome.UserId, russian
                ? $"Получен платёж на сумму {price}.\nПодписка Telegram Premium продлена до {FormatReceiptDate(outcome.ExpiresAt)}.\n\nВы можете подарить Telegram Premium 🎁  друзьям с помощью команды /gift."
                : $"We received the payment for {price}.\nYour premium subscription is now renewed until {FormatReceiptDate(outcome.ExpiresAt)}.\n\nTo 🎁 gift Telegram Premium to a friend, use the /gift command.");
            return;
        }

        if (outcome.WillRetry)
        {
            await SendAsync(outcome.UserId, russian
                ? "Не удалось выполнить автоматический платёж. Мы повторим попытку через несколько часов.\n\nПожалуйста, проверьте, что срок действия указанной карты не истёк и на ней достаточно средств."
                : "Automatic payment failed. We will try again in several hours.\n\nPlease check that the card you have provided is not expired and has a sufficient balance.");
            return;
        }

        await SendAsync(outcome.UserId, russian
            ? $"Не удалось выполнить автоматический платёж, регулярные платежи отключены. Подписка Telegram Premium завершится {FormatReceiptDate(outcome.ExpiresAt)}.\n\nЧтобы оформить её снова, отправьте команду /start."
            : $"Automatic payment failed, so recurring payments have been stopped. Your Telegram Premium subscription will end on {FormatReceiptDate(outcome.ExpiresAt)}.\n\nTo subscribe again, send the /start command.");
    }

    /// <summary>
    /// Real Telegram's receipt for a subscription paid in the bot: "Спасибо! Получен платёж на сумму …
    /// Подписка активирована и будет действовать до 11 Apr 2026 12:16:03 UTC. …".
    /// </summary>
    private Task SendSubscriptionReceiptAsync(long userId, string price, long expiresAt, bool russian)
    {
        var (text, bold1, bold2) = russian
            ? ($"Спасибо! Получен платёж на сумму {price}.\nПодписка активирована и будет действовать до {FormatReceiptDate(expiresAt)}.\n\nВы можете подарить Telegram Premium 🎁  друзьям с помощью команды /gift.",
                "активирована", "подарить Telegram Premium")
            : ($"Thank you! We received the payment for {price}.\nYour subscription is now active until {FormatReceiptDate(expiresAt)}.\n\nTo 🎁 gift Telegram Premium to a friend, use the /gift command.",
                "active", "gift Telegram Premium");
        return SendAsync(userId, text, [Bold(text, bold1), Bold(text, bold2)], new TReplyKeyboardHide());
    }

    /// <summary>"Получен платёж на сумму 9.599,00 SEK.\n\nСпасибо за покупку 50000 Звёзд Telegram!" and the new balance.</summary>
    private async Task SendStarsReceiptAsync(long userId, string price, BotPurchase purchase, bool russian)
    {
        var recipientId = purchase.Recipients.FirstOrDefault();
        if (recipientId > 0 && recipientId != userId)
        {
            var recipient = await queryProcessor.ProcessAsync(new GetUserByIdQuery(recipientId));
            var name = recipient == null ? recipientId.ToString(CultureInfo.InvariantCulture) : DisplayName(recipient);
            await SendAsync(userId, russian
                ? $"Получен платёж на сумму {price}.\n\nСпасибо! {purchase.Stars} Звёзд Telegram отправлены в подарок: {name}."
                : $"We received the payment for {price}. Thank you for gifting {purchase.Stars} Stars to {name}!");
            return;
        }

        var balance = await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, userId);
        await SendAsync(userId, russian
            ? $"Получен платёж на сумму {price}.\n\nСпасибо за покупку {purchase.Stars} Звёзд Telegram!\n\nВаш новый баланс: {balance} ⭐️"
            : $"We received the payment for {price}. Thank you for purchasing {purchase.Stars} Stars!\n\nYour new balance: {balance} ⭐️");
    }

    /// <summary>"11 Apr 2026 12:16:03 UTC", the way real Telegram's receipts write dates.</summary>
    private static string FormatReceiptDate(long unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime.ToString("d MMM yyyy HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- subscription

    /// <summary>The status real Telegram's bot answers <c>/start</c> with while Premium is active.</summary>
    private async Task SendStatusAsync(long userId, bool russian)
    {
        var renewal = await premiumAutoRenewService.GetAsync(userId);
        var expiresAt = await premiumSubscriptionService.GetExpiresAtAsync(userId);
        string text;

        if (renewal is { Enabled: true } && expiresAt.HasValue)
        {
            var next = NextPayment(renewal, expiresAt.Value, russian);
            var upgrade = renewal.Months < 24 && premiumOptions.Value.FindSubscription(24) != null;
            text = russian
                ? "Ваша подписка Telegram Premium активна.\n" +
                  $"📆 Следующий платёж: {next}.\n" +
                  "Отказаться от дальнейших платежей можно с помощью команды /stop.\n\n" +
                  (upgrade ? "Вы можете перейти на подписку с продлением раз в 2 года 🚀 со скидкой, отправив команду /upgrade.\n\n" : string.Empty) +
                  "Если Вы хотите подарить Telegram Premium 🎁 друзьям, отправьте команду /gift."
                : "You have an active Telegram Premium subscription.\n" +
                  $"📆 Next payment: {next}.\n" +
                  "You can cancel further payments with the /stop command.\n\n" +
                  (upgrade ? "You can switch to a discounted subscription renewed every 2 years 🚀 by sending the /upgrade command.\n\n" : string.Empty) +
                  "To 🎁 gift Telegram Premium to a friend, use the /gift command.";
        }
        else
        {
            text = russian
                ? "Ваша подписка Telegram Premium активна.\n\n" +
                  "Регулярные платежи отключены, по окончании текущего периода новый счёт не будет выставлен.\n\n" +
                  "Чтобы возобновить регулярные платежи, отправьте команду /resume.\n\n" +
                  "Вы можете подарить Telegram Premium 🎁  друзьям с помощью команды /gift."
                : "You have an active Telegram Premium subscription.\n\n" +
                  "Recurring payments have been stopped, so you won't be charged again after the current period expires.\n\n" +
                  "To reactivate your recurring payments, use /resume command.\n\n" +
                  "To 🎁 gift Telegram Premium to a friend, use the /gift command.";
        }

        await SendAsync(userId, text, markup: new TReplyKeyboardHide());
    }

    private async Task UpgradeAsync(long userId, bool russian)
    {
        var twoYears = premiumOptions.Value.FindSubscription(24);
        if (twoYears == null || await premiumPurchaseGate.IsBlockedAsync(userId))
        {
            await SendNotUnderstoodAsync(userId, russian);
            return;
        }

        // Without Premium real Telegram offers the two years like /start does, but with the switch
        // to monthly under it. With Premium the 24 months are paid now and added after the current
        // term (GrantAsync extends from its end); renewals continue at the new term.
        var premium = await HasPremiumAsync(userId);
        await SendInvoiceAsync(userId, twoYears, russian, upgrade: premium, offerMonthly: !premium);
    }

    private async Task StopAsync(long userId, bool russian)
    {
        var renewal = await premiumAutoRenewService.GetAsync(userId);
        if (!await HasPremiumAsync(userId) || renewal is not { Enabled: true })
        {
            await SendAsync(userId, russian ? "Регулярные платежи на этом аккаунте уже отключены." : "Recurring payments are already disabled for this account.",
                markup: new TReplyKeyboardHide());
            return;
        }

        var text = russian
            ? "У Вас есть действующая подписка Telegram Premium с автопродлением.\n\n" +
              "Вы можете отказаться от автоматического продления. В этом случае бот больше не сможет выставлять Вам счета, а действие подписки завершится по окончании текущего периода."
            : "You have an active Telegram Premium subscription with auto-renewal.\n\n" +
              "You can cancel auto-renewal. The bot will then no longer be able to charge you, and your subscription will end when the current period expires.";
        IMessageEntity[] entities = russian
            ? [Bold(text, "действующая"), Bold(text, "отказаться")]
            : [Bold(text, "active"), Bold(text, "cancel")];

        await SetDialogAsync(userId, StateStopConfirm);
        await SendAsync(userId, text, entities, Keyboard(resize: false, StopConfirm(russian), StopKeep(russian)));
    }

    private async Task SendStopReasonsAsync(long userId, bool russian)
    {
        var text = russian
            ? "Чтобы мы могли отменить продление подписки, пожалуйста, выберите причину отказа при помощи одной из кнопок. Это поможет нам улучшить Telegram Premium для всех пользователей."
            : "To cancel the renewal, please choose the reason with one of the buttons. This will help us improve Telegram Premium for everyone.";
        await SendAsync(userId, text, [Bold(text, russian ? "кнопок" : "buttons")],
            Keyboard(resize: false, [.. (russian ? StopReasonsRu : StopReasonsEn), Cancel(russian)]));
    }

    private async Task ResumeAsync(long userId, bool russian)
    {
        var renewal = await premiumAutoRenewService.GetAsync(userId);
        var expiresAt = await premiumSubscriptionService.GetExpiresAtAsync(userId);
        if (!await HasPremiumAsync(userId) || expiresAt == null || renewal is not { StripePaymentMethodId.Length: > 0 })
        {
            // Nothing to resume: no subscription, or no card it was paid with (a gift, or paid without Stripe).
            await SendCannotResumeAsync(userId, russian);
            return;
        }

        if (renewal.Enabled)
        {
            await SendStatusAsync(userId, russian);
            return;
        }

        var intro = russian
            ? "Вы собираетесь возобновить регулярные платежи за подписку Telegram Premium."
            : "You are about to resume recurring payments for your Telegram Premium subscription.";
        var next = NextPayment(renewal, expiresAt.Value, russian);
        var text = russian
            ? $"{intro}\n\n📆  Следующий платёж: {next}. \nЕсли Вы согласны с условиями и готовы возобновить подписку, нажмите кнопку ниже."
            : $"{intro}\n\n📆  Next payment: {next}. \nIf you agree to the terms and are ready to resume your subscription, tap the button below.";

        await SetDialogAsync(userId, StateResume);
        await SendAsync(userId, text, [new TMessageEntityItalic { Offset = 0, Length = intro.Length }],
            Keyboard(resize: false, ResumeAgree(russian), Cancel(russian)));
    }

    private Task SendCannotResumeAsync(long userId, bool russian) => SendAsync(userId, russian
            ? "Невозможно возобновить текущую подписку. Отправьте команду /start, чтобы оформить новую подписку Telegram Premium."
            : "The current subscription can't be resumed. Send the /start command to start a new Telegram Premium subscription.",
        markup: new TReplyKeyboardHide());

    // ---------------------------------------------------------------- /gift

    private async Task StartGiftAsync(long userId, bool russian)
    {
        if (await premiumPurchaseGate.IsBlockedAsync(userId))
        {
            await SendNotUnderstoodAsync(userId, russian);
            return;
        }

        var gift = russian ? "подарить" : "gift";
        var button = russian ? "Выбрать получателя подарка" : "Select Gift Recipient";
        var text = russian
            ? $"Чтобы {gift} 🎁 подписку Telegram Premium другому пользователю, пожалуйста, воспользуйтесь кнопкой {button}. 👇"
            : $"To {gift} 🎁 a Telegram Premium subscription to another user, please use the {button} button. 👇";

        await SetDialogAsync(userId, StateGift);
        await SendAsync(userId, text, [Bold(text, gift), Bold(text, button)], new TReplyKeyboardMarkup
        {
            Resize = true,
            Rows = new TVector<IKeyboardButtonRow>(Row(new TKeyboardButtonRequestPeer
            {
                Text = $" 👉 {button}",
                ButtonId = ButtonGiftRecipient,
                PeerType = new TRequestPeerTypeUser { Bot = false },
                MaxQuantity = MaxGiftRecipients
            }))
        });
    }

    /// <summary>The gift terms for the chosen people, each a link to its invoice.</summary>
    private async Task OfferGiftAsync(long userId, IReadOnlyList<long> recipientIds, bool russian)
    {
        var recipients = new List<IUserReadModel>();
        foreach (var id in recipientIds.Distinct().Where(id => id != userId).Take(MaxGiftRecipients))
        {
            var user = await queryProcessor.ProcessAsync(new GetUserByIdQuery(id));
            if (user is { Bot: false } && user.IsDeleted != true && !await premiumPurchaseGate.IsBlockedAsync(id))
            {
                recipients.Add(user);
            }
        }

        var gifts = GiftTerms();
        if (recipients.Count == 0 || gifts.Count == 0)
        {
            await SendNotUnderstoodAsync(userId, russian);
            return;
        }

        await SetDialogAsync(userId, null);
        await SendAsync(userId, "🎁", markup: new TReplyKeyboardHide());

        var names = string.Join(", ", recipients.Select(DisplayName));
        var head = russian
            ? $"Спасибо, контакт получен. Теперь можно выбрать подарочную подписку для пользователя {names}:\n\n"
            : $"Thank you, the contact has been received. Now you can choose a gift subscription for {names}:\n\n";
        var (text, entities) = TermList(head, gifts, russian);
        var rows = new TVector<IKeyboardButtonRow>();
        var ids = string.Join(',', recipients.Select(r => r.UserId.ToString(CultureInfo.InvariantCulture)));

        foreach (var gift in gifts)
        {
            var amount = gift.Amount * recipients.Count;
            var label = $"{recipients.Count}x Premium for {MonthsText(gift.Months, false)}";
            var slug = await SaveLinkInvoiceAsync(userId, $"premcodes{RandomHex(16)}", "Premium Gifts", "Gift to your contacts",
                label, amount, premiumOptions.Value.Currency, $"premium-gift:{gift.Months}:{ids}");
            rows.Add(Row(new TKeyboardButtonUrl
            {
                Text = $"{(russian ? "На" : "For")} {MonthsText(gift.Months, russian)} - {FormatPrice(amount, russian)}",
                Url = $"https://t.me/${slug}"
            }));
        }

        await SendAsync(userId, text, entities, new TReplyInlineMarkup { Rows = rows });
    }

    // ---------------------------------------------------------------- /stars

    private async Task StartStarsAsync(long userId, bool russian)
    {
        var balance = await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, userId);
        var amount = balance.ToString(CultureInfo.InvariantCulture);
        var (buy, gift) = russian ? ("Купить Звёзды", "Подарить") : ("Buy Stars", "Gift");
        var text = russian
            ? $"Ваш баланс: {amount} ⭐️\n\nПожалуйста, используйте кнопки ниже, чтобы {buy} себе или 🎁 {gift} их Вашим друзьям."
            : $"Your current balance: {amount} ⭐️\n\nPlease use the buttons below to {buy} for yourself or 🎁 {gift} them to your friends.";
        var balanceOffset = text.IndexOf(amount, StringComparison.Ordinal);
        var buyOffset = text.IndexOf(buy, balanceOffset + amount.Length, StringComparison.Ordinal);

        await SetDialogAsync(userId, StateStars);
        await SendAsync(userId, text,
            [
                new TMessageEntityBold { Offset = balanceOffset, Length = amount.Length },
                new TMessageEntityItalic { Offset = buyOffset, Length = buy.Length },
                new TMessageEntityItalic { Offset = text.IndexOf(gift, buyOffset + buy.Length, StringComparison.Ordinal), Length = gift.Length }
            ],
            new TReplyKeyboardMarkup
            {
                Resize = true,
                Rows = new TVector<IKeyboardButtonRow>(
                    Row(new TKeyboardButton { Text = StarsBuy(russian) }),
                    Row(new TKeyboardButtonRequestPeer
                    {
                        Text = russian ? "Подарить Звёзды Telegram" : "Gift Telegram Stars",
                        ButtonId = ButtonStarsRecipient,
                        PeerType = new TRequestPeerTypeUser { Bot = false },
                        MaxQuantity = 1
                    }),
                    Row(new TKeyboardButton { Text = Cancel(russian) }))
            });
    }

    /// <summary>The Stars packs on sale, each a link to its top-up (or, for a friend, gift) invoice.</summary>
    private async Task SendStarsPacksAsync(long userId, IUserReadModel? recipient, bool russian)
    {
        await SendAsync(userId, "⭐️", markup: new TReplyKeyboardHide());

        var rows = new TVector<IKeyboardButtonRow>();
        foreach (var pair in StripeHelper.TopupOptions.Chunk(2))
        {
            var row = new TKeyboardButtonRow { Buttons = new TVector<IKeyboardButton>() };
            foreach (var pack in pair)
            {
                var label = $"{pack.Stars} Telegram Stars";
                var slug = recipient == null
                    ? await SaveLinkInvoiceAsync(userId, $"stars{pack.Stars}", "Telegram Stars Top-Up", label, label, pack.Amount,
                        StarsCatalog.Currency, $"stars:{pack.Stars}:0")
                    : await SaveLinkInvoiceAsync(userId, $"stars_gift{recipient.UserId}_{pack.Stars}", "Telegram Stars Gift",
                        $"{label} for {DisplayName(recipient)}", label, pack.Amount, StarsCatalog.Currency,
                        $"stars:{pack.Stars}:{recipient.UserId}");
                row.Buttons.Add(new TKeyboardButtonUrl
                {
                    Text = $"{pack.Stars} ⭐️ ({FormatPrice(pack.Amount, StarsCatalog.Currency, russian)})",
                    Url = $"https://t.me/${slug}"
                });
            }

            rows.Add(row);
        }

        var (text, bold) = recipient == null
            ? russian
                ? ("Пожалуйста, нажмите на кнопку с нужным количеством Звёзд Telegram:", "количеством")
                : ("Please tap the button with the number of Telegram Stars you need:", "number")
            : russian
                ? ("Пожалуйста, выберите количество Звёзд Telegram с помощью кнопок ниже:", "количество")
                : ("Please select the quantity of Telegram Stars using the buttons below:", "quantity");
        await SendAsync(userId, text, [Bold(text, bold)], new TReplyInlineMarkup { Rows = rows });
    }

    // ---------------------------------------------------------------- giveaways

    private async Task StartGiveawayAsync(long userId, bool stars, bool russian)
    {
        if (!stars && await premiumPurchaseGate.IsBlockedAsync(userId))
        {
            await SendNotUnderstoodAsync(userId, russian);
            return;
        }

        var prize = russian
            ? stars ? "Звёзды Telegram" : "подписки Telegram Premium"
            : stars ? "Telegram Stars" : "Telegram Premium subscriptions";
        var pay = russian ? "оплатить" : "pay";
        var giveaway = russian ? "розыгрыша" : "giveaway";
        var button = russian ? "Выбрать канал" : "Select Channel";
        var footnote = russian
            ? "Перед запуском розыгрыша Вы сможете указать дополнительные каналы, на которые необходимо подписаться для участия."
            : "Before starting the giveaway, you will be able to add more channels that participants must subscribe to.";
        var text = russian
            ? $"Чтобы 💰 {pay} {prize} для проведения {giveaway} в Вашем канале, нажмите на кнопку {button}. 👇\n\n{footnote}"
            : $"To 💰 {pay} for {prize} for a {giveaway} in your channel, tap the {button} button. 👇\n\n{footnote}";

        IMessageEntity[] entities =
        [
            Bold(text, pay),
            Bold(text, giveaway),
            Bold(text, button),
            new TMessageEntityItalic { Offset = text.LastIndexOf(footnote, StringComparison.Ordinal), Length = footnote.Length }
        ];

        var markup = new TReplyKeyboardMarkup
        {
            Resize = true,
            Rows = new TVector<IKeyboardButtonRow>(
                Row(new TKeyboardButtonRequestPeer
                {
                    Text = russian ? "👉 Выбрать канал" : "👉 Select Channel",
                    ButtonId = ButtonChannel,
                    PeerType = new TRequestPeerTypeBroadcast
                    {
                        UserAdminRights = new TChatAdminRights { PostMessages = true, Other = true }
                    },
                    MaxQuantity = 1
                }),
                Row(new TKeyboardButtonRequestPeer
                {
                    Text = russian ? "👉 Выбрать группу" : "👉 Select Group",
                    ButtonId = ButtonGroup,
                    PeerType = new TRequestPeerTypeChat
                    {
                        UserAdminRights = new TChatAdminRights { DeleteMessages = true, Other = true }
                    },
                    MaxQuantity = 1
                }))
        };

        await SetDialogAsync(userId, stars ? StateGiveawayStars : StateGiveawayPremium);
        await SendAsync(userId, text, entities, markup);
    }

    private Task SendNoChatSelectedAsync(long userId, bool russian) => SendAsync(userId, russian
        ? "Чат не выбран. Нажмите на кнопку Выбрать канал или Выбрать группу и отметьте один из чатов, в которых у Вас есть права администратора."
        : "No chat selected. Tap Select Channel or Select Group and pick one of the chats where you are an admin.");

    private static bool IsGiveawayAdmin(IChannelReadModel channel, long userId)
    {
        if (channel.CreatorId == userId)
        {
            return true;
        }

        var admin = channel.AdminList.FirstOrDefault(a => a.UserId == userId);
        return admin != null && (channel.Broadcast ? admin.AdminRights.PostMessages : admin.AdminRights.DeleteMessages);
    }

    /// <summary>Premium giveaway terms, each with its saving, as callback buttons "give/&lt;channel&gt;/&lt;months&gt;".</summary>
    private (string Text, IEnumerable<IMessageEntity> Entities, IReplyMarkup Markup) GiveawayTerms(IChannelReadModel channel, bool russian)
    {
        var head = russian
            ? $"Пожалуйста, выберите срок действия подписок Telegram Premium, которые Вы хотите разыграть в {channel.Title}:\n\n"
            : $"OK! Please select the duration of the Telegram Premium subscriptions you want to give away in {channel.Title}:\n\n";
        var gifts = GiftTerms();
        var (text, entities) = TermList(head, gifts, russian);
        entities.Insert(0, Bold(head, russian ? "срок действия" : "duration"));

        var rows = new TVector<IKeyboardButtonRow>(gifts.Select(gift => (IKeyboardButtonRow)Row(new TKeyboardButtonCallback
        {
            Text = russian
                ? $"На {MonthsText(gift.Months, true)} - {FormatPrice(gift.Amount, true)}"
                : $"{MonthsText(gift.Months, false)} - {FormatPrice(gift.Amount, false)}",
            Data = $"{GiveawayCallbackPrefix}{channel.ChannelId}/{gift.Months}"
        })));

        return (text, entities, new TReplyInlineMarkup { Rows = rows });
    }

    /// <summary>Stars giveaway pools, largest first, as callback buttons "stargive/&lt;channel&gt;/&lt;stars&gt;".</summary>
    private static (string Text, IEnumerable<IMessageEntity> Entities, IReplyMarkup Markup) StarsGiveawayPools(IChannelReadModel channel,
        bool russian)
    {
        var offers = StarsCatalog.GiveawayOffers.OrderByDescending(o => o.Stars).ToList();
        var head = russian
            ? $"Отлично! Пожалуйста, выберите количество Звёзд Telegram, которые Вы хотите разыграть в {channel.Title}:\n\n"
            : $"OK! Please select the quantity of the Telegram Stars you want to give away in {channel.Title}:\n\n";
        var lines = offers.Select(o => russian
            ? $"{o.Stars} {Plural((int)(o.Stars % 100), "звезда", "звезды", "звёзд")}, {o.YearlyBoosts} {Plural(o.YearlyBoosts, "буст", "буста", "бустов")}"
            : $"{o.Stars} stars, {o.YearlyBoosts} {(o.YearlyBoosts == 1 ? "boost" : "boosts")}");
        var text = head + string.Join('\n', lines);

        var rows = new TVector<IKeyboardButtonRow>(offers.Chunk(2).Select(pair => (IKeyboardButtonRow)new TKeyboardButtonRow
        {
            Buttons = new TVector<IKeyboardButton>(pair.Select(o => (IKeyboardButton)new TKeyboardButtonCallback
            {
                Text = $"{o.Stars} ⭐️ - {FormatPrice(o.Amount, StarsCatalog.Currency, russian)}",
                Data = $"{StarsGiveawayCallbackPrefix}{channel.ChannelId}/{o.Stars}"
            }))
        }));

        return (text, [Bold(head, russian ? "количество" : "quantity")], new TReplyInlineMarkup { Rows = rows });
    }

    /// <summary>
    /// "give/…" and "stargive/…" presses: the choice message is edited in place into the next step,
    /// or back to the first one when the data carries no second value.
    /// </summary>
    private async Task HandleGiveawayCallbackAsync(long userId, string data, bool russian)
    {
        var stars = data.StartsWith(StarsGiveawayCallbackPrefix, StringComparison.Ordinal);
        var parts = data[(stars ? StarsGiveawayCallbackPrefix : GiveawayCallbackPrefix).Length..].Split('/');
        var dialog = await GetDialogAsync(userId);
        if (dialog == null || dialog.State != (stars ? StateGiveawayStars : StateGiveawayPremium) || dialog.MessageId == 0 ||
            parts[0] != dialog.Data ||
            !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var channelId) ||
            await queryProcessor.ProcessAsync(new GetChannelByIdQuery(channelId)) is not { } channel ||
            !IsGiveawayAdmin(channel, userId) ||
            (!stars && await premiumPurchaseGate.IsBlockedAsync(userId)))
        {
            await SendNotUnderstoodAsync(userId, russian);
            return;
        }

        long choice = 0;
        if (parts.Length > 1 && !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out choice))
        {
            await SendNotUnderstoodAsync(userId, russian);
            return;
        }

        (string Text, IEnumerable<IMessageEntity> Entities, IReplyMarkup Markup)? step = (stars, choice) switch
        {
            (false, 0) => GiveawayTerms(channel, russian),
            (true, 0) => StarsGiveawayPools(channel, russian),
            (false, _) => await GiveawayQuantitiesAsync(userId, channel, (int)choice, russian),
            (true, _) => await StarsGiveawayWinnersAsync(userId, channel, choice, russian)
        };

        if (step == null)
        {
            await SendNotUnderstoodAsync(userId, russian);
            return;
        }

        await EditAsync(userId, dialog.MessageId, step.Value.Text, step.Value.Entities, step.Value.Markup);
    }

    private async Task<(string Text, IEnumerable<IMessageEntity> Entities, IReplyMarkup Markup)?> GiveawayQuantitiesAsync(long userId,
        IChannelReadModel channel, int months, bool russian)
    {
        if (premiumOptions.Value.FindGift(months) is not { Amount: > 0 } gift)
        {
            return null;
        }

        var rows = new TVector<IKeyboardButtonRow>();
        foreach (var pair in GiveawayQuantities.Chunk(2))
        {
            var row = new TKeyboardButtonRow { Buttons = new TVector<IKeyboardButton>() };
            foreach (var quantity in pair)
            {
                var amount = gift.Amount * quantity;
                var slug = await SaveLinkInvoiceAsync(userId, $"premgiveprep{channel.ChannelId}_{months}_{quantity}", "Channel Giveaway",
                    $"Get more boosts for your channel by creating a giveaway in {channel.Title}.",
                    $"{quantity}x Premium for {MonthsText(months, false)}", amount, premiumOptions.Value.Currency,
                    $"premium-giveaway:{channel.ChannelId}:{months}:{quantity}");
                row.Buttons.Add(new TKeyboardButtonUrl
                {
                    Text = $"{quantity}x {months}{(russian ? "м." : "mo")} ({FormatPrice(amount, russian)})",
                    Url = $"https://t.me/${slug}"
                });
            }

            rows.Add(row);
        }

        rows.Add(Row(new TKeyboardButtonCallback
        {
            Text = russian ? "« Изменить срок подписки" : "« Back to duration",
            Data = $"{GiveawayCallbackPrefix}{channel.ChannelId}"
        }));

        var (text, bold) = russian
            ? ("Пожалуйста, выберите количество призовых подписок Telegram Premium, которые будут разыграны каналом:", "количество")
            : ("Perfect! Now please select the quantity of the Telegram Premium subscriptions for your giveaway:", "quantity");
        return (text, [Bold(text, bold)], new TReplyInlineMarkup { Rows = rows });
    }

    private async Task<(string Text, IEnumerable<IMessageEntity> Entities, IReplyMarkup Markup)?> StarsGiveawayWinnersAsync(long userId,
        IChannelReadModel channel, long stars, bool russian)
    {
        if (StarsCatalog.FindGiveaway(stars) is not { } offer)
        {
            return null;
        }

        var price = FormatPrice(offer.Amount, StarsCatalog.Currency, russian);
        var buttons = new List<IKeyboardButton>();
        foreach (var (users, perUserStars) in offer.Winners)
        {
            var boosts = offer.YearlyBoosts;
            var slug = await SaveLinkInvoiceAsync(userId, $"stargiveprep{channel.ChannelId}_{stars}_{users}", "Channel Giveaway",
                $"Get {boosts} {(boosts == 1 ? "boost" : "boosts")} for your channel by creating a giveaway in {channel.Title}.",
                $"{stars} stars shared between {users} winners", offer.Amount, StarsCatalog.Currency,
                $"stars-giveaway:{channel.ChannelId}:{stars}:{users}");
            buttons.Add(new TKeyboardButtonUrl
            {
                Text = russian
                    ? $"{users} {Plural(users, "победитель", "победителя", "победителей")}, {perUserStars} ⭐️ каждому"
                    : $"{users} {(users == 1 ? "winner" : "winners")}, {perUserStars} ⭐️ each",
                Url = $"https://t.me/${slug}"
            });
        }

        // Two to a row, the last one alone when the count is odd, as on real Telegram.
        var rows = new TVector<IKeyboardButtonRow>(buttons.Chunk(2).Select(pair => (IKeyboardButtonRow)new TKeyboardButtonRow
        {
            Buttons = new TVector<IKeyboardButton>(pair)
        }));
        rows.Add(Row(new TKeyboardButtonCallback
        {
            Text = russian ? "« Изменить количество звёзд" : "« Change the number of stars",
            Data = $"{StarsGiveawayCallbackPrefix}{channel.ChannelId}"
        }));

        var (lead, bold) = russian
            ? ("Спасибо! \nЭто стоит ", "количество")
            : ("Thank you! \nThis costs ", "number");
        var text = russian
            ? $"{lead}{price}.\nТеперь можно выбрать количество победителей розыгрыша, между которыми будут распределены звёзды:"
            : $"{lead}{price}.\nNow you can choose the number of giveaway winners the stars will be shared between:";
        return (text,
            [new TMessageEntityBold { Offset = lead.Length, Length = price.Length }, Bold(text, bold)],
            new TReplyInlineMarkup { Rows = rows });
    }

    /// <summary>
    /// A paid giveaway link: the prize waits on the channel as a prepaid giveaway
    /// (<c>premium.getBoostsStatus</c> lists it) until an admin launches it with
    /// <c>payments.launchPrepaidGiveaway</c>.
    /// </summary>
    private async Task PrepayGiveawayAsync(long userId, BotPurchase purchase, string price, bool russian)
    {
        var stars = purchase.Kind == PurchaseKind.StarsGiveaway;
        var document = new BsonDocument
        {
            ["GiveawayId"] = Random.Shared.NextInt64(1, long.MaxValue),
            ["ChannelId"] = purchase.ChannelId,
            ["Status"] = "prepaid",
            ["Type"] = stars ? "stars" : "premium",
            ["Winners"] = purchase.Quantity,
            ["UntilDate"] = 0,
            ["PurchasedBy"] = userId,
            ["CreatedAt"] = DateTime.UtcNow
        };
        if (stars)
        {
            document["Stars"] = purchase.Stars;
            document["Boosts"] = StarsCatalog.FindGiveaway(purchase.Stars)?.YearlyBoosts ?? 0;
        }
        else
        {
            document["Months"] = purchase.Months;
        }

        await mongoDatabase.GetCollection<BsonDocument>("giveaways").InsertOneAsync(document);

        // Real Telegram's receipt (measured 2026-10-31).
        var channel = await queryProcessor.ProcessAsync(new GetChannelByIdQuery(purchase.ChannelId));
        await SendAsync(userId, russian
            ? $"Спасибо! Получен платёж на сумму {price}.\nПредоплаченный розыгрыш готов к активации в канале {channel?.Title}.\n\n" +
              "Перед запуском розыгрыша Вы сможете указать дополнительные каналы, на которые необходимо подписаться для участия.\n\n" +
              "Начать розыгрыш или посмотреть количество и срок действия голосов, которые канал получил, можно в профиле канала > Статистика > Голоса."
            : $"Thank you! We received the payment for {price}.\nThe prepaid giveaway is ready to be launched in the channel {channel?.Title}.\n\n" +
              "Before starting the giveaway, you will be able to add more channels that participants must subscribe to.\n\n" +
              "You can start the giveaway or see the number and duration of the boosts the channel received in the channel profile > Statistics > Boosts.");
    }

    // ---------------------------------------------------------------- helpers

    private List<PremiumPeriodOption> GiftTerms() =>
        premiumOptions.Value.GetPeriods().Where(p => p.Gift && p.Amount > 0).OrderByDescending(p => p.Months).ToList();

    /// <summary>
    /// " 12 месяцев: -39% <s>4 196,00 RUB</s>" lines: the saving of each term against buying the shortest
    /// one repeatedly, as real Telegram lists gift and giveaway terms.
    /// </summary>
    private (string Text, List<IMessageEntity> Entities) TermList(string head, IReadOnlyList<PremiumPeriodOption> gifts, bool russian)
    {
        var text = new StringBuilder(head);
        var entities = new List<IMessageEntity>();
        var shortest = gifts[^1];
        foreach (var gift in gifts)
        {
            text.Append(russian ? " " : string.Empty).Append($"{MonthsText(gift.Months, russian)}: ");
            var undiscounted = shortest.Amount * gift.Months / shortest.Months;
            var saving = (int)Math.Floor((1 - (decimal)gift.Amount / undiscounted) * 100);
            if (gift != shortest && saving > 0)
            {
                var percent = $"-{saving}%";
                entities.Add(new TMessageEntityBold { Offset = text.Length, Length = percent.Length });
                text.Append(percent).Append(' ');
                var full = FormatPrice(undiscounted, russian);
                entities.Add(new TMessageEntityStrike { Offset = text.Length, Length = full.Length });
                text.Append(full);
            }
            else
            {
                text.Append(russian ? "без скидки" : "no discount");
            }

            text.Append('\n');
        }

        return (text.ToString().TrimEnd('\n'), entities);
    }

    /// <summary>
    /// Stores the invoice behind a <c>t.me/$&lt;slug&gt;</c> link, made out to <paramref name="userId"/>;
    /// the slug ends in a MAC over the user and the offer, so it cannot be guessed or reused by another account.
    /// </summary>
    private async Task<string> SaveLinkInvoiceAsync(long userId, string body, string title, string description, string label,
        long amount, string currency, string payload)
    {
        var slug = $"{body}_{LinkMac(userId, body)}";
        var options = premiumOptions.Value;
        var document = BotInvoiceHelper.Create(new TInputMediaInvoice
        {
            Title = title,
            Description = description,
            Photo = string.IsNullOrEmpty(options.InvoicePhotoUrl)
                ? null
                : new TInputWebDocument
                {
                    Url = options.InvoicePhotoUrl,
                    Size = options.InvoicePhotoSize,
                    MimeType = options.InvoicePhotoMimeType,
                    Attributes = new TVector<IDocumentAttribute>(new TDocumentAttributeImageSize
                    {
                        W = options.InvoicePhotoWidth,
                        H = options.InvoicePhotoHeight
                    })
                },
            Invoice = new TInvoice
            {
                Currency = currency,
                Prices = new TVector<ILabeledPrice>(new TLabeledPrice { Label = label, Amount = amount })
            },
            Payload = Encoding.UTF8.GetBytes(payload),
            Provider = "stripe",
            ProviderData = new TDataJSON { Data = "{}" }
        }, BotUserId, ownerPeerId: BotUserId, toPeerId: userId, msgId: 0);
        document.Slug = slug;
        document.Id = BotInvoiceHelper.MakeSlugId(slug);
        await BotInvoiceHelper.SaveAsync(mongoDatabase, document);

        return slug;
    }

    private string LinkMac(long userId, string body)
    {
        var key = Encoding.UTF8.GetBytes(serverOptions.CurrentValue.EncryptionConfig?.MessageKeys?.FirstOrDefault()?.Key ??
                                         MyTelegramConsts.PremiumBotUserName);
        var mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{userId}:{body}"));
        return Convert.ToHexStringLower(mac.AsSpan(0, 9));
    }

    private static string RandomHex(int bytes) => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(bytes));

    private string NextPayment(PremiumAutoRenewalDocument renewal, long expiresAt, bool russian) =>
        $"{FormatPrice(renewal.Amount, renewal.Currency, russian)}, {FormatDate(expiresAt)}";

    private async Task<bool> HasPremiumAsync(long userId) =>
        (await queryProcessor.ProcessAsync(new GetUserByIdQuery(userId)))?.Premium == true;

    private static string DisplayName(IUserReadModel user) =>
        string.IsNullOrEmpty(user.LastName) ? user.FirstName : $"{user.FirstName} {user.LastName}";

    private async Task<Dialog?> GetDialogAsync(long userId)
    {
        var document = await mongoDatabase.GetCollection<BsonDocument>(DialogCollectionName)
            .Find(Builders<BsonDocument>.Filter.Eq("_id", userId))
            .FirstOrDefaultAsync();
        if (document?.GetValue("State", BsonNull.Value) is not { IsString: true } state)
        {
            return null;
        }

        return new Dialog(state.AsString,
            document.GetValue("Data", BsonNull.Value) is { IsString: true } data ? data.AsString : null,
            document.GetValue("MessageId", 0).ToInt32());
    }

    private Task SetDialogAsync(long userId, string? state, string? data = null, int messageId = 0)
    {
        var collection = mongoDatabase.GetCollection<BsonDocument>(DialogCollectionName);
        var filter = Builders<BsonDocument>.Filter.Eq("_id", userId);
        return state == null
            ? collection.DeleteOneAsync(filter)
            : collection.ReplaceOneAsync(filter, new BsonDocument
            {
                ["_id"] = userId,
                ["State"] = state,
                ["Data"] = data == null ? BsonNull.Value : data,
                ["MessageId"] = messageId,
                ["UpdatedAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            }, new ReplaceOptions { IsUpsert = true });
    }

    /// <summary>A text message from the bot; every <c>/command</c> in it is marked the way real Telegram marks them.</summary>
    private async Task SendAsync(long userId, string text, IEnumerable<IMessageEntity>? entities = null, IReplyMarkup? markup = null,
        int? messageId = null)
    {
        await messageAppService.SendMessageAsync([new SendMessageInput(
            BotRequest(),
            BotUserId,
            new Peer(PeerType.User, userId),
            text,
            Random.Shared.NextInt64(),
            entities: WithCommandEntities(text, entities),
            replyMarkup: markup,
            messageId: messageId)]);
    }

    /// <summary>Edits one of the bot's own messages to <paramref name="userId"/>, the way bots edit their keyboards.</summary>
    private async Task EditAsync(long userId, int botMessageId, string text, IEnumerable<IMessageEntity>? entities, IReplyMarkup? markup)
    {
        var message = text;
        byte[]? encryptedData = null;
        byte[]? inboxEncryptedData = null;
        var encryption = serverOptions.CurrentValue.EncryptionConfig;
        if (encryption is { Enabled: true, MessageKeys.Count: > 0 })
        {
            // Stored the way EditMessageHandler stores an edited text: encrypted per owner, never in clear.
            var keyConfig = encryption.MessageKeys[0];
            var key = Encoding.UTF8.GetBytes(keyConfig.Key);
            encryptedData = dataEncryptionHelper.Encrypt(keyConfig.Id, key, BotUserId, text);
            inboxEncryptedData = dataEncryptionHelper.Encrypt(keyConfig.Id, key, userId, text);
            message = string.Empty;
        }

        await commandBus.PublishAsync(new EditOutboxMessageCommand(MessageId.Create(BotUserId, botMessageId), BotRequest(), botMessageId,
            message, DateTime.UtcNow.ToTimestamp(), WithCommandEntities(text, entities), null, markup, false,
            messageAppService.GetHashtags(text), encryptedData, inboxEncryptedData));
    }

    private static TVector<IMessageEntity>? WithCommandEntities(string text, IEnumerable<IMessageEntity>? entities)
    {
        var all = new List<IMessageEntity>(entities ?? []);
        foreach (Match match in BotCommandRegex().Matches(text))
        {
            all.Add(new TMessageEntityBotCommand { Offset = match.Index, Length = match.Length });
        }

        all.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        return all.Count == 0 ? null : new TVector<IMessageEntity>(all);
    }

    private static TKeyboardButtonRow Row(IKeyboardButton button) => new() { Buttons = new TVector<IKeyboardButton>(button) };

    private static TReplyKeyboardMarkup Keyboard(bool resize, params string[] buttons) => new()
    {
        Resize = resize,
        Rows = new TVector<IKeyboardButtonRow>(buttons.Select(text => (IKeyboardButtonRow)Row(new TKeyboardButton { Text = text })))
    };

    private static TMessageEntityBold Bold(string text, string part) =>
        new() { Offset = text.IndexOf(part, StringComparison.Ordinal), Length = part.Length };

    private static string MonthsText(int months, bool russian) => russian
        ? $"{months} {Plural(months, "месяц", "месяца", "месяцев")}"
        : months == 1 ? "1 month" : $"{months} months";

    private static string UpgradeNote(bool russian) => russian
        ? "Пожалуйста, оплатите счёт выше, чтобы 🚀 подключить подписку Telegram Premium с продлением раз в 2 года.\n\n" +
          "Новый 24-месячный период начнётся сразу после окончания срока текущей подписки."
        : "Please pay the invoice above to 🚀 switch to a Telegram Premium subscription renewed every 2 years.\n\n" +
          "The new 24-month period will start right after your current subscription ends.";

    /// <summary>A reply-keyboard answer in either language: the language is read per request and may change in between.</summary>
    private static bool Either(string text, Func<bool, string> button) => text == button(true) || text == button(false);

    private static string ResumeAgree(bool russian) => russian ? "Я согласен(на), возобновите платежи" : "I agree, resume payments";
    private static string StopConfirm(bool russian) => russian ? "Да, отменить продление" : "Yes, cancel renewal";
    private static string StopKeep(bool russian) => russian ? "Не отменять продление" : "Don't cancel renewal";
    private static string StarsBuy(bool russian) => russian ? "Купить Звёзды Telegram" : "Buy Telegram Stars";
    private static string Cancel(bool russian) => russian ? "Отмена" : "Cancel";

    [GeneratedRegex(@"(?<![\w/])/[a-z_]+\b")]
    private static partial Regex BotCommandRegex();
}
