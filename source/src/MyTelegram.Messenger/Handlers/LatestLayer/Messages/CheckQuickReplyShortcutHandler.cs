using MyTelegram.Messenger.Services.Business;
using MyTelegram.Schema.Messages;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Before offering the user the choice to add a message to a <a href="https://corefork.telegram.org/api/business#quick-reply-shortcuts">quick reply shortcut</a>,
/// to make sure that none of the limits specified <a href="https://corefork.telegram.org/api/business#quick-reply-shortcuts">here »</a> were reached.
/// Possible errors
/// Code Type Description
/// 400 SHORTCUT_INVALID The specified shortcut is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.checkQuickReplyShortcut"/> </c></para>
/// </summary>
internal sealed class CheckQuickReplyShortcutHandler(IQuickReplyService quickReplyService)
    : RpcResultObjectHandler<RequestCheckQuickReplyShortcut, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestCheckQuickReplyShortcut obj)
    {
        if (!QuickReplyService.IsValidShortcutName(obj.Shortcut))
        {
            RpcErrors.RpcErrors400.ShortcutInvalid.ThrowRpcError();
        }

        return await quickReplyService.CanAddMessagesAsync(input.UserId, obj.Shortcut)
            ? new TBoolTrue()
            : new TBoolFalse();
    }
}
