using MyTelegram.Messenger.Services.Payments;
namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class GetStarsTopupOptionsHandler
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetStarsTopupOptions, TVector<MyTelegram.Schema.IStarsTopupOption>>
{
    protected override Task<TVector<MyTelegram.Schema.IStarsTopupOption>> HandleCoreAsync(
        IRequestInput input, MyTelegram.Schema.Payments.RequestGetStarsTopupOptions obj)
    {
        var options = StarsCatalog.TopupOffers.Select(o => (IStarsTopupOption)new TStarsTopupOption
        {
            Stars = o.Stars,
            StoreProduct = o.StoreProduct,
            Currency = StarsCatalog.Currency,
            Amount = o.Amount,
            Extended = o.Extended,
        }).ToList();

        return Task.FromResult(new TVector<IStarsTopupOption>(options));
    }
}
