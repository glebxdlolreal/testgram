using Microsoft.Extensions.Options;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;
using MyTelegram.Services.Services;
using System.Text;
using MyTelegram.Messenger.Services.Premium;
using System.Text.Json;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Informs server about a purchase made through the App Store: for official applications only.
/// Possible errors
/// Code Type Description
/// 400 INPUT_PURPOSE_INVALID The specified payment purpose is invalid.
/// 400 RECEIPT_EMPTY The specified receipt is empty.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.assignAppStoreTransaction"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✔]
/// </remarks>
internal sealed class AssignAppStoreTransactionHandler(
    IMongoDatabase mongoDatabase,
    IOptions<MyTelegramMessengerServerOptions> options,
    IObjectMessageSender objectMessageSender,
    IPremiumStorePurchaseService premiumStorePurchaseService)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestAssignAppStoreTransaction, MyTelegram.Schema.IUpdates>
{
    protected override async Task<IUpdates> HandleCoreAsync(
        IRequestInput input, MyTelegram.Schema.Payments.RequestAssignAppStoreTransaction obj)
    {
        if (obj.Receipt.Length == 0)
        {
            RpcErrors.RpcErrors400.ReceiptEmpty.ThrowRpcError();
        }

        // Unlike assignPlayMarketTransaction, what is being bought is stated in a typed `purpose`
        // rather than hidden inside the receipt, so it is read from there.
        switch (obj.Purpose)
        {
            case TInputStorePaymentPremiumSubscription:
            case TInputStorePaymentGiftPremium:
                // An App Store receipt is an opaque blob that names no product this server can read.
                await premiumStorePurchaseService.SettleAsync(input, "appstore", obj.Receipt, obj.Purpose, storeProduct: null);
                return StoreTransactionHelper.EmptyUpdates();

            case TInputStorePaymentStarsTopup topup:
                await SettleStarsTopupAsync(input.UserId, obj.Receipt, topup.Stars);
                return StoreTransactionHelper.EmptyUpdates();

            default:
                RpcErrors.RpcErrors400.InputPurposeInvalid.ThrowRpcError();
                return null!;
        }
    }

    /// <summary>
    /// Credits a Stars top-up, matching the receipt against the pending Stripe intent when there is
    /// one so the amount comes from the server side record rather than from the client.
    /// </summary>
    private async Task SettleStarsTopupAsync(long userId, ReadOnlyMemory<byte> receipt, long requestedStars)
    {
        var (paymentIntentId, formId) = ParseReceipt(receipt);

        var col = mongoDatabase.GetCollection<StripePaymentIntentDocument>("stripe-payment-intents");
        StripePaymentIntentDocument? intent = null;

        if (!string.IsNullOrEmpty(paymentIntentId))
        {
            intent = await col.Find(x => x.PaymentIntentId == paymentIntentId && x.UserId == userId).FirstOrDefaultAsync();
        }
        else if (formId != 0)
        {
            intent = await col.Find(x => x.FormId == formId && x.UserId == userId).FirstOrDefaultAsync();
        }

        if (intent == null)
        {
            // Nothing on file to settle against, and this server cannot ask Apple whether the receipt
            // is real, so the amount would be whatever the caller put in `purpose`. That is only ever
            // acceptable on a test stand, and only once per receipt and under a per account ceiling.
            await StoreTransactionHelper.CreditUnverifiedTopupAsync(
                mongoDatabase, objectMessageSender, options.Value.Payments, "appstore", receipt, userId,
                requestedStars, $"App Store top-up: {requestedStars} stars");
            return;
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
            mongoDatabase, objectMessageSender, userId, intent.Stars, $"App Store top-up: {intent.Stars} stars");

        await col.DeleteOneAsync(x => x.Id == intent.Id);
    }

    private static (string? PaymentIntentId, long FormId) ParseReceipt(ReadOnlyMemory<byte> receipt)
    {
        try
        {
            var root = JsonDocument.Parse(Encoding.UTF8.GetString(receipt.Span)).RootElement;
            var paymentIntentId = root.TryGetProperty("paymentIntentId", out var intentProperty)
                ? intentProperty.GetString()
                : null;
            var formId = root.TryGetProperty("formId", out var formProperty) ? formProperty.GetInt64() : 0;
            return (paymentIntentId, formId);
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException)
        {
            // A real App Store receipt is opaque, not JSON: there is simply no intent to match.
            return (null, 0);
        }
    }
}