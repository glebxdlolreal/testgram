using MyTelegram.Messenger.Services.Payments;
namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Fetch a list of <a href="https://corefork.telegram.org/api/giveaways#star-giveaways">star giveaway options »</a>.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.getStarsGiveawayOptions"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetStarsGiveawayOptionsHandler : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetStarsGiveawayOptions, TVector<MyTelegram.Schema.IStarsGiveawayOption>>
{
    protected override Task<TVector<MyTelegram.Schema.IStarsGiveawayOption>> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestGetStarsGiveawayOptions obj)
    {
        var options = new TVector<MyTelegram.Schema.IStarsGiveawayOption>(StarsCatalog.GiveawayOffers.Select((offer, i) =>
            (MyTelegram.Schema.IStarsGiveawayOption)new TStarsGiveawayOption
            {
                Default = i == 0,
                Extended = offer.Extended,
                Stars = offer.Stars,
                YearlyBoosts = offer.YearlyBoosts,
                Currency = StarsCatalog.Currency,
                Amount = offer.Amount,
                Winners = new TVector<MyTelegram.Schema.IStarsGiveawayWinnersOption>(offer.Winners.Select(w =>
                    (MyTelegram.Schema.IStarsGiveawayWinnersOption)new TStarsGiveawayWinnersOption
                    {
                        Default = w.Default,
                        Users = w.Users,
                        PerUserStars = w.PerUserStars
                    }))
            }));

        return Task.FromResult(options);
    }
}