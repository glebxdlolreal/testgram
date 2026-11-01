using MyTelegram.Messenger.Services.Business;
using MyTelegram.Schema.Messages;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Completely delete a <a href="https://corefork.telegram.org/api/business#quick-reply-shortcuts">quick reply shortcut</a>.<br/>
/// This will also emit an <a href="https://corefork.telegram.org/constructor/updateDeleteQuickReply">updateDeleteQuickReply</a> update to other logged-in sessions (and <strong>no</strong> <a href="https://corefork.telegram.org/constructor/updateDeleteQuickReplyMessages">updateDeleteQuickReplyMessages</a> updates, even if all the messages in the shortcuts are also deleted by this method).
/// Possible errors
/// Code Type Description
/// 400 SHORTCUT_INVALID The specified shortcut is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.deleteQuickReplyShortcut"/> </c></para>
/// </summary>
internal sealed class DeleteQuickReplyShortcutHandler(ICommandBus commandBus, IQuickReplyService quickReplyService)
    : RpcResultObjectHandler<RequestDeleteQuickReplyShortcut, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestDeleteQuickReplyShortcut obj)
    {
        var userId = input.UserId;
        var messages = await quickReplyService.GetMessagesAsync(userId, [obj.ShortcutId]);
        var messageIds = messages.Select(p => p.MessageId).ToList();
        var requestInfo = input.ToRequestInfo();

        await commandBus.PublishAsync(new DeleteShortcutCommand(QuickReplyListId.Create(userId), requestInfo,
            userId, obj.ShortcutId, messageIds));

        foreach (var messageId in messageIds)
        {
            await commandBus.PublishAsync(new DeleteQuickReplyMessageCommand(
                MessageId.Create(userId, messageId, true), requestInfo));
        }

        await quickReplyService.PushToOtherSessionsAsync(input,
            new TUpdateDeleteQuickReply { ShortcutId = obj.ShortcutId });

        return new TBoolTrue();
    }
}
