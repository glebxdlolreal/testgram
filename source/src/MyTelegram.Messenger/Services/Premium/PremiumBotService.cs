using System.Globalization;
using System.Text;
using MongoDB.Driver;
using MyTelegram.Messenger.Options;
using MyTelegram.Messenger.Services.Payments;
using MyTelegram.Schema.Payments;

namespace MyTelegram.Messenger.Services.Premium;

/// <summary>
/// <c>@PremiumBot</c>: sells Telegram Premium with card invoices, the way the official bot does for
/// apps outside the App Store and Google Play.
/// </summary>
/// <remarks>
/// <para>Clients open it with <c>messages.startBot</c> (autostart) and a <c>start_param</c> naming
/// where the user came from: a feature id, <c>settings</c>, <c>profile</c>, a
/// <c>double_limits__*</c> id, <c>deeplink_*</c> (https://corefork.telegram.org/api/premium). Any of
/// those, or no parameter, gets a one-month invoice. A number gets that many months
/// (<c>/start 1</c> a month, <c>/start 12</c> a year, <c>/start 24</c> two) if that term is on sale,
/// as in the bot_url links help.getPremiumPromo hands out.</para>
/// <para>For an account Premium is off sale for (<see cref="IPremiumPurchaseGate"/>) the bot sells
/// nothing and answers every purchase command as if it did not understand it.</para>
/// <para>Invoices are in <see cref="PremiumOptions.Currency"/> and are paid by card through the
/// configured Stripe account; settling one grants the term (<see cref="PayAsync"/>).</para>
/// </remarks>
public interface IPremiumBotService
{
    Task HandleMessageAsync(IRequestInput input, string text);

    bool IsPremiumBotInvoice(BotInvoiceDocument storedInvoice);

    Task<IPaymentForm> BuildPaymentFormAsync(IRequestInput input, BotInvoiceDocument storedInvoice);

    Task<IPaymentResult> PayAsync(IRequestInput input, BotInvoiceDocument storedInvoice, RequestSendPaymentForm request);
}

/// <inheritdoc />
public sealed class PremiumBotService(
    IMongoDatabase mongoDatabase,
    IMessageAppService messageAppService,
    IMediaHelper mediaHelper,
    IIdGenerator idGenerator,
    IQueryProcessor queryProcessor,
    IUserConverterService userConverterService,
    IOptions<PremiumOptions> premiumOptions,
    IPremiumPurchaseGate premiumPurchaseGate,
    IPremiumSubscriptionService premiumSubscriptionService,
    IPremiumCardCheckout premiumCardCheckout,
    IBotInvoicePaymentService botInvoicePaymentService,
    ILogger<PremiumBotService> logger) : IPremiumBotService, ITransientDependency
{
    public const long BotUserId = MyTelegramConsts.PremiumBotUserId;
    private const string PayloadPrefix = "premium:";

    /// <summary>
    /// The deep link <c>premiumSubscriptionOption.bot_url</c> carries, as real Telegram sends it
    /// (<c>https://t.me/PremiumBot?start=12</c>); clients open it with an autostarted startBot.
    /// </summary>
    public static string BotUrlFor(int months) => $"https://t.me/{MyTelegramConsts.PremiumBotUserName}?start={months}";

    public async Task HandleMessageAsync(IRequestInput input, string text)
    {
        try
        {
            var userId = input.UserId;
            var russian = await IsRussianAsync(input);
            var command = text.Trim();

            if (!command.StartsWith("/start", StringComparison.OrdinalIgnoreCase) ||
                (command.Length > 6 && !char.IsWhiteSpace(command[6]) && command[6] != '@'))
            {
                await SendTextAsync(userId, NotUnderstood(russian));
                return;
            }

            var parameter = command.Length > 6 ? command[6..].Trim() : string.Empty;
            if (parameter.StartsWith('@'))
            {
                // "/start@PremiumBot 12"
                var space = parameter.IndexOf(' ');
                parameter = space < 0 ? string.Empty : parameter[(space + 1)..].Trim();
            }

            if (await premiumPurchaseGate.IsBlockedAsync(userId))
            {
                await SendTextAsync(userId, NotUnderstood(russian));
                return;
            }

            PremiumPeriodOption? period;
            if (int.TryParse(parameter, NumberStyles.None, CultureInfo.InvariantCulture, out var requestedMonths))
            {
                period = premiumOptions.Value.FindSubscription(requestedMonths);
            }
            else
            {
                // Every other start parameter only says where the user came from.
                period = premiumOptions.Value.FindSubscription(1) ??
                         premiumOptions.Value.GetPeriods().FirstOrDefault(p => p.Subscription && p.Amount > 0);
            }

            if (period == null || period.Amount <= 0)
            {
                await SendTextAsync(userId, NotUnderstood(russian));
                return;
            }

            await SendInvoiceAsync(userId, period, russian);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "PremiumBot failed to answer {UserId}: {Text}", input.UserId, text);
        }
    }

    public bool IsPremiumBotInvoice(BotInvoiceDocument storedInvoice) => storedInvoice.BotId == BotUserId;

    public async Task<IPaymentForm> BuildPaymentFormAsync(IRequestInput input, BotInvoiceDocument storedInvoice)
    {
        var months = ReadMonths(storedInvoice);
        await premiumPurchaseGate.EnsureAllowedAsync(input.UserId);

        var invoice = BotInvoiceHelper.ReadInvoice(storedInvoice);
        var form = await premiumCardCheckout.CreateAsync(input.UserId, storedInvoice.TotalAmount, storedInvoice.Currency,
            storedInvoice.Title, intent =>
            {
                intent.PremiumKind = PremiumCardCheckout.KindBotInvoice;
                intent.PremiumMonths = months;
                intent.PremiumQuantity = 1;
                intent.PremiumRecipientUserIds = [input.UserId];
                intent.BotInvoiceId = storedInvoice.Id;
            });

        // tdlib refuses a form whose seller bot or payment provider it has never seen, so both ids
        // point at the bot and the bot comes with the form.
        var bot = await userConverterService.GetUserAsync(input, BotUserId, layer: input.Layer);

        return new TPaymentForm
        {
            FormId = form.FormId,
            BotId = BotUserId,
            ProviderId = BotUserId,
            Title = storedInvoice.Title,
            Description = storedInvoice.Description,
            Photo = BotInvoiceHelper.ReadPhoto(storedInvoice),
            Invoice = invoice,
            Url = string.Empty,
            NativeProvider = form.NativeProvider,
            NativeParams = new TDataJSON { Data = form.NativeParamsJson },
            Users = new TVector<IUser>(bot)
        };
    }

    public async Task<IPaymentResult> PayAsync(IRequestInput input, BotInvoiceDocument storedInvoice, RequestSendPaymentForm request)
    {
        var intent = await premiumCardCheckout.FindAsync(request.FormId, input.UserId);
        if (intent.PremiumKind != PremiumCardCheckout.KindBotInvoice || intent.PremiumMonths <= 0 ||
            (intent.BotInvoiceId != null && intent.BotInvoiceId != storedInvoice.Id &&
             intent.BotInvoiceId != BotInvoiceHelper.MakeSlugId(storedInvoice.Slug)))
        {
            RpcErrors.RpcErrors400.PaymentProviderInvalid.ThrowRpcError();
        }

        await premiumPurchaseGate.EnsureAllowedAsync(input.UserId);
        await premiumCardCheckout.ChargeAsync(intent, request.Credentials);

        var result = await premiumSubscriptionService.GrantAsync(input.UserId, PremiumDuration.DaysFromMonths(intent.PremiumMonths),
            "premium-bot", input.UserId);

        await botInvoicePaymentService.CompleteExternalChargeAsync(input, storedInvoice, intent.PaymentIntentId);

        var russian = await IsRussianAsync(input);
        await SendTextAsync(input.UserId, russian
            ? $"Спасибо за покупку! Telegram Premium активен до {FormatDate(result.ExpiresAt)}."
            : $"Thank you for your purchase! Telegram Premium is active until {FormatDate(result.ExpiresAt)}.");

        return new TPaymentResult
        {
            Updates = new TUpdates
            {
                Updates = new TVector<IUpdate>(),
                Users = new TVector<IUser>(),
                Chats = new TVector<IChat>(),
                Date = DateTime.UtcNow.ToTimestamp(),
                Seq = 0
            }
        };
    }

    private async Task SendInvoiceAsync(long userId, PremiumPeriodOption period, bool russian)
    {
        var inputInvoice = CreateInputInvoice(period, russian);
        var media = await mediaHelper.SaveMediaAsync(inputInvoice);

        // The server side half of the invoice is keyed by the bot's own copy of the message, so its id
        // has to exist before the message is dispatched.
        var messageId = (int)await idGenerator.NextIdAsync(IdType.MessageId, BotUserId);
        var replyMarkup = BotInvoiceHelper.WithBuyButton(new TReplyInlineMarkup
        {
            Rows = new TVector<IKeyboardButtonRow>(new TKeyboardButtonRow
            {
                Buttons = new TVector<IKeyboardButton>(new TKeyboardButtonBuy
                {
                    Text = russian ? $"Оплатить {FormatPrice(period.Amount)}" : $"Pay {FormatPrice(period.Amount)}"
                })
            })
        });

        await messageAppService.SendMessageAsync([new SendMessageInput(
            BotRequest(),
            BotUserId,
            new Peer(PeerType.User, userId),
            string.Empty,
            Random.Shared.NextInt64(),
            media: media,
            sendMessageType: SendMessageType.Media,
            messageType: mediaHelper.GeMessageType(media),
            replyMarkup: replyMarkup,
            messageId: messageId)]);

        await BotInvoiceHelper.SaveAsync(mongoDatabase,
            BotInvoiceHelper.Create(inputInvoice, BotUserId, ownerPeerId: BotUserId, toPeerId: userId, msgId: messageId));
    }

    private TInputMediaInvoice CreateInputInvoice(PremiumPeriodOption period, bool russian)
    {
        var currency = premiumOptions.Value.Currency;
        var term = TermText(period.Months, russian);

        return new TInputMediaInvoice
        {
            Title = "Telegram Premium",
            Description = russian
                ? $"Подписка Telegram Premium на {term}: удвоенные лимиты, загрузка файлов до 4 ГБ, расшифровка голосовых, эксклюзивные стикеры и реакции, без рекламы и многое другое."
                : $"Telegram Premium for {term}: doubled limits, 4 GB uploads, voice-to-text, exclusive stickers and reactions, no ads and more.",
            Invoice = new TInvoice
            {
                Currency = currency,
                Prices = new TVector<ILabeledPrice>(new TLabeledPrice
                {
                    Label = $"Telegram Premium ({term})",
                    Amount = period.Amount
                })
            },
            Payload = Encoding.UTF8.GetBytes($"{PayloadPrefix}{period.Months}"),
            Provider = "stripe",
            ProviderData = new TDataJSON { Data = "{}" }
        };
    }

    private static int ReadMonths(BotInvoiceDocument storedInvoice)
    {
        var payload = Encoding.UTF8.GetString(storedInvoice.Payload);
        var months = 0;
        if (payload.StartsWith(PayloadPrefix, StringComparison.Ordinal))
        {
            int.TryParse(payload.AsSpan(PayloadPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out months);
        }

        if (months <= 0)
        {
            RpcErrors.RpcErrors400.InvoiceInvalid.ThrowRpcError();
        }

        return months;
    }

    private async Task SendTextAsync(long userId, string text)
    {
        await messageAppService.SendMessageAsync([new SendMessageInput(
            BotRequest(),
            BotUserId,
            new Peer(PeerType.User, userId),
            text,
            Random.Shared.NextInt64())]);
    }

    private async Task<bool> IsRussianAsync(IRequestInput input)
    {
        var device = input.PermAuthKeyId != 0
            ? await queryProcessor.ProcessAsync(new GetDeviceByAuthKeyIdQuery(input.PermAuthKeyId))
            : null;
        var language = string.IsNullOrEmpty(device?.LangCode) ? device?.SystemLangCode : device.LangCode;

        return language?.StartsWith("ru", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static RequestInfo BotRequest() => RequestInfo.Empty with
    {
        UserId = BotUserId,
        Layer = Layers.LayerLatest,
        Date = DateTime.UtcNow.ToTimestamp(),
        RequestId = Guid.NewGuid()
    };

    private static string NotUnderstood(bool russian) => russian
        ? "Извините, я не понял Ваш запрос. Отправьте команду /start."
        : "Sorry, I didn't understand your request. Send the /start command.";

    private static string TermText(int months, bool russian)
    {
        if (months % 12 == 0)
        {
            var years = months / 12;
            return russian ? $"{years} {Plural(years, "год", "года", "лет")}" : years == 1 ? "1 year" : $"{years} years";
        }

        return russian ? $"{months} {Plural(months, "месяц", "месяца", "месяцев")}" : months == 1 ? "1 month" : $"{months} months";
    }

    private static string Plural(int n, string one, string few, string many)
    {
        var mod100 = n % 100;
        var mod10 = n % 10;
        if (mod100 is >= 11 and <= 14)
        {
            return many;
        }

        return mod10 switch
        {
            1 => one,
            >= 2 and <= 4 => few,
            _ => many
        };
    }

    private string FormatPrice(long amount)
    {
        var currency = premiumOptions.Value.Currency.ToUpperInvariant();
        var value = (amount / 100m).ToString("0.00", CultureInfo.InvariantCulture);
        return currency == "USD" ? $"${value}" : $"{value} {currency}";
    }

    private static string FormatDate(long unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
}
