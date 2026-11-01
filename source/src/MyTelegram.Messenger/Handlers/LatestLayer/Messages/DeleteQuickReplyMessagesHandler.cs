using MyTelegram.Messenger.Services.Business;
using MyTelegram.Schema.Messages;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Delete one or more messages from a <a href="https://corefork.telegram.org/api/business#quick-reply-shortcuts">quick reply shortcut</a>. This will also emit an <a href="https://corefork.telegram.org/constructor/updateDeleteQuickReplyMessages">updateDeleteQuickReplyMessages</a> update.
/// Possible errors
/// Code Type Description
/// 400 SHORTCUT_INVALID The specified shortcut is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.deleteQuickReplyMessages"/> </c></para>
/// </summary>
internal sealed class DeleteQuickReplyMessagesHandler(ICommandBus commandBus, IQuickReplyService quickReplyService)
    : RpcResultObjectHandler<RequestDeleteQuickReplyMessages, IUpdates>
{
    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestDeleteQuickReplyMessages obj)
    {
        var userId = input.UserId;
        var shortcuts = await quickReplyService.GetShortcutsAsync(userId);
        if (shortcuts.All(p => p.ShortcutId != obj.ShortcutId))
        {
            RpcErrors.RpcErrors400.ShortcutInvalid.ThrowRpcError();
        }

        var messages = await quickReplyService.GetMessagesAsync(userId, [obj.ShortcutId]);
        var requested = obj.Id?.ToHashSet() ?? [];
        var deletedIds = messages.Where(p => requested.Contains(p.MessageId)).Select(p => p.MessageId).ToList();
        var requestInfo = input.ToRequestInfo();
        foreach (var messageId in deletedIds)
        {
            await commandBus.PublishAsync(new DeleteQuickReplyMessageCommand(
                MessageId.Create(userId, messageId, true), requestInfo));
        }

        var updates = new List<IUpdate>
        {
            new TUpdateDeleteQuickReplyMessages
            {
                ShortcutId = obj.ShortcutId,
                Messages = new TVector<int>(deletedIds)
            }
        };

        // A shortcut without messages no longer exists for clients (tdlib drops count == 0).
        if (deletedIds.Count > 0 && deletedIds.Count == messages.Count)
        {
            await commandBus.PublishAsync(new DeleteShortcutCommand(QuickReplyListId.Create(userId), requestInfo,
                userId, obj.ShortcutId, deletedIds));
            updates.Add(new TUpdateDeleteQuickReply { ShortcutId = obj.ShortcutId });
        }

        await quickReplyService.PushToOtherSessionsAsync(input, [.. updates]);

        return new TUpdates
        {
            Updates = new TVector<IUpdate>(updates),
            Users = [],
            Chats = [],
            Date = DateTime.UtcNow.ToTimestamp(),
            Seq = 0
        };
    }
}
