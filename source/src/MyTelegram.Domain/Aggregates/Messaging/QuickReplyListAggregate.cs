namespace MyTelegram.Domain.Aggregates.Messaging;

/// <summary>
/// The <a href="https://corefork.telegram.org/api/business#quick-reply-shortcuts">quick reply shortcuts</a>
/// of one user, in the order the user arranged them. Every event carries the complete resulting list, so
/// the read model never has to merge.
/// </summary>
[EnableAutoGeneration]
public class QuickReplyListAggregate : AggregateRoot<QuickReplyListAggregate, QuickReplyListId>
{
    private readonly QuickReplyListState _state = new();

    public QuickReplyListAggregate(QuickReplyListId id) : base(id)
    {
        Register(_state);
    }

    public IReadOnlyList<QuickReplyShortcut> Shortcuts => _state.Shortcuts;

    /// <summary>
    /// Called by the send path before the first message of a new shortcut is created. A shortcut is created
    /// implicitly by sending a message to a name that does not exist yet; a second send racing for the same
    /// name keeps the shortcut that won, and the caller reads the id back from the read model.
    /// </summary>
    public void CreateShortcut(long ownerUserId, int shortcutId, string name)
    {
        if (_state.Shortcuts.Any(p => p.ShortcutId == shortcutId ||
                                      string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var shortcuts = _state.Shortcuts.Append(new QuickReplyShortcut(shortcutId, name)).ToList();
        Emit(new QuickReplyShortcutCreatedEvent(ownerUserId, shortcutId, name, shortcuts));
    }

    public void RenameShortcut(RequestInfo requestInfo, long ownerUserId, int shortcutId, string name)
    {
        if (_state.Shortcuts.All(p => p.ShortcutId != shortcutId))
        {
            RpcErrors.RpcErrors400.ShortcutInvalid.ThrowRpcError();
        }

        if (_state.Shortcuts.Any(p => p.ShortcutId != shortcutId &&
                                      string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            RpcErrors.RpcErrors400.ShortcutInvalid.ThrowRpcError();
        }

        var shortcuts = _state.Shortcuts
            .Select(p => p.ShortcutId == shortcutId ? p with { Name = name } : p)
            .ToList();
        Emit(new QuickReplyShortcutRenamedEvent(requestInfo, ownerUserId, shortcutId, name, shortcuts));
    }

    /// <param name="messageIds">The messages of the shortcut; deleted together with it.</param>
    public void DeleteShortcut(RequestInfo requestInfo, long ownerUserId, int shortcutId, List<int> messageIds)
    {
        if (_state.Shortcuts.All(p => p.ShortcutId != shortcutId))
        {
            RpcErrors.RpcErrors400.ShortcutInvalid.ThrowRpcError();
        }

        var shortcuts = _state.Shortcuts.Where(p => p.ShortcutId != shortcutId).ToList();
        Emit(new QuickReplyShortcutDeletedEvent(requestInfo, ownerUserId, shortcutId, messageIds, shortcuts));
    }

    /// <summary>
    /// <c>messages.reorderQuickReplies</c>: the listed shortcuts go first in the given order, the rest keep
    /// their relative order after them (tdlib does the same locally).
    /// </summary>
    public void ReorderShortcuts(RequestInfo requestInfo, long ownerUserId, List<int> order)
    {
        var byId = _state.Shortcuts.ToDictionary(p => p.ShortcutId);
        var shortcuts = order.Distinct().Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        shortcuts.AddRange(_state.Shortcuts.Where(p => !shortcuts.Contains(p)));
        var changed = !shortcuts.SequenceEqual(_state.Shortcuts);
        Emit(new QuickRepliesReorderedEvent(requestInfo, ownerUserId, shortcuts, changed));
    }
}
