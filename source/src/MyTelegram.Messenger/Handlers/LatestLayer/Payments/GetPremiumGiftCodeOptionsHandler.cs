using Microsoft.Extensions.Options;
using MyTelegram.Messenger.Options;
using MyTelegram.Messenger.Services.Premium;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Obtain a list of Telegram Premium <a href="https://corefork.telegram.org/api/giveaways">giveaway/gift code »</a> options.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.getPremiumGiftCodeOptions"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
///
/// <para>Prices come from <see cref="PremiumOptions"/>, the same table the payment form charges. The
/// shape follows real Telegram (measured 2026-10-31): each gift term, longest first, for 1 to 10
/// recipients in the money currency, then the single-recipient <c>XTR</c> options unofficial clients
/// must gift with through <c>inputInvoicePremiumGiftStars</c>
/// (https://corefork.telegram.org/api/premium#gifting-telegram-premium).</para>
/// <para>For an account Premium is off sale for (<see cref="IPremiumPurchaseGate"/>) there is nothing
/// it may buy, so the list is empty.</para>
/// </remarks>
internal sealed class GetPremiumGiftCodeOptionsHandler(
    IOptions<PremiumOptions> premiumOptions,
    IPremiumPurchaseGate premiumPurchaseGate)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetPremiumGiftCodeOptions, TVector<MyTelegram.Schema.IPremiumGiftCodeOption>>
{
    private const int MaxRecipients = 10;

    protected override async Task<TVector<MyTelegram.Schema.IPremiumGiftCodeOption>> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestGetPremiumGiftCodeOptions obj)
    {
        var options = new TVector<MyTelegram.Schema.IPremiumGiftCodeOption>();
        if (await premiumPurchaseGate.IsBlockedAsync(input.UserId))
        {
            return options;
        }

        var premium = premiumOptions.Value;
        var gifts = premium.GetPeriods().Where(p => p.Gift).OrderByDescending(p => p.Months).ToList();

        // Gift codes go to 1–10 users; a giveaway (boost_peer set) may also have 25, 50 or 100 winners.
        int[] quantities = obj.BoostPeer == null
            ? [.. Enumerable.Range(1, MaxRecipients)]
            : [.. Enumerable.Range(1, MaxRecipients), .. PremiumOptions.GiveawayExtraQuantities];
        foreach (var period in gifts)
        {
            foreach (var users in quantities)
            {
                var amount = premium.GiftPrice(period.Months, users);
                if (amount <= 0)
                {
                    continue;
                }

                options.Add(new TPremiumGiftCodeOption
                {
                    Users = users,
                    Months = period.Months,
                    Currency = premium.Currency,
                    Amount = amount
                });
            }
        }

        // Stars are for gifting to one user only, and not for giveaways.
        if (obj.BoostPeer == null)
        {
            foreach (var period in gifts.Where(p => p.AmountStars > 0))
            {
                options.Add(new TPremiumGiftCodeOption
                {
                    Users = 1,
                    Months = period.Months,
                    Currency = "XTR",
                    Amount = period.AmountStars
                });
            }
        }

        return options;
    }
}
