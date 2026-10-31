using MyTelegram.Schema.Payments;
using MyTelegram.Schema;
using MyTelegram.Messenger.Services.Payments;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Obtain a list of <a href="https://corefork.telegram.org/api/stars#buying-or-gifting-stars">Telegram Stars gift options »</a> as <a href="https://corefork.telegram.org/constructor/starsGiftOption">starsGiftOption</a> constructors.
/// Possible errors
/// Code Type Description
/// 400 INPUT_USER_DEACTIVATED The specified user was deleted.
/// 400 USER_GIFT_UNAVAILABLE Gifts are not available in the current region (<a href="https://corefork.telegram.org/api/config#stars-gifts-enabled">stars_gifts_enabled</a> is equal to false).
/// 400 USER_ID_INVALID The provided user ID is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.getStarsGiftOptions"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetStarsGiftOptionsHandler(IPurchasePricing purchasePricing) : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetStarsGiftOptions, TVector<MyTelegram.Schema.IStarsGiftOption>>
{
    protected override async Task<TVector<MyTelegram.Schema.IStarsGiftOption>> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestGetStarsGiftOptions obj)
    {
        var prices = await purchasePricing.ForUserAsync(input.UserId);
        var options = StarsCatalog.TopupOffers.Select(o => (IStarsGiftOption)new TStarsGiftOption
        {
            Stars = o.Stars,
            StoreProduct = o.StoreProduct,
            Currency = prices.Currency,
            Amount = prices.StarsPrice(o.Amount),
            Extended = o.Extended,
        });
        return [.. options];
    }
}