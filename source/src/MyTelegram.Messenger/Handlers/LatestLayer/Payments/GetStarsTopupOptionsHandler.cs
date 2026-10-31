using MyTelegram.Messenger.Services.Payments;
namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class GetStarsTopupOptionsHandler(IPurchasePricing purchasePricing)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetStarsTopupOptions, TVector<MyTelegram.Schema.IStarsTopupOption>>
{
    protected override async Task<TVector<MyTelegram.Schema.IStarsTopupOption>> HandleCoreAsync(
        IRequestInput input, MyTelegram.Schema.Payments.RequestGetStarsTopupOptions obj)
    {
        var prices = await purchasePricing.ForUserAsync(input.UserId);
        var options = StarsCatalog.TopupOffers.Select(o => (IStarsTopupOption)new TStarsTopupOption
        {
            Stars = o.Stars,
            StoreProduct = o.StoreProduct,
            Currency = prices.Currency,
            Amount = prices.StarsPrice(o.Amount),
            Extended = o.Extended,
        }).ToList();

        return new TVector<IStarsTopupOption>(options);
    }
}
