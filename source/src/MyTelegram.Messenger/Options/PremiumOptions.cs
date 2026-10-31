namespace MyTelegram.Messenger.Options;

/// <summary>One purchasable Telegram Premium term.</summary>
public class PremiumPeriodOption
{
    public int Months { get; set; }

    /// <summary>Price in the smallest units of <see cref="PremiumOptions.Currency"/> (29900 = 299,00 RUB); 0 means not sold for money.</summary>
    public long Amount { get; set; }

    /// <summary>
    /// Price of one gifted subscription of this term when it differs from <see cref="Amount"/> (real
    /// Telegram sells a year for 1 990 RUB but gifts it for 2 539); 0 means the same as <see cref="Amount"/>.
    /// </summary>
    public long GiftAmount { get; set; }

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
/// <c>Premium__Periods__0__Amount=199000</c>, <c>Premium__Periods__0__GiftAmount=253900</c>, <c>Premium__Periods__0__AmountStars=2500</c>,
/// <c>Premium__Periods__0__Subscription=true</c>, <c>Premium__Periods__0__Gift=true</c>. When no
/// periods are configured <see cref="DefaultPeriods"/> applies; configured periods replace the
/// defaults rather than being appended to them.
/// </remarks>
/// <summary>The price of gifting a term to several users at once (real Telegram's is not linear).</summary>
public class PremiumGiftPrice
{
    public int Months { get; set; }
    public int Users { get; set; }
    public long Amount { get; set; }
}

public class PremiumOptions
{
    public const string SectionName = "Premium";

    /// <summary>
    /// Real Telegram's terms and prices for a Russian account (help.getPremiumPromo and
    /// payments.getPremiumGiftCodeOptions, measured 2026-10-31): subscriptions for 1, 12 and 24 months,
    /// gifts for 3, 6 and 12, and Stars gifts at 1000/1500/2500.
    /// </summary>
    public static readonly IReadOnlyList<PremiumPeriodOption> DefaultPeriods =
    [
        new() { Months = 1, Amount = 29900, Subscription = true },
        new() { Months = 3, Amount = 104900, AmountStars = 1000, Gift = true },
        new() { Months = 6, Amount = 139900, AmountStars = 1500, Gift = true },
        new() { Months = 12, Amount = 199000, GiftAmount = 253900, AmountStars = 2500, Subscription = true, Gift = true },
        new() { Months = 24, Amount = 299000, Subscription = true }
    ];

    /// <summary>Winner counts a giveaway may have beyond the 1–10 gift codes, as real Telegram offers them.</summary>
    public static readonly int[] GiveawayExtraQuantities = [25, 50, 100];

    /// <summary>Real Telegram's prices for gifting a term to several users (measured 2026-10-31, RUB).</summary>
    public static readonly IReadOnlyList<PremiumGiftPrice> DefaultGiftPrices = BuildGiftPrices(
        (12, [253900, 499900, 759900, 999900, 1269900, 1499900, 1799900, 2039900, 2299900, 2539900, 6399900, 12699900, 25399900]),
        (6, [139900, 279900, 419900, 559900, 699900, 839900, 999900, 1099900, 1259900, 1399900, 3499900, 6999900, 13999900]),
        (3, [104900, 209900, 319900, 419900, 529900, 629900, 739900, 839900, 949900, 1049900, 2599900, 5299900, 10499900]));

    /// <summary>Currency code (ISO 4217) for every money price here.</summary>
    public string Currency { get; set; } = "RUB";

    /// <summary>Gift prices by number of users; when none are configured <see cref="DefaultGiftPrices"/> applies.</summary>
    public List<PremiumGiftPrice>? GiftPrices { get; set; }

    public List<PremiumPeriodOption>? Periods { get; set; }

    /// <summary>
    /// The picture on @PremiumBot's invoices, real Telegram's own (telegram.org, 800x650 JPEG, measured
    /// 2026-10-31). It is registered with the file-server so clients download it through this server.
    /// An empty URL sends invoices without a picture.
    /// </summary>
    public string InvoicePhotoUrl { get; set; } =
        "https://telegram.org/file/464001019/11ed9/cJ2gxYh2KJs.29672/31f9128ef9c4794644";

    public string InvoicePhotoMimeType { get; set; } = "image/jpeg";

    public int InvoicePhotoSize { get; set; } = 29672;

    public int InvoicePhotoWidth { get; set; } = 800;

    public int InvoicePhotoHeight { get; set; } = 650;

    /// <summary>
    /// The terms a recurring @PremiumBot invoice links to (<c>invoice.terms_url</c>, which clients make
    /// the buyer accept); real Telegram's, measured 2026-10-31.
    /// </summary>
    public string TermsUrl { get; set; } = "https://telegra.ph/End-User-License-Agreement-09-05-2";

    /// <summary>How fast accounts without Premium may upload and download, see <see cref="PremiumSpeedLimitOptions"/>.</summary>
    public PremiumSpeedLimitOptions SpeedLimit { get; set; } = new();

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

    /// <summary>
    /// What gifting <paramref name="months"/> to <paramref name="users"/> people costs: the price table
    /// when it has the pair, otherwise the term's single gift price times the number of users; 0 when
    /// the term is not sold as a gift for money.
    /// </summary>
    public long GiftPrice(int months, int users)
    {
        if (users <= 0 || FindGift(months) is not { } gift)
        {
            return 0;
        }

        var table = GiftPrices is { Count: > 0 } ? GiftPrices : DefaultGiftPrices;
        var listed = table.FirstOrDefault(p => p.Months == months && p.Users == users);
        if (listed is { Amount: > 0 })
        {
            return listed.Amount;
        }

        var single = gift.GiftAmount > 0 ? gift.GiftAmount : gift.Amount;
        return single * users;
    }

    private static List<PremiumGiftPrice> BuildGiftPrices(params (int Months, long[] Amounts)[] rows)
    {
        int[] users = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, .. GiveawayExtraQuantities];
        return rows.SelectMany(row => row.Amounts.Select((amount, i) => new PremiumGiftPrice
        {
            Months = row.Months, Users = users[i], Amount = amount
        })).ToList();
    }
}

/// <summary>
/// The upload/download speed cap for accounts without Premium. Past it the server answers
/// <c>upload.saveFilePart</c>, <c>upload.saveBigFilePart</c> and <c>upload.getFile</c> with
/// <c>420 FLOOD_PREMIUM_WAIT_X</c>: the client retries by itself after X seconds and offers Premium
/// to speed the transfer up. See https://corefork.telegram.org/api/files
/// </summary>
/// <remarks>
/// Bound from <c>Premium:SpeedLimit</c>, e.g. <c>Premium__SpeedLimit__Enabled=false</c>,
/// <c>Premium__SpeedLimit__UploadBytesPerSecond=1048576</c>. Each account first gets
/// <see cref="BurstBytes"/> at full speed in each direction; the allowance then refills at the
/// configured rate, so a short transfer never notices the cap and a long one settles at it.
/// </remarks>
public class PremiumSpeedLimitOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Sustained upload rate for an account without Premium, in bytes per second.</summary>
    public long UploadBytesPerSecond { get; set; } = 1024 * 1024;

    /// <summary>Sustained download rate for an account without Premium, in bytes per second.</summary>
    public long DownloadBytesPerSecond { get; set; } = 1024 * 1024;

    /// <summary>How much an account may move at full speed before the cap applies, per direction.</summary>
    public long BurstBytes { get; set; } = 512L * 1024 * 1024;
}
