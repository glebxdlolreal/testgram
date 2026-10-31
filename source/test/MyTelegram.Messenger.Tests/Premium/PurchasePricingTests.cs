using EventFlow.Queries;
using Moq;
using MyTelegram.Messenger.Options;
using MyTelegram.Messenger.Services.Impl;
using MyTelegram.Messenger.Services.Payments;

namespace MyTelegram.Messenger.Tests.Premium;

public class PurchasePricingTests
{
    private static PurchasePricing Pricing(PremiumOptions? options = null) =>
        new(new Mock<IQueryProcessor>().Object, new CountryHelper(), Microsoft.Extensions.Options.Options.Create(options ?? new PremiumOptions()));

    [Theory]
    [InlineData("79968980414", "RU", "RUB")]
    [InlineData("77011234567", "KZ", "KZT")]
    [InlineData("46701234567", "SE", "SEK")]
    [InlineData("447700900123", "GB", "GBP")]
    [InlineData("4915112345678", "DE", "EUR")]
    [InlineData("905321234567", "TR", "TRY")]
    [InlineData("12025550123", "US", "USD")]
    [InlineData("14035550123", "CA", "CAD")]
    [InlineData("380501234567", "UA", "UAH")]
    [InlineData("+81 90 1234 5678", "JP", "JPY")]
    public void The_currency_follows_the_country_of_the_phone_number(string phone, string country, string currency)
    {
        var pricing = Pricing();
        Assert.Equal(country, pricing.CountryFor(phone));
        Assert.Equal(currency, pricing.CurrencyFor(phone));
    }

    [Theory]
    [InlineData("8888888")]
    [InlineData("88804123456")]
    public void Anonymous_numbers_pay_in_dollars(string phone)
    {
        var pricing = Pricing();
        Assert.Equal("FT", pricing.CountryFor(phone));
        Assert.Equal("USD", pricing.ForPhone(phone).Currency);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("989121234567")] // Iran: IRR is neither Telegram's nor Stripe's
    public void Numbers_without_a_payable_currency_pay_in_dollars(string? phone) =>
        Assert.Equal("USD", Pricing().ForPhone(phone).Currency);

    [Fact]
    public void Roubles_are_the_tables_own_prices()
    {
        var prices = Pricing().ForPhone("79968980414");
        Assert.Equal(29900, prices.Price(29900));
        Assert.Equal(8299900, prices.StarsPrice(8299900));
    }

    [Fact]
    public void Other_currencies_are_converted_and_end_in_nine()
    {
        var prices = Pricing().ForPhone("46701234567");
        var month = prices.Price(29900);
        Assert.Equal(99, month % 100);
        var stars = prices.StarsPrice(8299900);
        // Real Telegram asks 9 599,00 SEK for 50 000 Stars; the converted price lands near it and ends in 9.
        Assert.InRange(stars, 900000, 1100000);
        Assert.Equal(0, stars % 100);
        Assert.Equal(9, stars / 100 % 10);
    }

    [Theory]
    [InlineData(4.62, 2, 4.99)]
    [InlineData(35.6, 2, 35.99)]
    [InlineData(0.2, 2, 0.99)]
    [InlineData(702.3, 2, 699)]
    [InlineData(9603, 2, 9599)]
    [InlineData(28612, 2, 28599)]
    [InlineData(1532, 0, 1529)]
    [InlineData(57, 0, 57)]
    public void Prices_are_rounded_like_real_telegrams(double major, int exponent, double expected) =>
        Assert.Equal((decimal)expected, PurchasePrices.RoundLikeTelegram((decimal)major, exponent));

    [Fact]
    public void Configured_rates_replace_the_built_in_ones()
    {
        var prices = Pricing(new PremiumOptions { ExchangeRates = new() { ["USD"] = 0.01m } }).ForPhone("12025550123");
        Assert.Equal(299, prices.Price(29900)); // 299 RUB = 2.99 USD
    }

    [Fact]
    public void Zero_decimal_currencies_are_whole_units()
    {
        var prices = Pricing().ForPhone("819012345678");
        var amount = prices.Price(29900);
        // 299 RUB is several hundred yen, quoted in whole yen.
        Assert.InRange(amount, 300, 1500);
    }

    [Theory]
    [InlineData(199000, "RUB", "1\u00a0990,00\u00a0RUB")]
    [InlineData(959900, "SEK", "9.599,00\u00a0SEK")]
    [InlineData(69900, "GBP", "£699.00")]
    [InlineData(3211220, "TRY", "32.112,20\u00a0TRY")]
    [InlineData(499, "USD", "$4.99")]
    [InlineData(1529, "JPY", "¥1,529")]
    public void Amounts_are_written_the_way_real_telegram_writes_them(long amount, string currency, string expected) =>
        Assert.Equal(expected, CurrencyFormatter.Format(amount, currency));
}
