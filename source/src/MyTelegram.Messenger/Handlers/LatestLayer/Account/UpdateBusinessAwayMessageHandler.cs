using MyTelegram.Messenger.Services.Business;
using MyTelegram.Schema.Account;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

/// <summary>
/// Set a list of <a href="https://corefork.telegram.org/api/business#away-messages">Telegram Business away messages</a>.
/// Calling the method without <c>message</c> disables away messages.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.updateBusinessAwayMessage"/> </c></para>
/// </summary>
internal sealed class UpdateBusinessAwayMessageHandler(
    ICommandBus commandBus,
    IUserAppService userAppService,
    IQuickReplyService quickReplyService,
    IBusinessRecipientsReader recipientsReader,
    IBusinessProfileService businessProfileService)
    : RpcResultObjectHandler<RequestUpdateBusinessAwayMessage, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestUpdateBusinessAwayMessage obj)
    {
        BusinessAwayItem? item = null;
        if (obj.Message is TInputBusinessAwayMessage message)
        {
            await userAppService.CheckAccountPremiumStatusAsync(input.UserId);
            await UpdateBusinessGreetingMessageHandler.EnsureShortcutAsync(quickReplyService, input.UserId,
                message.ShortcutId);

            var (kind, start, end) = message.Schedule switch
            {
                TBusinessAwayMessageScheduleAlways => (BusinessAwayScheduleKind.Always, (int?)null, (int?)null),
                TBusinessAwayMessageScheduleOutsideWorkHours => (BusinessAwayScheduleKind.OutsideWorkHours, null,
                    null),
                TBusinessAwayMessageScheduleCustom custom => (BusinessAwayScheduleKind.Custom, custom.StartDate,
                    custom.EndDate),
                _ => (BusinessAwayScheduleKind.Always, null, null)
            };
            if (kind == BusinessAwayScheduleKind.Custom && (start < 0 || start >= end))
            {
                RpcErrors.RpcErrors400.ScheduleDateInvalid.ThrowRpcError();
            }

            var recipients = await recipientsReader.ReadAsync(input, message.Recipients);
            item = new BusinessAwayItem(message.ShortcutId, recipients, kind, start, end, message.OfflineOnly,
                CurrentDate);
        }

        await commandBus.PublishAsync(new UpdateBusinessAwayMessageCommand(UserId.Create(input.UserId), item));
        await businessProfileService.NotifyUserUpdatedAsync(input);

        return new TBoolTrue();
    }
}
