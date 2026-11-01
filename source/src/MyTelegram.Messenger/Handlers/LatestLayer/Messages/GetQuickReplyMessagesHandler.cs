using MyTelegram.Messenger.Services.Business;
using MyTelegram.Schema.Messages;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Fetch (a subset or all) messages in a <a href="https://corefork.telegram.org/api/business#quick-reply-shortcuts">quick reply shortcut »</a>.
/// Possible errors
/// Code Type Description
/// 400 SHORTCUT_INVALID The specified shortcut is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getQuickReplyMessages"/> </c></para>
/// </summary>
internal sealed class GetQuickReplyMessagesHandler(
    IQuickReplyService quickReplyService,
    IMessageConverterService messageConverterService,
    IUserConverterService userConverterService)
    : RpcResultObjectHandler<RequestGetQuickReplyMessages, IMessages>
{
    protected override async Task<IMessages> HandleCoreAsync(IRequestInput input, RequestGetQuickReplyMessages obj)
    {
        var userId = input.UserId;
        var shortcuts = await quickReplyService.GetShortcutsAsync(userId);
        if (shortcuts.All(p => p.ShortcutId != obj.ShortcutId))
        {
            RpcErrors.RpcErrors400.ShortcutInvalid.ThrowRpcError();
        }

        var messages = await quickReplyService.GetMessagesAsync(userId, [obj.ShortcutId],
            obj.Id?.Count > 0 ? obj.Id.ToList() : null);

        // tdlib hashes its whole cached shortcut, so the hash only means something for a full fetch.
        if (obj.Hash != 0 && !(obj.Id?.Count > 0))
        {
            var hash = QuickReplyService.ComputeMessagesHash(messages.Select(p => (p.MessageId, p.EditDate ?? 0)));
            if (hash == obj.Hash)
            {
                return new TMessagesNotModified { Count = messages.Count };
            }
        }

        var users = await userConverterService.GetUserListAsync(input, [userId], layer: input.Layer);
        var ordered = messages.OrderByDescending(p => p.MessageId).ToList();

        return new TMessages
        {
            Messages = new TVector<IMessage>(messageConverterService.ToMessageList(userId, ordered, null, null,
                null, input.Layer)),
            Chats = [],
            Users = new TVector<IUser>(users),
            Topics = []
        };
    }
}
