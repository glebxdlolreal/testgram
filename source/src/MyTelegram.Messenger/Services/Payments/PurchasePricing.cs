using System.Collections.Concurrent;
using System.Globalization;
using MyTelegram.Messenger.Options;

namespace MyTelegram.Messenger.Services.Payments;

/// <summary>
/// The currency a buyer pays Premium and Stars in, and the prices in it. Like real Telegram, the
/// currency follows the country of the account's phone number (+46 pays in SEK, +44 in GBP, +7 9… in
/// RUB, +7 7… in KZT); anonymous +888 numbers, which belong to no country, and countries whose
/// currency Telegram or Stripe cannot take pay in USD.
/// </summary>
/// <remarks>
/// <para>The price tables (<see cref="PremiumOptions"/>, <see cref="StarsCatalog"/>) are real
/// Telegram's for one currency, measured where it can be. Other currencies are converted from them at
/// the fixed rates of <see cref="CurrencyTables.UnitsPerRouble"/> (overridable through
/// <see cref="PremiumOptions.ExchangeRates"/>) and rounded the way real Telegram's prices are, to an
/// amount ending in 9 (699.00, 9 599.00, 35.99). Fixed rates keep an invoice's price the same between
/// the form and the charge.</para>
/// </remarks>
public interface IPurchasePricing
{
    /// <summary>The prices for <paramref name="userId"/>, from the country of its phone number.</summary>
    Task<PurchasePrices> ForUserAsync(long userId);

    /// <summary>The prices for an account with <paramref name="phoneNumber"/> (digits, with or without +).</summary>
    PurchasePrices ForPhone(string? phoneNumber);
}

/// <summary>Prices in one buyer's currency.</summary>
public sealed class PurchasePrices(string currency, string premiumCurrency, Func<string, decimal?> unitsPerRouble)
{
    /// <summary>ISO 4217 code every amount from <see cref="Price"/> is in.</summary>
    public string Currency { get; } = currency;

    /// <summary>
    /// <paramref name="amount"/>, in the smallest units of the Premium price table's currency
    /// (<see cref="PremiumOptions.Currency"/>), in <see cref="Currency"/>'s smallest units (Telegram's
    /// exponent: whole yen, cents of a dollar); 0 stays 0.
    /// </summary>
    public long Price(long amount) => Price(amount, premiumCurrency);

    /// <summary>A <see cref="StarsCatalog"/> amount in <see cref="Currency"/>.</summary>
    public long StarsPrice(long amount) => Price(amount, StarsCatalog.Currency);

    private long Price(long amount, string sourceCurrency)
    {
        if (amount <= 0 || string.Equals(Currency, sourceCurrency, StringComparison.OrdinalIgnoreCase) ||
            unitsPerRouble(Currency) is not { } target || unitsPerRouble(sourceCurrency.ToUpperInvariant()) is not { } source)
        {
            return amount;
        }

        var major = amount / Pow10(CurrencyFormatter.Exponent(sourceCurrency)) * target / source;
        var exponent = CurrencyFormatter.Exponent(Currency);
        return (long)(RoundLikeTelegram(major, exponent) * Pow10(exponent));
    }

    /// <summary>Real Telegram's prices end in 9: 4.99 and 35.99 below 100, then 699, 9 599, 28 599.</summary>
    internal static decimal RoundLikeTelegram(decimal major, int exponent)
    {
        if (major < 100)
        {
            var whole = Math.Max(1m, Math.Round(major, MidpointRounding.AwayFromZero));
            return exponent > 0 ? whole - 1m / Pow10(exponent) : whole;
        }

        var digits = (int)Math.Floor(Math.Log10((double)major)) + 1;
        var step = Pow10(Math.Max(1, digits - 3));
        return Math.Round(major / step, MidpointRounding.AwayFromZero) * step - 1m;
    }

    private static decimal Pow10(int exponent)
    {
        var value = 1m;
        for (var i = 0; i < exponent; i++)
        {
            value *= 10;
        }

        return value;
    }
}

/// <inheritdoc cref="IPurchasePricing" />
public sealed class PurchasePricing(
    IQueryProcessor queryProcessor,
    ICountryHelper countryHelper,
    IOptions<PremiumOptions> premiumOptions) : IPurchasePricing, ITransientDependency
{
    /// <summary>Currency for numbers of no country (+888) or whose currency cannot be charged.</summary>
    public const string FallbackCurrency = "USD";

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<long, (string? Phone, DateTime At)> Phones = new();

    public async Task<PurchasePrices> ForUserAsync(long userId)
    {
        if (!Phones.TryGetValue(userId, out var cached) || DateTime.UtcNow - cached.At > CacheDuration)
        {
            var user = await queryProcessor.ProcessAsync(new GetUserByIdQuery(userId));
            cached = (user?.PhoneNumber, DateTime.UtcNow);
            Phones[userId] = cached;
        }

        return ForPhone(cached.Phone);
    }

    public PurchasePrices ForPhone(string? phoneNumber)
    {
        var premiumCurrency = premiumOptions.Value.Currency.ToUpperInvariant();
        var currency = CurrencyFor(phoneNumber);
        // Without rates for both currencies the tables' own currency is all that can be quoted honestly.
        if (UnitsPerRouble(currency) == null || UnitsPerRouble(premiumCurrency) == null ||
            UnitsPerRouble(StarsCatalog.Currency) == null)
        {
            currency = premiumCurrency;
        }

        return new PurchasePrices(currency, premiumCurrency, UnitsPerRouble);
    }

    /// <summary>The currency for the country of <paramref name="phoneNumber"/>, or <see cref="FallbackCurrency"/>.</summary>
    public string CurrencyFor(string? phoneNumber)
    {
        var iso2 = CountryFor(phoneNumber);
        return iso2 != null && CurrencyTables.CountryCurrencies.TryGetValue(iso2, out var currency)
            ? currency
            : FallbackCurrency;
    }

    /// <summary>
    /// The country of a phone number, telling apart countries that share a calling code by their
    /// prefixes (+7 7… is Kazakhstan, the rest of +7 Russia; +1 403… is Canada).
    /// </summary>
    public string? CountryFor(string? phoneNumber)
    {
        var digits = new string((phoneNumber ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        var code = countryHelper.GetCountryCodeByPhoneNumber(digits);
        if (string.IsNullOrEmpty(code))
        {
            return null;
        }

        var national = digits[code.Length..];
        string? fallback = null;
        string? best = null;
        var bestLength = -1;
        foreach (var country in countryHelper.GetAllCountryList())
        {
            foreach (var item in country.CountryCodes.Where(c => c.CountryCode == code))
            {
                if (item.Prefixes is not { Count: > 0 } prefixes)
                {
                    fallback ??= country.Iso2;
                    continue;
                }

                foreach (var prefix in prefixes.Where(p => national.StartsWith(p, StringComparison.Ordinal) && p.Length > bestLength))
                {
                    best = country.Iso2;
                    bestLength = prefix.Length;
                }
            }
        }

        return best ?? fallback;
    }

    private decimal? UnitsPerRouble(string currency)
    {
        if (premiumOptions.Value.ExchangeRates?.TryGetValue(currency, out var configured) == true && configured > 0)
        {
            return configured;
        }

        return CurrencyTables.UnitsPerRouble.TryGetValue(currency, out var rate) ? rate : null;
    }
}

/// <summary>Writes amounts the way real Telegram does for each currency ("1 990,00 RUB", "9.599,00 SEK", "£699.00").</summary>
public static class CurrencyFormatter
{
    /// <summary>Number of decimals Telegram uses for <paramref name="currency"/> (2 when unknown).</summary>
    public static int Exponent(string currency) =>
        CurrencyTables.Formats.TryGetValue(currency.ToUpperInvariant(), out var format) ? format.Exponent : 2;

    /// <summary><paramref name="amount"/> in the smallest units of <paramref name="currency"/>, formatted.</summary>
    public static string Format(long amount, string currency)
    {
        var code = currency.ToUpperInvariant();
        var format = CurrencyTables.Formats.TryGetValue(code, out var known)
            ? known
            : new CurrencyFormat(code, code, ",", ".", false, true, 2);
        var numbers = (NumberFormatInfo)NumberFormatInfo.InvariantInfo.Clone();
        numbers.NumberGroupSeparator = format.ThousandsSeparator;
        numbers.NumberDecimalSeparator = format.DecimalSeparator;
        var divisor = 1m;
        for (var i = 0; i < format.Exponent; i++)
        {
            divisor *= 10;
        }

        var pattern = format.Exponent > 0 ? "#,0." + new string('0', format.Exponent) : "#,0";
        var number = (amount / divisor).ToString(pattern, numbers);
        var space = format.SpaceBetween ? "\u00a0" : string.Empty;
        return format.SymbolLeft ? $"{format.Symbol}{space}{number}" : $"{number}{space}{format.Symbol}";
    }
}
