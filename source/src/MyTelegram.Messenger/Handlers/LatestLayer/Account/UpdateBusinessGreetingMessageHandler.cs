using MyTelegram.Messenger.Services.Business;
using MyTelegram.Schema.Account;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

/// <summary>
/// Set a list of <a href="https://corefork.telegram.org/api/business#greeting-messages">Telegram Business greeting messages</a>.
/// Calling the method without <c>message</c> disables greetings.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.updateBusinessGreetingMessage"/> </c></para>
/// </summary>
internal sealed class UpdateBusinessGreetingMessageHandler(
    ICommandBus commandBus,
    IUserAppService userAppService,
    IQuickReplyService quickReplyService,
    IBusinessRecipientsReader recipientsReader,
    IBusinessProfileService businessProfileService)
    : RpcResultObjectHandler<RequestUpdateBusinessGreetingMessage, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestUpdateBusinessGreetingMessage obj)
    {
        BusinessGreetingItem? item = null;
        if (obj.Message is TInputBusinessGreetingMessage message)
        {
            await userAppService.CheckAccountPremiumStatusAsync(input.UserId);
            await EnsureShortcutAsync(quickReplyService, input.UserId, message.ShortcutId);

            // Only 7, 14, 21 and 28 days exist; tdlib rounds anything else the same way.
            var noActivityDays = Math.Clamp(message.NoActivityDays / 7 * 7, 7, 28);
            var recipients = await recipientsReader.ReadAsync(input, message.Recipients);
            item = new BusinessGreetingItem(message.ShortcutId, recipients, noActivityDays, CurrentDate);
        }

        await commandBus.PublishAsync(new UpdateBusinessGreetingMessageCommand(UserId.Create(input.UserId), item));
        await businessProfileService.NotifyUserUpdatedAsync(input);

        return new TBoolTrue();
    }

    /// <summary>The automatic message is the content of an existing, non-empty quick reply shortcut.</summary>
    internal static async Task EnsureShortcutAsync(IQuickReplyService quickReplyService, long userId, int shortcutId)
    {
        var shortcuts = await quickReplyService.GetShortcutsAsync(userId);
        if (shortcuts.All(p => p.ShortcutId != shortcutId) ||
            (await quickReplyService.GetMessagesAsync(userId, [shortcutId])).Count == 0)
        {
            RpcErrors.RpcErrors400.ShortcutInvalid.ThrowRpcError();
        }
    }
}
