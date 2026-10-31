namespace MyTelegram.Messenger.Services.Payments;

/// <summary>One Stars giveaway on sale: the pool, the boosts it brings and how it may be split.</summary>
public sealed record StarsGiveawayOffer(long Stars, int YearlyBoosts, long Amount, (int Users, long PerUserStars)[] Winners);

/// <summary>
/// The Stars this server sells for money, shared by <c>payments.getStarsGiveawayOptions</c> and
/// @PremiumBot so both offer the same prices. Amounts are in <see cref="Currency"/>.
/// </summary>
public static class StarsCatalog
{
    public const string Currency = "USD";

    public static readonly StarsGiveawayOffer[] GiveawayOffers =
    [
        new(500, 5, 999, [(5, 100), (10, 50), (25, 20)]),
        new(1000, 10, 1999, [(10, 100), (20, 50), (50, 20)]),
        new(2500, 25, 4999, [(25, 100), (50, 50), (125, 20)]),
        new(5000, 50, 9999, [(50, 100), (100, 50), (250, 20)])
    ];

    public static StarsGiveawayOffer? FindGiveaway(long stars) => GiveawayOffers.FirstOrDefault(o => o.Stars == stars);
}
