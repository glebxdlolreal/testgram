using MyTelegram.Messenger.Handlers.LatestLayer.Account;

namespace MyTelegram.Messenger.Tests.Business;

/// <summary>
/// Feature: <a href="https://corefork.telegram.org/method/account.updateBirthday">account.updateBirthday</a>
/// refuses dates that do not exist with <c>BIRTHDAY_INVALID</c>.
/// </summary>
public class BirthdayValidationTests
{
    [Theory]
    [InlineData(29, 2, null)]
    [InlineData(31, 12, null)]
    [InlineData(29, 2, 2000)]
    [InlineData(1, 1, 1990)]
    public void Accepts_real_dates(int day, int month, int? year)
    {
        UpdateBirthdayHandler.IsValid(day, month, year).ShouldBeTrue();
    }

    [Theory]
    [InlineData(0, 1, null)]
    [InlineData(32, 1, null)]
    [InlineData(30, 2, null)]
    [InlineData(1, 13, null)]
    [InlineData(29, 2, 2001)]
    [InlineData(1, 1, 1700)]
    public void Rejects_impossible_dates(int day, int month, int? year)
    {
        UpdateBirthdayHandler.IsValid(day, month, year).ShouldBeFalse();
    }

    [Fact]
    public void Rejects_a_birthday_in_a_future_year()
    {
        UpdateBirthdayHandler.IsValid(1, 1, DateTime.UtcNow.Year + 1).ShouldBeFalse();
    }
}
