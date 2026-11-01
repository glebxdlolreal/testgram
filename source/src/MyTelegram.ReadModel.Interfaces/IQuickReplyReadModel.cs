namespace MyTelegram.ReadModel.Interfaces;

/// <summary>
/// The <a href="https://corefork.telegram.org/api/business#quick-reply-shortcuts">quick reply shortcuts</a>
/// of one user, in the user's order. The messages of each shortcut are message read models flagged with
/// <see cref="IMessageReadModel.IsQuickReplyMessage"/>.
/// </summary>
public interface IQuickReplyListReadModel : IReadModel
{
    long OwnerUserId { get; }
    IReadOnlyList<QuickReplyShortcut> Shortcuts { get; }
}
