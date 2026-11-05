using System.Text;

namespace MyTelegram.Messenger.Services.Bots;

/// <summary>
/// A bot message being put together with its entities. Bold, links and code are marked explicitly;
/// URLs, @mentions and /commands are found in the text afterwards, because clients do not detect them
/// in incoming messages and real bots send them as entities. Offsets are UTF-16, as C# strings are.
/// </summary>
internal sealed partial class BotText
{
    private readonly StringBuilder _text = new();
    private readonly List<IMessageEntity> _entities = [];

    public BotText(string text = "")
    {
        _text.Append(text);
    }

    public string Text => _text.ToString();

    public BotText Append(string text)
    {
        _text.Append(text);
        return this;
    }

    public BotText Bold(string text) => Mark(text, (offset, length) => new TMessageEntityBold { Offset = offset, Length = length });

    public BotText Code(string text) => Mark(text, (offset, length) => new TMessageEntityCode { Offset = offset, Length = length });

    public BotText Link(string text, string url) =>
        Mark(text, (offset, length) => new TMessageEntityTextUrl { Offset = offset, Length = length, Url = url });

    /// <summary>The explicit entities plus every detected one that does not overlap them, in text order.</summary>
    public TVector<IMessageEntity>? Entities()
    {
        var text = Text;
        var all = new List<IMessageEntity>(_entities);

        void AddDetected(Regex regex, Func<int, int, IMessageEntity> create)
        {
            foreach (Match match in regex.Matches(text))
            {
                var length = match.Value.TrimEnd('.', ',', ')', '!', '?', ':', ';').Length;
                if (length > 0 && !all.Any(e => match.Index < e.Offset + e.Length && e.Offset < match.Index + length))
                {
                    all.Add(create(match.Index, length));
                }
            }
        }

        AddDetected(UrlRegex(), (offset, length) => new TMessageEntityUrl { Offset = offset, Length = length });
        AddDetected(MentionRegex(), (offset, length) => new TMessageEntityMention { Offset = offset, Length = length });
        AddDetected(CommandRegex(), (offset, length) => new TMessageEntityBotCommand { Offset = offset, Length = length });

        all.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        return all.Count == 0 ? null : new TVector<IMessageEntity>(all);
    }

    private BotText Mark(string text, Func<int, int, IMessageEntity> create)
    {
        if (text.Length > 0)
        {
            _entities.Add(create(_text.Length, text.Length));
        }

        _text.Append(text);
        return this;
    }

    [GeneratedRegex(@"(?<![\w.])(?:https?://|t\.me/)\S+")]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"(?<![\w@])@[a-zA-Z][a-zA-Z0-9_]{2,31}\b")]
    private static partial Regex MentionRegex();

    [GeneratedRegex(@"(?<![\w/:.@])/[a-zA-Z0-9_]{1,32}\b")]
    private static partial Regex CommandRegex();
}
