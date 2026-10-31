namespace MyTelegram.Messenger.Options;

/// <summary>One purchasable Telegram Premium term.</summary>
public class PremiumPeriodOption
{
    public int Months { get; set; }

    /// <summary>Price in the smallest units of <see cref="PremiumOptions.Currency"/> (499 = $4.99); 0 means not sold for money.</summary>
    public long Amount { get; set; }

    /// <summary>Price in Telegram Stars when gifted through <c>inputInvoicePremiumGiftStars</c>; 0 means not offered for Stars.</summary>
    public long AmountStars { get; set; }

    /// <summary>Sold as a subscription for oneself: listed in <c>help.getPremiumPromo</c> and by @PremiumBot.</summary>
    public bool Subscription { get; set; }

    /// <summary>Sold as a gift: listed by <c>payments.getPremiumGiftCodeOptions</c>.</summary>
    public bool Gift { get; set; }
}

/// <summary>
/// Telegram Premium pricing, the single source every price-quoting path reads: the promo
/// (<c>help.getPremiumPromo</c>), gift options, payment forms, Stars checkout and @PremiumBot.
/// </summary>
/// <remarks>
/// Bound from the <c>Premium</c> section, e.g. <c>Premium__Periods__0__Months=12</c>,
/// <c>Premium__Periods__0__Amount=3999</c>, <c>Premium__Periods__0__AmountStars=2500</c>,
/// <c>Premium__Periods__0__Subscription=true</c>, <c>Premium__Periods__0__Gift=true</c>. When no
/// periods are configured <see cref="DefaultPeriods"/> applies; configured periods replace the
/// defaults rather than being appended to them.
/// </remarks>
public class PremiumOptions
{
    public const string SectionName = "Premium";

    /// <summary>
    /// The terms real Telegram sells: subscriptions for 1, 12 and 24 months, gifts for 3, 6 and 12,
    /// and Stars gifts at 1000/1500/2500 (measured on help.getPremiumPromo and
    /// payments.getPremiumGiftCodeOptions, 2026-10-31). The money prices are this server's own.
    /// </summary>
    public static readonly IReadOnlyList<PremiumPeriodOption> DefaultPeriods =
    [
        new() { Months = 1, Amount = 499, Subscription = true },
        new() { Months = 3, Amount = 1299, AmountStars = 1000, Gift = true },
        new() { Months = 6, Amount = 2499, AmountStars = 1500, Gift = true },
        new() { Months = 12, Amount = 3999, AmountStars = 2500, Subscription = true, Gift = true },
        new() { Months = 24, Amount = 6999, Subscription = true }
    ];

    /// <summary>Currency code (ISO 4217) for <see cref="PremiumPeriodOption.Amount"/>.</summary>
    public string Currency { get; set; } = "USD";

    public List<PremiumPeriodOption>? Periods { get; set; }

    /// <summary>The configured terms, ordered by length, with invalid rows dropped.</summary>
    public IReadOnlyList<PremiumPeriodOption> GetPeriods()
    {
        var periods = Periods is { Count: > 0 } ? Periods : DefaultPeriods;
        return periods
            .Where(p => p.Months > 0 && (p.Amount > 0 || p.AmountStars > 0))
            .GroupBy(p => p.Months)
            .Select(g => g.First())
            .OrderBy(p => p.Months)
            .ToList();
    }

    public PremiumPeriodOption? FindPeriod(int months) => GetPeriods().FirstOrDefault(p => p.Months == months);

    /// <summary>A term sold as a subscription for oneself, priced in <see cref="Currency"/>.</summary>
    public PremiumPeriodOption? FindSubscription(int months) =>
        GetPeriods().FirstOrDefault(p => p.Months == months && p.Subscription && p.Amount > 0);

    /// <summary>A term sold as a gift.</summary>
    public PremiumPeriodOption? FindGift(int months) =>
        GetPeriods().FirstOrDefault(p => p.Months == months && p.Gift);
}
