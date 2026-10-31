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

    /// <summary>Tells the subscriber how an automatic renewal went.</summary>
    Task NotifyRenewalAsync(PremiumRenewalOutcome outcome);

    /// <summary>The chat picked with one of the bot's <c>keyboardButtonRequestPeer</c> buttons.</summary>
    Task HandleRequestedPeerAsync(IRequestInput input, int buttonId, IReadOnlyList<Peer> peers);

    /// <summary>A contact shared with the bot; <paramref name="contactUserId"/> is 0 for a phone with no account.</summary>
    Task HandleContactAsync(IRequestInput input, long contactUserId);

    bool IsPremiumBotInvoice(BotInvoiceDocument storedInvoice);

    Task<IPaymentForm> BuildPaymentFormAsync(IRequestInput input, BotInvoiceDocument storedInvoice);

    /// <summary>Charges the card (query server) and hands the delivery to the command server.</summary>
    Task<IPaymentResult> PayAsync(IRequestInput input, BotInvoiceDocument storedInvoice, RequestSendPaymentForm request);

    /// <summary>Delivers a paid invoice and sends the receipts; command server only.</summary>
    Task SettleAsync(IRequestInput input, PremiumBotPaymentSettledEvent settled);
}

/// <inheritdoc />
public sealed partial class PremiumBotService(
    IMongoDatabase mongoDatabase,
    IMessageAppService messageAppService,
    IMediaHelper mediaHelper,
    IIdGenerator idGenerator,
    IQueryProcessor queryProcessor,
    IUserConverterService userConverterService,
    IOptions<PremiumOptions> premiumOptions,
    IPremiumPurchaseGate premiumPurchaseGate,
    IStarsPurchaseGate starsPurchaseGate,
    IPremiumSubscriptionService premiumSubscriptionService,
    IPremiumCardCheckout premiumCardCheckout,
    IPremiumAutoRenewService premiumAutoRenewService,
    IPremiumGiftService premiumGiftService,
    IPremiumWelcomeNotifier premiumWelcomeNotifier,
    IEventBus eventBus,
    ICommandBus commandBus,
    IDataEncryptionHelper dataEncryptionHelper,
    IOptionsMonitor<MyTelegramMessengerServerOptions> serverOptions,
    IBotInvoicePaymentService botInvoicePaymentService,
    IWebFileRegistrar webFileRegistrar,
    IWebDocumentProxy webDocumentProxy,
    IPurchasePricing purchasePricing,
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
            var russian = await IsRussianAsync(input);
            var command = text.Trim();
            if (!command.StartsWith('/'))
            {
                // A reply-keyboard button, or text typed in answer to one of the bot's questions.
                await HandleReplyAsync(input.UserId, command, russian);
                return;
            }

            // "/start 12", "/start@PremiumBot 12"
            var space = command.IndexOfAny([' ', '\n', '\t']);
            var name = space < 0 ? command[1..] : command[1..space];
            var parameter = space < 0 ? string.Empty : command[(space + 1)..].Trim();
            var at = name.IndexOf('@');
            if (at >= 0)
            {
                if (!string.Equals(name[(at + 1)..], MyTelegramConsts.PremiumBotUserName, StringComparison.OrdinalIgnoreCase))
                {
                    await SendNotUnderstoodAsync(input.UserId, russian);
                    return;
                }

                name = name[..at];
            }

            await HandleCommandAsync(input.UserId, name.ToLowerInvariant(), parameter, russian);
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
            if (data.StartsWith(GiveawayCallbackPrefix, StringComparison.Ordinal) ||
                data.StartsWith(StarsGiveawayCallbackPrefix, StringComparison.Ordinal))
            {
                await HandleGiveawayCallbackAsync(input.UserId, data, russian);
                return;
            }

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
            // Off sale for this account: the same answer, without the invoice or anything to press.
            await SendAsync(userId, InvoiceNote(russian), [InvoiceNoteBold(russian)], new TReplyKeyboardHide());
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
        var purchase = ReadPurchase(storedInvoice);
        await EnsureCanBuyAsync(input.UserId, storedInvoice, purchase);

        var invoice = BotInvoiceHelper.ReadInvoice(storedInvoice);
        var form = await premiumCardCheckout.CreateAsync(input.UserId, storedInvoice.TotalAmount, storedInvoice.Currency,
            storedInvoice.Title, intent =>
            {
                intent.PremiumKind = PremiumCardCheckout.KindBotInvoice;
                intent.PremiumMonths = purchase.Months;
                intent.PremiumQuantity = purchase.Quantity;
                intent.PremiumRecipientUserIds = [.. purchase.Recipients];
                intent.Stars = purchase.Stars;
                intent.BotInvoiceId = storedInvoice.Id;
                // Subscriptions renew: the card goes on file for the next term.
                intent.SaveCardForRenewal = purchase.Kind == PurchaseKind.Subscription;
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
        if (intent.PremiumKind != PremiumCardCheckout.KindBotInvoice ||
            (intent.BotInvoiceId != null && intent.BotInvoiceId != storedInvoice.Id &&
             intent.BotInvoiceId != BotInvoiceHelper.MakeSlugId(storedInvoice.Slug)))
        {
            RpcErrors.RpcErrors400.PaymentProviderInvalid.ThrowRpcError();
        }

        var purchase = ReadPurchase(storedInvoice);
        await EnsureCanBuyAsync(input.UserId, storedInvoice, purchase);
        var charge = await premiumCardCheckout.ChargeAsync(intent, request.Credentials);

        // Paid. What was bought is delivered by the command server, the one process that may send messages.
        await eventBus.PublishAsync(new PremiumBotPaymentSettledEvent(input.UserId, input.PermAuthKeyId, input.Layer, storedInvoice.Id,
            intent.PaymentIntentId, charge.StripeCustomerId ?? string.Empty, charge.StripePaymentMethodId ?? string.Empty,
            charge.CredentialsTitle ?? string.Empty));

        return EmptyPaymentResult();
    }

    public async Task SettleAsync(IRequestInput input, PremiumBotPaymentSettledEvent settled)
    {
        var storedInvoice = await mongoDatabase.GetCollection<BotInvoiceDocument>(BotInvoiceHelper.CollectionName)
            .Find(x => x.Id == settled.StoredInvoiceId)
            .FirstOrDefaultAsync();
        if (storedInvoice == null)
        {
            logger.LogError("PremiumBot: a paid invoice {InvoiceId} of {UserId} is gone; charge {Charge} was not delivered",
                settled.StoredInvoiceId, settled.UserId, settled.PaymentIntentId);
            return;
        }

        var purchase = ReadPurchase(storedInvoice);
        await botInvoicePaymentService.CompleteExternalChargeAsync(Fresh(input), storedInvoice, settled.PaymentIntentId,
            settled.CredentialsTitle);
        var russian = await IsRussianAsync(input);
        var price = FormatPrice(storedInvoice.TotalAmount, storedInvoice.Currency);
        switch (purchase.Kind)
        {
            case PurchaseKind.Subscription:
                var result = await premiumSubscriptionService.GrantAsync(input.UserId, PremiumDuration.DaysFromMonths(purchase.Months),
                    "premium-bot", input.UserId);
                if (settled.StripeCustomerId.Length > 0 && settled.StripePaymentMethodId.Length > 0)
                {
                    await premiumAutoRenewService.SaveCardAsync(input.UserId, purchase.Months, storedInvoice.TotalAmount,
                        storedInvoice.Currency, settled.StripeCustomerId, settled.StripePaymentMethodId);
                }

                await SendSubscriptionReceiptAsync(input.UserId, price, result.ExpiresAt, russian);
                if (result.BecamePremium)
                {
                    await premiumWelcomeNotifier.SendAsync(input.UserId);
                }

                break;

            case PurchaseKind.PremiumGift:
                foreach (var recipient in purchase.Recipients)
                {
                    await premiumGiftService.DeliverAsync(Fresh(input), recipient, purchase.Months, storedInvoice.Currency,
                        storedInvoice.TotalAmount / purchase.Recipients.Length, null, "premium-bot-gift");
                }

                await SendPremiumGiftReceiptAsync(input.UserId, price, purchase, russian);
                break;

            case PurchaseKind.Stars:
                await StarsPurchaseHelper.CreditAsync(mongoDatabase, messageAppService, Fresh(input), purchase.Recipients.FirstOrDefault(),
                    purchase.Stars, storedInvoice.Currency, storedInvoice.TotalAmount, settled.PaymentIntentId);
                await SendStarsReceiptAsync(input.UserId, price, purchase, russian);
                break;

            case PurchaseKind.PremiumGiveaway:
            case PurchaseKind.StarsGiveaway:
                await PrepayGiveawayAsync(input.UserId, purchase, price, russian);
                break;
        }

    }

    /// <summary>
    /// The same request with its own id. A message send saga is keyed by the request id, so two messages
    /// sent on one request (the receipt, then a gift) merge and the second is silently lost.
    /// </summary>
    private static IRequestInput Fresh(IRequestInput input) =>
        input is RequestInput request ? request with { RequestId = Guid.NewGuid() } : input;

    /// <summary>
    /// Links are made out to the user the bot offered them to; Premium is refused to an account it is off
    /// sale for, and to recipients who may not receive it.
    /// </summary>
    private async Task EnsureCanBuyAsync(long userId, BotInvoiceDocument storedInvoice, BotPurchase purchase)
    {
        if (purchase.Kind != PurchaseKind.Subscription && storedInvoice.ToPeerId != userId)
        {
            RpcErrors.RpcErrors400.InvoiceInvalid.ThrowRpcError();
        }

        if (purchase.Kind is PurchaseKind.Subscription or PurchaseKind.PremiumGift or PurchaseKind.PremiumGiveaway)
        {
            await premiumPurchaseGate.EnsureAllowedAsync(userId);
        }

        if (purchase.Kind is PurchaseKind.Stars or PurchaseKind.StarsGiveaway)
        {
            await starsPurchaseGate.EnsureAllowedAsync([userId, .. purchase.Recipients]);
        }

        if (purchase.Kind == PurchaseKind.PremiumGift)
        {
            foreach (var recipient in purchase.Recipients)
            {
                await premiumPurchaseGate.EnsureAllowedAsync(recipient);
            }
        }
    }

    private static TPaymentResult EmptyPaymentResult() => new()
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

    private async Task SendInvoiceAsync(long userId, PremiumPeriodOption period, bool russian, bool upgrade = false,
        bool offerMonthly = false)
    {
        var prices = await purchasePricing.ForUserAsync(userId);
        var inputInvoice = CreateInputInvoice(period, russian, prices);
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
                    Text = (russian ? "Заплатить " : "Pay ") + FormatPrice(prices, period.Amount)
                })
            })
        });

        string note;
        TVector<IMessageEntity> noteEntities;
        IReplyMarkup noteMarkup;
        if (upgrade)
        {
            // /upgrade: the longer term is bought now and starts when the current one ends.
            note = UpgradeNote(russian);
            noteEntities = new TVector<IMessageEntity>();
            noteMarkup = new TReplyKeyboardHide();
        }
        else
        {
            note = InvoiceNote(russian);
            noteEntities = new TVector<IMessageEntity>(InvoiceNoteBold(russian));
            noteMarkup = SwitchTermMarkup(period, noteId, russian, offerMonthly, prices);
        }

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
                entities: noteEntities,
                replyMarkup: noteMarkup,
                messageId: noteId)
        ]);

        var document = BotInvoiceHelper.Create(inputInvoice, BotUserId, ownerPeerId: BotUserId, toPeerId: userId, msgId: messageId);
        document.Photo = photo?.ToBytes();
        await BotInvoiceHelper.SaveAsync(mongoDatabase, document);
    }

    /// <summary>
    /// Under a monthly invoice, a switch to two years with its saving per month; under a yearly one, a
    /// switch to monthly at its price; under the two-year one nothing (real Telegram hides the keyboard),
    /// except when it came from <c>/upgrade</c> without Premium, where it offers monthly too.
    /// </summary>
    private IReplyMarkup SwitchTermMarkup(PremiumPeriodOption period, int noteId, bool russian, bool offerMonthly,
        PurchasePrices prices)
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
        else if ((period.Months == 12 || (offerMonthly && period.Months > 1)) && monthly != null)
        {
            var price = FormatPrice(prices, monthly.Amount);
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

    private TInputMediaInvoice CreateInputInvoice(PremiumPeriodOption period, bool russian, PurchasePrices prices)
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
                // Recurring, as on real Telegram, only where renewals can really be charged.
                Recurring = !string.IsNullOrEmpty(serverOptions.CurrentValue.Stripe?.SecretKey),
                TermsUrl = string.IsNullOrEmpty(serverOptions.CurrentValue.Stripe?.SecretKey) ? null : options.TermsUrl,
                Currency = prices.Currency,
                Prices = new TVector<ILabeledPrice>(new TLabeledPrice
                {
                    Label = russian ? $"Платёж за {term}" : $"Payment for {term}",
                    Amount = prices.Price(period.Amount)
                })
            },
            Payload = Encoding.UTF8.GetBytes($"{PayloadPrefix}{period.Months}"),
            Provider = "stripe",
            ProviderData = new TDataJSON { Data = "{}" },
            // A forwarded copy then links back here instead of offering to pay.
            StartParam = period.Months.ToString(CultureInfo.InvariantCulture)
        };
    }

    private enum PurchaseKind
    {
        Subscription,
        PremiumGift,
        PremiumGiveaway,
        Stars,
        StarsGiveaway
    }

    /// <summary>What a bot invoice sells, from its payload.</summary>
    private sealed record BotPurchase(PurchaseKind Kind, int Months = 0, long ChannelId = 0, int Quantity = 1, long Stars = 0,
        long[]? RecipientIds = null)
    {
        public long[] Recipients => RecipientIds ?? [];
    }

    /// <summary>
    /// Payloads: <c>premium:&lt;months&gt;</c>, <c>premium-gift:&lt;months&gt;:&lt;user,…&gt;</c>,
    /// <c>premium-giveaway:&lt;channel&gt;:&lt;months&gt;:&lt;quantity&gt;</c>, <c>stars:&lt;stars&gt;:&lt;recipient or 0&gt;</c>,
    /// <c>stars-giveaway:&lt;channel&gt;:&lt;stars&gt;:&lt;winners&gt;</c>.
    /// </summary>
    private static BotPurchase ReadPurchase(BotInvoiceDocument storedInvoice)
    {
        var payload = Encoding.UTF8.GetString(storedInvoice.Payload);
        var colon = payload.IndexOf(':');
        var kind = colon < 0 ? string.Empty : payload[..colon];
        var parts = colon < 0 ? [] : payload[(colon + 1)..].Split(':');
        BotPurchase? purchase = null;

        switch (kind)
        {
            case "premium" when parts.Length == 1 && TryPositive(parts[0], out var months):
                purchase = new BotPurchase(PurchaseKind.Subscription, (int)months);
                break;
            case "premium-gift" when parts.Length == 2 && TryPositive(parts[0], out var giftMonths):
                var recipients = parts[1].Split(',').Select(p => TryPositive(p, out var id) ? id : 0).ToArray();
                if (recipients.Length > 0 && recipients.All(id => id > 0))
                {
                    purchase = new BotPurchase(PurchaseKind.PremiumGift, (int)giftMonths, Quantity: recipients.Length, RecipientIds: recipients);
                }

                break;
            case "premium-giveaway" when parts.Length == 3 && TryPositive(parts[0], out var channelId) &&
                                         TryPositive(parts[1], out var giveawayMonths) && TryPositive(parts[2], out var quantity):
                purchase = new BotPurchase(PurchaseKind.PremiumGiveaway, (int)giveawayMonths, channelId, (int)quantity);
                break;
            case "stars" when parts.Length == 2 && TryPositive(parts[0], out var stars) &&
                              long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var starsRecipient):
                purchase = new BotPurchase(PurchaseKind.Stars, Stars: stars, RecipientIds: starsRecipient > 0 ? [starsRecipient] : []);
                break;
            case "stars-giveaway" when parts.Length == 3 && TryPositive(parts[0], out var starsChannelId) &&
                                       TryPositive(parts[1], out var pool) && TryPositive(parts[2], out var winners):
                purchase = new BotPurchase(PurchaseKind.StarsGiveaway, ChannelId: starsChannelId, Quantity: (int)winners, Stars: pool);
                break;
        }

        if (purchase == null)
        {
            RpcErrors.RpcErrors400.InvoiceInvalid.ThrowRpcError();
        }

        return purchase!;

        static bool TryPositive(string value, out long number) =>
            long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0;
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
        return IsRussian(device);
    }

    /// <summary>For a message nobody asked for (a renewal): the language of the account's latest session.</summary>
    private async Task<bool> IsRussianAsync(long userId)
    {
        var devices = await queryProcessor.ProcessAsync(new GetDeviceByUserIdQuery(userId));
        return IsRussian(devices.Where(d => d.IsActive).MaxBy(d => d.DateActive));
    }

    private static bool IsRussian(IDeviceReadModel? device)
    {
        var language = string.IsNullOrEmpty(device?.LangCode) ? device?.SystemLangCode : device.LangCode;

        return language?.StartsWith("ru", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string InvoiceNote(bool russian) => russian
        ? "Оплатите счёт выше, чтобы получить доступ к эксклюзивным функциям, доступным только для подписчиков Telegram Premium."
        : "Pay the invoice above to get access to exclusive features available only to Telegram Premium subscribers.";

    private static TMessageEntityBold InvoiceNoteBold(bool russian) =>
        new() { Offset = 0, Length = russian ? "Оплатите счёт выше".Length : "Pay the invoice above".Length };

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

    /// <summary>A Premium table price in the buyer's currency, written like <see cref="FormatPrice(long, string)"/>.</summary>
    private static string FormatPrice(PurchasePrices prices, long amount) => FormatPrice(prices.Price(amount), prices.Currency);

    /// <summary>
    /// "1 990,00 RUB", "9.599,00 SEK", "£699.00": real Telegram's bot writes each currency its own way
    /// whatever the language of the chat.
    /// </summary>
    private static string FormatPrice(long amount, string currencyCode) => CurrencyFormatter.Format(amount, currencyCode);

    private static string FormatDate(long unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
}
