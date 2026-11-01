using MyTelegram.Messenger.Services.Business;
using MyTelegram.Schema.Messages;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Rename a <a href="https://corefork.telegram.org/api/business#quick-reply-shortcuts">quick reply shortcut</a>.<br/>
/// This will emit an <a href="https://corefork.telegram.org/constructor/updateQuickReplies">updateQuickReplies</a> update to other logged-in sessions.
/// Possible errors
/// Code Type Description
/// 400 SHORTCUT_INVALID The specified shortcut is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.editQuickReplyShortcut"/> </c></para>
/// </summary>
internal sealed class EditQuickReplyShortcutHandler(ICommandBus commandBus, IQuickReplyService quickReplyService)
    : RpcResultObjectHandler<RequestEditQuickReplyShortcut, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestEditQuickReplyShortcut obj)
    {
        if (!QuickReplyService.IsValidShortcutName(obj.Shortcut))
        {
            RpcErrors.RpcErrors400.ShortcutInvalid.ThrowRpcError();
        }

        // Existence and name uniqueness are checked by the aggregate.
        await commandBus.PublishAsync(new RenameShortcutCommand(QuickReplyListId.Create(input.UserId),
            input.ToRequestInfo(), input.UserId, obj.ShortcutId, obj.Shortcut));

        var shortcuts = await quickReplyService.GetShortcutsAsync(input.UserId);
        var renamed = shortcuts
            .Select(p => p.ShortcutId == obj.ShortcutId ? p with { Name = obj.Shortcut } : p)
            .ToList();
        await quickReplyService.PushToOtherSessionsAsync(input, new TUpdateQuickReplies
        {
            QuickReplies = new TVector<IQuickReply>(await quickReplyService.BuildQuickRepliesAsync(input.UserId,
                renamed))
        });

        return new TBoolTrue();
    }
}
