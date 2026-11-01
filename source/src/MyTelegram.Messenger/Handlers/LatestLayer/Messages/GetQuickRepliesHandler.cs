using MyTelegram.Messenger.Services.Business;
using MyTelegram.Schema.Messages;
using TQuickReply = MyTelegram.Schema.TQuickReply;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Fetch basic info about all existing <a href="https://corefork.telegram.org/api/business#quick-reply-shortcuts">quick reply shortcuts</a>.
/// <para>Each shortcut comes with its first message: tdlib drops a shortcut whose <c>top_message</c> is not in
/// <c>messages</c>, and builds its cache hash from exactly that message.</para>
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getQuickReplies"/> </c></para>
/// </summary>
internal sealed class GetQuickRepliesHandler(
    IQuickReplyService quickReplyService,
    IMessageConverterService messageConverterService,
    IUserConverterService userConverterService)
    : RpcResultObjectHandler<RequestGetQuickReplies, IQuickReplies>
{
    protected override async Task<IQuickReplies> HandleCoreAsync(IRequestInput input, RequestGetQuickReplies obj)
    {
        var userId = input.UserId;
        var shortcuts = await quickReplyService.GetShortcutsAsync(userId);
        var messages = shortcuts.Count == 0
            ? []
            : await quickReplyService.GetMessagesAsync(userId, shortcuts.Select(p => p.ShortcutId).ToList());
        var byShortcut = messages
            .GroupBy(p => p.ShortcutId ?? 0)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.MessageId).ToList());

        // A shortcut whose messages are all gone is not listed (tdlib discards count == 0).
        var listed = shortcuts
            .Where(p => byShortcut.ContainsKey(p.ShortcutId))
            .Select(p => (Shortcut: p, Messages: byShortcut[p.ShortcutId]))
            .ToList();

        var hash = QuickReplyService.ComputeShortcutsHash(listed.Select(p =>
            (p.Shortcut, p.Messages[0].MessageId, p.Messages[0].EditDate ?? 0)));
        if (obj.Hash != 0 && obj.Hash == hash)
        {
            return new TQuickRepliesNotModified();
        }

        var firstMessages = listed.Select(p => p.Messages[0]).ToList();
        var users = await userConverterService.GetUserListAsync(input, [userId], layer: input.Layer);

        return new TQuickReplies
        {
            QuickReplies = new TVector<IQuickReply>(listed.Select(p => (IQuickReply)new TQuickReply
            {
                ShortcutId = p.Shortcut.ShortcutId,
                Shortcut = p.Shortcut.Name,
                TopMessage = p.Messages[0].MessageId,
                Count = p.Messages.Count
            })),
            Messages = new TVector<IMessage>(messageConverterService.ToMessageList(userId, firstMessages, null,
                null, null, input.Layer)),
            Chats = [],
            Users = new TVector<IUser>(users)
        };
    }
}
