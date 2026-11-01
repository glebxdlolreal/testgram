using MyTelegram.Messenger.Services.Business;
using MyTelegram.Schema.Messages;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Reorder <a href="https://corefork.telegram.org/api/business#quick-reply-shortcuts">quick reply shortcuts</a>.<br/>
/// This will emit an <a href="https://corefork.telegram.org/constructor/updateQuickReplies">updateQuickReplies</a> update to other logged-in sessions.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.reorderQuickReplies"/> </c></para>
/// </summary>
internal sealed class ReorderQuickRepliesHandler(ICommandBus commandBus, IQuickReplyService quickReplyService)
    : RpcResultObjectHandler<RequestReorderQuickReplies, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestReorderQuickReplies obj)
    {
        var order = obj.Order?.ToList() ?? [];
        var shortcuts = await quickReplyService.GetShortcutsAsync(input.UserId);
        if (order.Distinct().Count() != order.Count || order.Any(id => shortcuts.All(p => p.ShortcutId != id)))
        {
            RpcErrors.RpcErrors400.ShortcutInvalid.ThrowRpcError();
        }

        await commandBus.PublishAsync(new ReorderShortcutsCommand(QuickReplyListId.Create(input.UserId),
            input.ToRequestInfo(), input.UserId, order));

        var reordered = order.Select(id => shortcuts.First(p => p.ShortcutId == id)).ToList();
        reordered.AddRange(shortcuts.Where(p => !order.Contains(p.ShortcutId)));
        if (!reordered.SequenceEqual(shortcuts))
        {
            await quickReplyService.PushToOtherSessionsAsync(input, new TUpdateQuickReplies
            {
                QuickReplies = new TVector<IQuickReply>(
                    await quickReplyService.BuildQuickRepliesAsync(input.UserId, reordered))
            });
        }

        return new TBoolTrue();
    }
}
