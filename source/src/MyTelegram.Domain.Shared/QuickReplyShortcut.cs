namespace MyTelegram;

/// <summary>
/// One <a href="https://corefork.telegram.org/api/business#quick-reply-shortcuts">quick reply shortcut</a>
/// of a user. The messages of a shortcut are ordinary message aggregates flagged with
/// <see cref="QuickReplyItem"/>; only the name, the id and the user's ordering live here.
/// </summary>
public record QuickReplyShortcut(int ShortcutId, string Name);
