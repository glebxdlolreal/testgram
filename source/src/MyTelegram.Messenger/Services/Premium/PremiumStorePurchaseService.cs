using MongoDB.Driver;
using MyTelegram.Messenger.Handlers.LatestLayer.Payments;
using MyTelegram.Messenger.Options;

namespace MyTelegram.Messenger.Services.Premium;

/// <summary>
/// Premium bought through the App Store or Google Play (<c>payments.assignAppStoreTransaction</c> /
/// <c>payments.assignPlayMarketTransaction</c>).
/// </summary>
/// <remarks>
/// The store flow is "not available to third-party apps" and this server cannot check a receipt with
/// Apple or Google, so the purchase rests on the client's word alone. It is therefore refused unless
/// <c>App__Payments__AllowUnverifiedTopup</c> is set, and even then each receipt redeems once.
/// See https://corefork.telegram.org/api/premium
/// </remarks>
public interface IPremiumStorePurchaseService
{
    Task SettleAsync(IRequestInput input, string platform, ReadOnlyMemory<byte> receipt, IInputStorePaymentPurpose? purpose,
        string? storeProduct);
}

/// <inheritdoc />
public sealed class PremiumStorePurchaseService(
    IMongoDatabase mongoDatabase,
    IOptions<MyTelegramMessengerServerOptions> options,
    IOptions<PremiumOptions> premiumOptions,
    IPeerHelper peerHelper,
    IQueryProcessor queryProcessor,
    IPremiumPurchaseGate premiumPurchaseGate,
    IPremiumSubscriptionService premiumSubscriptionService,
    IPremiumGiftService premiumGiftService,
    IPremiumWelcomeNotifier premiumWelcomeNotifier) : IPremiumStorePurchaseService, ITransientDependency
{
    public async Task SettleAsync(IRequestInput input, string platform, ReadOnlyMemory<byte> receipt,
        IInputStorePaymentPurpose? purpose, string? storeProduct)
    {
        var premium = premiumOptions.Value;

        if (purpose is TInputStorePaymentGiftPremium gift)
        {
            var recipientUserId = gift.UserId is TInputUserSelf
                ? input.UserId
                : peerHelper.GetPeer(gift.UserId, input.UserId).PeerId;
            if (recipientUserId != input.UserId)
            {
                await PeerKindHelper.EnsureNotBotOrSystemAsync(queryProcessor, recipientUserId);
            }

            await premiumPurchaseGate.EnsureAllowedAsync(input.UserId, recipientUserId);

            // The purpose names the price, not the term; the term is the one sold at that price.
            var months = premium.GetPeriods()
                .FirstOrDefault(p => p.Gift && p.Amount == gift.Amount &&
                                     string.Equals(premium.Currency, gift.Currency, StringComparison.OrdinalIgnoreCase))
                ?.Months ?? 0;
            if (months == 0)
            {
                RpcErrors.RpcErrors400.MonthInvalid.ThrowRpcError();
            }

            await StoreTransactionHelper.AuthorizeUnverifiedTopupAsync(
                mongoDatabase, options.Value.Payments, platform, receipt, input.UserId, 0);
            await premiumGiftService.DeliverAsync(input, recipientUserId, months, gift.Currency, gift.Amount, null, platform);
            return;
        }

        await premiumPurchaseGate.EnsureAllowedAsync(input.UserId);
        await StoreTransactionHelper.AuthorizeUnverifiedTopupAsync(
            mongoDatabase, options.Value.Payments, platform, receipt, input.UserId, 0);
        var granted = await premiumSubscriptionService.GrantAsync(input.UserId,
            PremiumDuration.DaysFromMonths(MonthsFromStoreProduct(storeProduct)), platform, input.UserId);
        if (granted.BecamePremium)
        {
            await premiumWelcomeNotifier.NotifyAsync(input.UserId);
        }
    }

    /// <summary>
    /// The term of a store product id such as <c>org.telegram.telegramPremium.yearly</c> (the ids
    /// help.getPremiumPromo advertises); a monthly subscription when the id names no other term.
    /// </summary>
    public static int MonthsFromStoreProduct(string? storeProduct)
    {
        var product = storeProduct?.ToLowerInvariant() ?? string.Empty;
        if (product.Contains("biannual") || product.Contains("24"))
        {
            return 24;
        }

        if (product.Contains("yearly") || product.Contains("annual") || product.Contains("12"))
        {
            return 12;
        }

        if (product.Contains("sixmonths") || product.Contains("semiannual"))
        {
            return 6;
        }

        if (product.Contains("threemonths") || product.Contains("quarter"))
        {
            return 3;
        }

        return 1;
    }
}
