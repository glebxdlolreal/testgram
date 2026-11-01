using MyTelegram.Messenger.Services.Business;
using MyTelegram.Schema.Account;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

/// <summary>
/// Specify a set of <a href="https://corefork.telegram.org/api/business#opening-hours">Telegram Business opening hours</a>.<br/>
/// This info will be contained in <a href="https://corefork.telegram.org/constructor/userFull">userFull</a>.<c>business_work_hours</c>.
/// Possible errors
/// Code Type Description
/// 400 BUSINESS_WORK_HOURS_EMPTY No work hours were specified.
/// 400 BUSINESS_WORK_HOURS_PERIOD_INVALID The specified work hours are invalid, see <a href="https://corefork.telegram.org/api/business#opening-hours">here »</a> for the exact requirements.
/// 400 TIMEZONE_INVALID The specified timezone does not exist.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.updateBusinessWorkHours"/> </c></para>
/// </summary>
internal sealed class UpdateBusinessWorkHoursHandler(
    ICommandBus commandBus,
    IUserAppService userAppService,
    ITimezoneHelper timezoneHelper,
    IBusinessProfileService businessProfileService)
    : RpcResultObjectHandler<RequestUpdateBusinessWorkHours, IBool>
{
    /// <summary>Seven days with up to four intervals each is what the official apps let you enter.</summary>
    private const int MaxIntervals = 28;

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestUpdateBusinessWorkHours obj)
    {
        BusinessWorkHoursItem? item = null;
        if (obj.BusinessWorkHours is TBusinessWorkHours workHours)
        {
            await userAppService.CheckAccountPremiumStatusAsync(input.UserId);
            if (string.IsNullOrEmpty(workHours.TimezoneId) ||
                !timezoneHelper.TryGeTimeZoneItem(workHours.TimezoneId, out _))
            {
                RpcErrors.RpcErrors400.TimezoneInvalid.ThrowRpcError();
            }

            var requested = workHours.WeeklyOpen?
                .OfType<TBusinessWeeklyOpen>()
                .Select(p => new BusinessWeeklyOpenItem(p.StartMinute, p.EndMinute))
                .ToList() ?? [];
            if (requested.Count == 0)
            {
                RpcErrors.RpcErrors400.BusinessWorkHoursEmpty.ThrowRpcError();
            }

            if (requested.Count > MaxIntervals ||
                requested.Any(p => p.StartMinute < 0 || p.EndMinute > 8 * 24 * 60 || p.StartMinute >= p.EndMinute))
            {
                RpcErrors.RpcErrors400.BusinessWorkHoursPeriodInvalid.ThrowRpcError();
            }

            item = new BusinessWorkHoursItem(workHours.TimezoneId, BusinessProfileService.Normalize(requested));
        }

        await commandBus.PublishAsync(new UpdateBusinessWorkHoursCommand(UserId.Create(input.UserId), item));
        await businessProfileService.NotifyUserUpdatedAsync(input);

        return new TBoolTrue();
    }
}
