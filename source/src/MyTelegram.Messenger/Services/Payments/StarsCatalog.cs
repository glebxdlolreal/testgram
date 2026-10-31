namespace MyTelegram.Messenger.Services.Payments;

/// <summary>A Stars pack on sale for money; <see cref="Extended"/> packs sit behind "show more" in clients.</summary>
public sealed record StarsTopupOffer(long Stars, long Amount, bool Extended)
{
    public string StoreProduct => $"com.testgram.stars{Stars}";
}

/// <summary>One Stars giveaway on sale: the pool, the boosts it brings and how it may be split.</summary>
public sealed record StarsGiveawayOffer(long Stars, int YearlyBoosts, long Amount, bool Extended,
    (int Users, long PerUserStars, bool Default)[] Winners);

/// <summary>
/// The Stars this server sells for money, shared by the Stars option methods, the payment forms and
/// @PremiumBot so all of them quote the same prices: real Telegram's for a Russian account
/// (payments.getStarsTopupOptions / getStarsGiftOptions / getStarsGiveawayOptions, measured 2026-10-31).
/// Amounts are in <see cref="Currency"/>.
/// </summary>
public static class StarsCatalog
{
    public const string Currency = "RUB";

    public static readonly StarsTopupOffer[] TopupOffers =
    [
        new(100, 18199, false), new(150, 26499, true), new(250, 42900, false), new(350, 59900, true),
        new(500, 84900, false), new(750, 125900, true), new(1000, 167900, false), new(1500, 249900, true),
        new(2500, 419900, false), new(5000, 829900, true), new(10000, 1659900, false), new(25000, 4149900, true),
        new(50000, 8299900, false), new(100000, 16599900, true), new(150000, 24999900, false)
    ];

    public static readonly StarsGiveawayOffer[] GiveawayOffers =
    [
        new(500, 1, 84900, false, [(1, 500, false), (3, 167, false), (5, 100, true)]),
        new(1000, 2, 167900, false, [(1, 1000, false), (3, 334, false), (5, 200, true), (7, 143, false), (10, 100, false)]),
        new(1500, 3, 249900, true, [(1, 1500, false), (3, 500, false), (5, 300, true), (7, 215, false), (10, 150, false)]),
        new(2500, 5, 419900, true, [(1, 2500, false), (3, 834, false), (5, 500, true), (7, 358, false), (10, 250, false), (25, 100, false)]),
        new(5000, 10, 829900, false,
            [(1, 5000, false), (3, 1667, false), (5, 1000, true), (7, 715, false), (10, 500, false), (25, 200, false), (50, 100, false)]),
        new(10000, 20, 1659900, true,
            [(1, 10000, false), (3, 3334, false), (5, 2000, true), (7, 1429, false), (10, 1000, false), (25, 400, false), (50, 200, false),
             (100, 100, false)]),
        new(25000, 50, 4149900, true,
            [(1, 25000, false), (3, 8334, false), (5, 5000, true), (7, 3572, false), (10, 2500, false), (25, 1000, false), (50, 500, false),
             (100, 250, false), (200, 125, false)]),
        new(50000, 100, 8299900, false,
            [(1, 50000, false), (3, 16667, false), (5, 10000, true), (7, 7143, false), (10, 5000, false), (25, 2000, false), (50, 1000, false),
             (100, 500, false), (200, 250, false), (300, 167, false), (500, 100, false)]),
        new(100000, 200, 16599900, true,
            [(1, 100000, false), (3, 33334, false), (5, 20000, true), (7, 14286, false), (10, 10000, false), (25, 4000, false),
             (50, 2000, false), (100, 1000, false), (200, 500, false), (300, 334, false), (500, 200, false)]),
        new(150000, 300, 24999900, true,
            [(1, 150000, false), (3, 50000, false), (5, 30000, true), (7, 21429, false), (10, 15000, false), (25, 6000, false),
             (50, 3000, false), (100, 1500, false), (200, 750, false), (300, 500, false), (500, 300, false)])
    ];

    public static StarsTopupOffer? FindTopup(long stars) => TopupOffers.FirstOrDefault(o => o.Stars == stars);

    public static StarsGiveawayOffer? FindGiveaway(long stars) => GiveawayOffers.FirstOrDefault(o => o.Stars == stars);
}
