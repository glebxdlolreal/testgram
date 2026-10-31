using System.Globalization;
using System.Text;
using MongoDB.Driver;
using MyTelegram.Messenger.Options;
using MyTelegram.Messenger.Services.Payments;
using MyTelegram.Messenger.Services.WebFiles;
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
/// those, no parameter, or a term that is not on sale gets a one-month invoice; <c>/start 12</c> a
/// year and <c>/start 24</c> two, as in the bot_url links help.getPremiumPromo hands out.</para>
/// <para>Every invoice is answered the way real Telegram's bot answers it (measured 2026-10-31): the
/// invoice with the Telegram picture and a <c>start_param</c> of its term, then "Pay the invoice
/// above…" with a button that switches between the monthly and the two-year term
/// (<c>init/24M/…</c>, <c>init/1M/…</c>). Anything that is not <c>/start</c> is not understood.</para>
/// <para>For an account Premium is off sale for (<see cref="IPremiumPurchaseGate"/>) the bot sells
/// nothing and answers every purchase command as if it did not understand it.</para>
/// <para>Invoices are in <see cref="PremiumOptions.Currency"/> and are paid by card through the
/// configured Stripe account; settling one grants the term (<see cref="PayAsync"/>).</para>
/// </remarks>
public interface IPremiumBotService
{
    Task HandleMessageAsync(IRequestInput input, string text);

    /// <summary>A press on one of the bot's inline buttons; answered by sending the chosen invoice.</summary>
    Task HandleCallbackAsync(IRequestInput input, string data);

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
    IWebFileRegistrar webFileRegistrar,
    IWebDocumentProxy webDocumentProxy,
    ILogger<PremiumBotService> logger) : IPremiumBotService, ITransientDependency
{
    public const long BotUserId = MyTelegramConsts.PremiumBotUserId;
    private const string PayloadPrefix = "premium:";
    private const string CallbackPrefix = "init/";

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
                await SendNotUnderstoodAsync(userId, russian);
                return;
            }

            var parameter = command.Length > 6 ? command[6..].Trim() : string.Empty;
            if (parameter.StartsWith('@'))
            {
                // "/start@PremiumBot 12"
                var space = parameter.IndexOf(' ');
                parameter = space < 0 ? string.Empty : parameter[(space + 1)..].Trim();
            }

            int.TryParse(parameter, NumberStyles.None, CultureInfo.InvariantCulture, out var requestedMonths);
            await OfferAsync(userId, requestedMonths, russian);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "PremiumBot failed to answer {UserId}: {Text}", input.UserId, text);
        }
    }

    public async Task HandleCallbackAsync(IRequestInput input, string data)
    {
        try
        {
            // "init/24M/<id>": the term the button switches to.
            var russian = await IsRussianAsync(input);
            var months = 0;
            if (data.StartsWith(CallbackPrefix, StringComparison.Ordinal))
            {
                var term = data.AsSpan(CallbackPrefix.Length);
                var end = term.IndexOf('M');
                if (end > 0)
                {
                    int.TryParse(term[..end], NumberStyles.None, CultureInfo.InvariantCulture, out months);
                }
            }

            if (months <= 0)
            {
                await SendNotUnderstoodAsync(input.UserId, russian);
                return;
            }

            await OfferAsync(input.UserId, months, russian);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "PremiumBot failed to answer a button {UserId}: {Data}", input.UserId, data);
        }
    }

    /// <summary>The invoice for <paramref name="requestedMonths"/>, or for a month when that term is not sold.</summary>
    private async Task OfferAsync(long userId, int requestedMonths, bool russian)
    {
        if (await premiumPurchaseGate.IsBlockedAsync(userId))
        {
            await SendNotUnderstoodAsync(userId, russian);
            return;
        }

        var options = premiumOptions.Value;
        var period = (requestedMonths > 0 ? options.FindSubscription(requestedMonths) : null) ??
                     options.FindSubscription(1) ??
                     options.GetPeriods().FirstOrDefault(p => p.Subscription && p.Amount > 0);
        if (period == null)
        {
            await SendNotUnderstoodAsync(userId, russian);
            return;
        }

        await SendInvoiceAsync(userId, period, russian);
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
        var photo = await GetInvoicePhotoAsync(inputInvoice.Photo);
        if (media is TMessageMediaInvoice mediaInvoice)
        {
            mediaInvoice.Photo = photo;
        }

        // The server side half of the invoice is keyed by the bot's own copy of the message, so its id
        // has to exist before the message is dispatched; the note after it takes the next one.
        var messageId = (int)await idGenerator.NextIdAsync(IdType.MessageId, BotUserId);
        var noteId = (int)await idGenerator.NextIdAsync(IdType.MessageId, BotUserId);
        var replyMarkup = BotInvoiceHelper.WithBuyButton(new TReplyInlineMarkup
        {
            Rows = new TVector<IKeyboardButtonRow>(new TKeyboardButtonRow
            {
                Buttons = new TVector<IKeyboardButton>(new TKeyboardButtonBuy
                {
                    Text = (russian ? "Заплатить " : "Pay ") + FormatPrice(period.Amount, russian)
                })
            })
        });

        var note = russian
            ? "Оплатите счёт выше, чтобы получить доступ к эксклюзивным функциям, доступным только для подписчиков Telegram Premium."
            : "Pay the invoice above to get access to exclusive features available only to Telegram Premium subscribers.";
        var noteBold = russian ? "Оплатите счёт выше".Length : "Pay the invoice above".Length;

        // One command, so the note can never overtake the invoice it refers to.
        await messageAppService.SendMessageAsync([
            new SendMessageInput(
                BotRequest(),
                BotUserId,
                new Peer(PeerType.User, userId),
                string.Empty,
                Random.Shared.NextInt64(),
                media: media,
                sendMessageType: SendMessageType.Media,
                messageType: mediaHelper.GeMessageType(media),
                replyMarkup: replyMarkup,
                messageId: messageId),
            new SendMessageInput(
                BotRequest(),
                BotUserId,
                new Peer(PeerType.User, userId),
                note,
                Random.Shared.NextInt64(),
                entities: new TVector<IMessageEntity>(new TMessageEntityBold { Offset = 0, Length = noteBold }),
                replyMarkup: SwitchTermMarkup(period, noteId, russian),
                messageId: noteId)
        ]);

        var document = BotInvoiceHelper.Create(inputInvoice, BotUserId, ownerPeerId: BotUserId, toPeerId: userId, msgId: messageId);
        document.Photo = photo?.ToBytes();
        await BotInvoiceHelper.SaveAsync(mongoDatabase, document);
    }

    /// <summary>
    /// Under a monthly invoice, a switch to two years with its saving per month; under a yearly one, a
    /// switch to monthly at its price; under the two-year one nothing (real Telegram hides the keyboard).
    /// </summary>
    private IReplyMarkup SwitchTermMarkup(PremiumPeriodOption period, int noteId, bool russian)
    {
        var options = premiumOptions.Value;
        var monthly = options.FindSubscription(1);
        string? text = null;
        var target = 0;

        if (period.Months == 1 && options.FindSubscription(24) is { } twoYears && monthly != null)
        {
            var saving = (int)Math.Floor((1 - twoYears.Amount / 24m / monthly.Amount) * 100);
            if (saving > 0)
            {
                text = russian ? $"Перейти на двухгодичную (-{saving}% в месяц)" : $"Switch to 2-year (-{saving}% per month)";
                target = 24;
            }
        }
        else if (period.Months == 12 && monthly != null)
        {
            var price = FormatPrice(monthly.Amount, russian);
            text = russian ? $"Перейти на ежемесячную за {price} в месяц" : $"Switch to monthly for {price} per month";
            target = 1;
        }

        if (text == null)
        {
            return new TReplyKeyboardHide();
        }

        return new TReplyInlineMarkup
        {
            Rows = new TVector<IKeyboardButtonRow>(new TKeyboardButtonRow
            {
                Buttons = new TVector<IKeyboardButton>(new TKeyboardButtonCallback
                {
                    Text = text,
                    Data = $"{CallbackPrefix}{target}M/{noteId}"
                })
            })
        };
    }

    /// <summary>
    /// The invoice picture as a proxied <c>webDocument</c>, the way real Telegram sends it, once the
    /// file-server holds a copy; until then the client fetches the URL itself.
    /// </summary>
    private async Task<IWebDocument?> GetInvoicePhotoAsync(IInputWebDocument? input)
    {
        if (input is not TInputWebDocument photo)
        {
            return null;
        }

        try
        {
            if (await webFileRegistrar.EnsureRegisteredAsync(BotUserId, photo.Url, photo.MimeType, photo.Size, photo.Attributes))
            {
                return new TWebDocument
                {
                    Url = photo.Url,
                    AccessHash = webDocumentProxy.Sign(photo.Url),
                    Size = photo.Size,
                    MimeType = photo.MimeType,
                    Attributes = photo.Attributes
                };
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PremiumBot could not register its invoice picture {Url}", photo.Url);
        }

        return BotInvoiceHelper.ToWebDocument(photo);
    }

    private TInputMediaInvoice CreateInputInvoice(PremiumPeriodOption period, bool russian)
    {
        var options = premiumOptions.Value;
        var term = TermText(period.Months, russian);

        return new TInputMediaInvoice
        {
            // Real Telegram's wording: the plain name for a month, its own name for the longer terms.
            Title = period.Months switch
            {
                1 => "Telegram Premium",
                12 => russian ? "Годовая подписка Premium" : "Premium Annual Subscription",
                _ => russian ? $"Premium на {term}" : $"Premium for {term}"
            },
            Description = russian
                ? "Ваш доступ к десяткам эксклюзивных функций Telegram Premium."
                : "Your access to dozens of exclusive Telegram Premium features.",
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
                Currency = options.Currency,
                Prices = new TVector<ILabeledPrice>(new TLabeledPrice
                {
                    Label = russian ? $"Платёж за {term}" : $"Payment for {term}",
                    Amount = period.Amount
                })
            },
            Payload = Encoding.UTF8.GetBytes($"{PayloadPrefix}{period.Months}"),
            Provider = "stripe",
            ProviderData = new TDataJSON { Data = "{}" },
            // A forwarded copy then links back here instead of offering to pay.
            StartParam = period.Months.ToString(CultureInfo.InvariantCulture)
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

    private async Task SendNotUnderstoodAsync(long userId, bool russian)
    {
        var text = NotUnderstood(russian);
        await messageAppService.SendMessageAsync([new SendMessageInput(
            BotRequest(),
            BotUserId,
            new Peer(PeerType.User, userId),
            text,
            Random.Shared.NextInt64(),
            entities: new TVector<IMessageEntity>(new TMessageEntityBotCommand
            {
                Offset = text.LastIndexOf("/start", StringComparison.Ordinal),
                Length = "/start".Length
            }),
            replyMarkup: new TReplyKeyboardHide())]);
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

    // Real Telegram's wording, without a final full stop.
    private static string NotUnderstood(bool russian) => russian
        ? "Извините, я не понял Ваш запрос. Отправьте команду /start"
        : "Sorry, I didn't understand your request. Send the /start command";

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

    /// <summary>"299,00 RUB" / "1 990,00 RUB" with no-break spaces, as real Telegram's bot writes prices.</summary>
    private string FormatPrice(long amount, bool russian)
    {
        var currency = premiumOptions.Value.Currency.ToUpperInvariant();
        var format = (NumberFormatInfo)NumberFormatInfo.InvariantInfo.Clone();
        format.NumberGroupSeparator = russian ? "\u00a0" : ",";
        format.NumberDecimalSeparator = russian ? "," : ".";
        return $"{(amount / 100m).ToString("#,0.00", format)}\u00a0{currency}";
    }

    private static string FormatDate(long unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
}
