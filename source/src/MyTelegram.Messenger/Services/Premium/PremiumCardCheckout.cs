using System.Text.Json;
using MongoDB.Driver;
using MyTelegram.Messenger.Handlers.LatestLayer.Payments;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.Services.Premium;

/// <summary>
/// Card checkout for Telegram Premium through the configured Stripe account: the payment form's
/// PaymentIntent, and settling it when the client submits the form.
/// </summary>
/// <remarks>
/// <para>The quote (who, how many months, how much) is stored with the intent when the form is
/// issued and is what settlement grants; the client's restated invoice is not trusted.</para>
/// <para>Without a Stripe secret key, or when the client sends no card token, the charge cannot be
/// confirmed. That path is refused unless <c>App__Payments__AllowUnverifiedTopup</c> is set, the
/// same switch the other unverifiable purchases sit behind.</para>
/// </remarks>
public interface IPremiumCardCheckout
{
    Task<PremiumCardForm> CreateAsync(long payerUserId, long amount, string currency, string description,
        Action<StripePaymentIntentDocument> describe);

    /// <summary>The caller's still-unsettled intent behind <paramref name="formId"/>, or PAYMENT_PROVIDER_INVALID.</summary>
    Task<StripePaymentIntentDocument> FindAsync(long formId, long payerUserId);

    /// <summary>
    /// Charges the card and claims the intent so it can be settled exactly once. Throws if the charge
    /// is not confirmed or the form was already settled.
    /// </summary>
    Task<PremiumCardCharge> ChargeAsync(StripePaymentIntentDocument intent, IInputPaymentCredentials? credentials);
}

public sealed record PremiumCardForm(long FormId, string NativeProvider, string NativeParamsJson);

/// <summary>
/// A settled charge. The customer and card are set only when the intent asked to keep the card
/// (<see cref="StripePaymentIntentDocument.SaveCardForRenewal"/>) and Stripe really charged it.
/// </summary>
public sealed record PremiumCardCharge(bool Verified, string? StripeCustomerId, string? StripePaymentMethodId);

/// <inheritdoc />
public sealed class PremiumCardCheckout(
    IMongoDatabase mongoDatabase,
    IOptions<MyTelegramMessengerServerOptions> options,
    ILogger<PremiumCardCheckout> logger) : IPremiumCardCheckout, ITransientDependency
{
    public const string CollectionName = "stripe-payment-intents";

    public const string KindGiftCode = "premium-gift-code";
    public const string KindSubscription = "premium-subscription";
    public const string KindGiveaway = "premium-giveaway";
    public const string KindBotInvoice = "premium-bot-invoice";

    public async Task<PremiumCardForm> CreateAsync(long payerUserId, long amount, string currency, string description,
        Action<StripePaymentIntentDocument> describe)
    {
        if (amount <= 0)
        {
            RpcErrors.RpcErrors400.InvoiceInvalid.ThrowRpcError();
        }

        var stripe = options.Value.Stripe;
        var formId = Random.Shared.NextInt64(1, long.MaxValue);
        var document = new StripePaymentIntentDocument
        {
            FormId = formId,
            UserId = payerUserId,
            CreatedAt = DateTime.UtcNow,
            Amount = amount,
            Currency = currency
        };
        describe(document);

        string nativeParamsJson;
        if (!string.IsNullOrEmpty(stripe.SecretKey))
        {
            var (clientSecret, paymentIntentId) = await StripeHelper.CreatePaymentIntentAsync(
                stripe.SecretKey, amount, currency, description);
            document.PaymentIntentId = paymentIntentId;
            document.ClientSecret = clientSecret;
            nativeParamsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                publishable_key = stripe.PublishableKey,
                payment_intent_client_secret = clientSecret,
                need_country = false,
                need_zip = false,
                need_cardholder_name = false,
            });
        }
        else
        {
            document.PaymentIntentId = $"unverified_{formId}";
            nativeParamsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                publishable_key = stripe.PublishableKey,
                need_country = false,
                need_zip = false,
                need_cardholder_name = false,
            });
        }

        await mongoDatabase.GetCollection<StripePaymentIntentDocument>(CollectionName).InsertOneAsync(document);

        return new PremiumCardForm(formId, "stripe", nativeParamsJson);
    }

    public async Task<StripePaymentIntentDocument> FindAsync(long formId, long payerUserId)
    {
        // Matched on the account too: a form id belonging to someone else is their purchase.
        var intent = await mongoDatabase.GetCollection<StripePaymentIntentDocument>(CollectionName)
            .Find(x => x.FormId == formId && x.UserId == payerUserId)
            .FirstOrDefaultAsync();
        if (intent == null)
        {
            RpcErrors.RpcErrors400.PaymentProviderInvalid.ThrowRpcError();
        }

        return intent!;
    }

    public async Task<PremiumCardCharge> ChargeAsync(StripePaymentIntentDocument intent, IInputPaymentCredentials? credentials)
    {
        var stripe = options.Value.Stripe;
        var tokenId = ReadCardToken(credentials);
        var chargeVerified = false;
        string? customerId = null;
        string? paymentMethodId = null;

        if (!string.IsNullOrEmpty(stripe.SecretKey) && !string.IsNullOrEmpty(tokenId) &&
            !intent.PaymentIntentId.StartsWith("unverified_", StringComparison.Ordinal))
        {
            var (existingStatus, _, _) = await StripeHelper.GetPaymentIntentAsync(stripe.SecretKey, intent.PaymentIntentId);
            if (!IsPaid(existingStatus))
            {
                try
                {
                    if (intent.SaveCardForRenewal)
                    {
                        customerId = await StripeHelper.CreateCustomerAsync(stripe.SecretKey, $"Telegram user {intent.UserId}");
                        await StripeHelper.SaveCardForFutureUsageAsync(stripe.SecretKey, intent.PaymentIntentId, customerId);
                    }

                    paymentMethodId = await StripeHelper.ConfirmPaymentIntentAsync(stripe.SecretKey, intent.PaymentIntentId, tokenId!);
                }
                catch (InvalidOperationException ex)
                {
                    logger.LogWarning(ex, "Premium card charge declined: formId={FormId} userId={UserId}", intent.FormId, intent.UserId);
                    RpcErrors.RpcErrors400.PaymentProviderInvalid.ThrowRpcError();
                }

                var (status, _, _) = await StripeHelper.GetPaymentIntentAsync(stripe.SecretKey, intent.PaymentIntentId);
                if (!IsPaid(status))
                {
                    RpcErrors.RpcErrors400.PaymentProviderInvalid.ThrowRpcError();
                }
            }

            chargeVerified = true;
        }

        if (!chargeVerified)
        {
            // Burns the form as a receipt and refuses unless unverified purchases are switched on.
            await StoreTransactionHelper.AuthorizeUnverifiedTopupAsync(
                mongoDatabase, options.Value.Payments, "stripe", intent.Id.ToString(), intent.UserId, 0);
        }

        // Claimed once: a second submission of the same form finds nothing to delete.
        var claimed = await mongoDatabase.GetCollection<StripePaymentIntentDocument>(CollectionName)
            .DeleteOneAsync(x => x.Id == intent.Id);
        if (claimed.DeletedCount == 0)
        {
            RpcErrors.RpcErrors400.FormExpired.ThrowRpcError();
        }

        logger.LogInformation("Premium card payment settled: formId={FormId} userId={UserId} kind={Kind} amount={Amount} {Currency} verified={Verified}",
            intent.FormId, intent.UserId, intent.PremiumKind, intent.Amount, intent.Currency, chargeVerified);

        return customerId != null && paymentMethodId != null
            ? new PremiumCardCharge(chargeVerified, customerId, paymentMethodId)
            : new PremiumCardCharge(chargeVerified, null, null);
    }

    private static bool IsPaid(string status) => status is "succeeded" or "requires_capture";

    private static string? ReadCardToken(IInputPaymentCredentials? credentials)
    {
        if (credentials is not TInputPaymentCredentials { Data.Data: { Length: > 0 } json })
        {
            return null;
        }

        try
        {
            var root = JsonDocument.Parse(json).RootElement;
            return root.TryGetProperty("id", out var id) ? id.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
