namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Update our <a href="https://corefork.telegram.org/api/profile#birthday">birthday, see here »</a> for more info.
/// Possible errors
/// Code Type Description
/// 400 BIRTHDAY_INVALID An invalid age was specified, must be between 0 and 150 years.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.updateBirthday"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class UpdateBirthdayHandler(ICommandBus commandBus) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestUpdateBirthday, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestUpdateBirthday obj)
    {
        Birthday? birthday = null;
        if (obj.Birthday != null)
        {
            if (!IsValid(obj.Birthday.Day, obj.Birthday.Month, obj.Birthday.Year))
            {
                RpcErrors.RpcErrors400.BirthdayInvalid.ThrowRpcError();
            }

            birthday = new Birthday(obj.Birthday.Day, obj.Birthday.Month, obj.Birthday.Year);
        }

        var command = new UpdateBirthdayCommand(UserId.Create(input.UserId), birthday);
        await commandBus.PublishAsync(command);
        return new TBoolTrue();
    }

    /// <summary>
    /// A real calendar date; without a year Feb 29 is allowed. With a year the age has to be
    /// 0..150, as the method's error description says.
    /// </summary>
    internal static bool IsValid(int day, int month, int? year)
    {
        if (month is < 1 or > 12 || day < 1)
        {
            return false;
        }

        if (year == null)
        {
            return day <= DateTime.DaysInMonth(2000, month);
        }

        var today = DateTime.UtcNow.Date;
        if (year < today.Year - 150 || year > today.Year || day > DateTime.DaysInMonth(year.Value, month))
        {
            return false;
        }

        return new DateTime(year.Value, month, day) <= today.AddDays(1);
    }
}
