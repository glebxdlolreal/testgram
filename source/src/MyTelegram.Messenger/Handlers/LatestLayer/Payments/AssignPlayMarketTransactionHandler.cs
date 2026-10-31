using MyTelegram.Messenger.Services.Payments;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;
using MyTelegram.Services.Services;
using System.Text.Json;
using MyTelegram.Messenger.Services.Premium;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

/// <summary>
/// Informs server about a purchase made through the Play Store: for official applications only.
/// Possible errors
/// Code Type Description
/// 400 INPUT_PURPOSE_INVALID The specified payment purpose is invalid.
/// 400 RECEIPT_EMPTY The specified receipt is empty.
/// 400 PAYMENT_PROVIDER_INVALID The receipt could not be settled against a payment.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.assignPlayMarketTransaction"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✔]
///
/// <para>
/// Play receipts are not checked against Google here. What the receipt carries instead is a pointer
/// at a Stripe PaymentIntent this server created earlier — <c>{"paymentIntentId":"pi_xxx"}</c> or
/// <c>{"formId":123}</c> — and it is that intent, held server side, that says how many Stars are owed
/// and confirms the charge went through. A receipt naming no intent buys nothing unless the stand is
/// explicitly configured to accept unverified top-ups.
/// </para>
/// </remarks>
internal sealed class AssignPlayMarketTransactionHandler(
    IMongoDatabase mongoDatabase,
    IOptions<MyTelegramMessengerServerOptions> options,
    IObjectMessageSender objectMessageSender,
    IPremiumStorePurchaseService premiumStorePurchaseService,
    IStarsPurchaseGate starsPurchaseGate)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestAssignPlayMarketTransaction, MyTelegram.Schema.IUpdates>
{
    protected override async Task<IUpdates> HandleCoreAsync(
        IRequestInput input, MyTelegram.Schema.Payments.RequestAssignPlayMarketTransaction obj)
    {
        var receiptJson = obj.Receipt?.Data;
        if (string.IsNullOrEmpty(receiptJson))
        {
            RpcErrors.RpcErrors400.ReceiptEmpty.ThrowRpcError();
        }

        var (paymentIntentId, formId, legacyPurpose) = ParseReceipt(receiptJson!);

        // Premium is stated either in the typed purpose an official client sends or, for the clients
        // built against this server before the purpose was read, inside the receipt itself.
        if (legacyPurpose == "premium" ||
            obj.Purpose is TInputStorePaymentPremiumSubscription or TInputStorePaymentGiftPremium)
        {
            await premiumStorePurchaseService.SettleAsync(input, "playmarket", System.Text.Encoding.UTF8.GetBytes(receiptJson!),
                obj.Purpose, ReadProductId(receiptJson!));
            return StoreTransactionHelper.EmptyUpdates();
        }

        // Everything past here credits Stars.
        await starsPurchaseGate.EnsureAllowedAsync(input.UserId);

        var collection = mongoDatabase.GetCollection<StripePaymentIntentDocument>("stripe-payment-intents");
        var userId = input.UserId;

        StripePaymentIntentDocument? intent = null;
        if (!string.IsNullOrEmpty(paymentIntentId))
        {
            // Matched on the account as well as on the id: an intent id learned from someone else is
            // still their purchase, and must not settle into the caller's balance.
            intent = await collection.Find(x => x.PaymentIntentId == paymentIntentId && x.UserId == userId)
                .FirstOrDefaultAsync();
        }
        else if (formId != 0)
        {
            intent = await collection.Find(x => x.FormId == formId && x.UserId == userId)
                .FirstOrDefaultAsync();
        }

        if (intent == null)
        {
            var requestedStars = obj.Purpose is TInputStorePaymentStarsTopup topup ? topup.Stars : 0;

            await StoreTransactionHelper.CreditUnverifiedTopupAsync(
                mongoDatabase, objectMessageSender, options.Value.Payments, "playmarket", receiptJson!, userId,
                requestedStars, $"Play Market top-up: {requestedStars} stars");

            return StoreTransactionHelper.EmptyUpdates();
        }

        var stripe = options.Value.Stripe;
        if (!string.IsNullOrEmpty(stripe.SecretKey))
        {
            var (status, _, _) = await StripeHelper.GetPaymentIntentAsync(stripe.SecretKey, intent.PaymentIntentId);
            if (status != "succeeded")
            {
                RpcErrors.RpcErrors400.PaymentProviderInvalid.ThrowRpcError();
            }
        }

        await StoreTransactionHelper.CreditStarsAsync(
            mongoDatabase, objectMessageSender, userId, intent.Stars, $"Stripe top-up: {intent.Stars} stars");

        // Settling the intent is what stops the same receipt paying out twice.
        await collection.DeleteOneAsync(x => x.Id == intent.Id);

        return StoreTransactionHelper.EmptyUpdates();
    }

    /// <summary>The <c>productId</c> of a Google Play purchase receipt, if it is one.</summary>
    private static string? ReadProductId(string receiptJson)
    {
        try
        {
            var root = JsonDocument.Parse(receiptJson).RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("productId", out var product)
                ? product.GetString()
                : null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static (string? PaymentIntentId, long FormId, string? Purpose) ParseReceipt(string receiptJson)
    {
        try
        {
            var root = JsonDocument.Parse(receiptJson).RootElement;
            var paymentIntentId = root.TryGetProperty("paymentIntentId", out var intentProperty)
                ? intentProperty.GetString()
                : null;
            var formId = root.TryGetProperty("formId", out var formProperty) ? formProperty.GetInt64() : 0;
            var purpose = root.TryGetProperty("purpose", out var purposeProperty)
                ? purposeProperty.GetString()
                : null;
            return (paymentIntentId, formId, purpose);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            // A real Play receipt is Google's own JSON, which names no intent: there is nothing to
            // settle against, and the caller falls through to the unverified path.
            return (null, 0, null);
        }
    }
}
