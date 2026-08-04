namespace MyTelegram;

/// <summary>
/// A single poll answer. <paramref name="AddedByPeerId"/> and <paramref name="Date"/> are only
/// set for answers contributed by members of an open poll (<c>open_answers</c>); answers created
/// together with the poll leave them null.
/// </summary>
public record PollAnswer(string Text,
    string Option, byte[]? Entities, long? AddedByPeerId = null, int? Date = null);
