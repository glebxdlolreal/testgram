using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace MyTelegram.Messenger.Services.AntiSpam;

/// <summary>
/// Two keys per message. <see cref="Strict"/> ignores spacing, invisible characters and emoji but keeps
/// case and punctuation. <see cref="Loose"/> keeps only case-folded letters and digits (with common
/// Cyrillic/Greek look-alikes folded to Latin), so it is absent for texts with too little of them.
/// </summary>
public readonly record struct AntiSpamKeys(UInt128 Strict, UInt128? Loose)
{
    public string StrictHex => AntiSpamClassifier.Hex(Strict);
    public string? LooseHex => Loose is { } loose ? AntiSpamClassifier.Hex(loose) : null;
    public string? Hex(string mode) => mode == AntiSpamClassifier.LooseMode ? LooseHex : StrictHex;
}

public sealed partial class AntiSpamClassifier(IOptions<AntiSpamOptions> options, IAppConfigHelper? appConfig = null)
{
    public const string StrictMode = "strict";
    public const string LooseMode = "loose";
    private const int MinimumLooseLength = 20;

    public bool Enabled => options.Value.Enabled;
    public int Threshold => Math.Clamp(options.Value.RepeatThreshold, 4, AntiSpamBurstTracker.RingSize);
    public int LooseThreshold => Math.Clamp(options.Value.LooseRepeatThreshold, Threshold, AntiSpamBurstTracker.RingSize);
    public int WindowSeconds => Math.Clamp(options.Value.WindowSeconds, 10, 300);
    /// <summary>
    /// As in Telegram, the filter acts in every group that may enable it: the advertised
    /// telegram_antispam_group_size_min (the same gate channels.toggleAntiSpam applies).
    /// </summary>
    public int MinimumParticipants => appConfig?.GetInt32Value("telegram_antispam_group_size_min", DefaultGroupSizeMin) ?? DefaultGroupSizeMin;
    public const int DefaultGroupSizeMin = 200;
    private int MinimumTextLength => Math.Clamp(options.Value.MinimumTextLength, 40, 1024);
    private int MaxTextLength => Math.Clamp(options.Value.MaxTextLength, 128, 16384);

    /// <summary>Keys of a message under scrutiny, or null when it is out of scope (no link, too short...).</summary>
    public AntiSpamKeys? Analyze(string? text, IEnumerable<IMessageEntity>? entities)
    {
        if (!options.Value.Enabled || string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength) return null;
        var strict = NormalizeStrict(text);
        if (strict.Length < MinimumTextLength || !HasLink(strict, entities)) return null;
        var loose = NormalizeLoose(strict);
        return new AntiSpamKeys(Hash(strict), loose.Length >= MinimumLooseLength ? Hash(loose) : null);
    }

    /// <summary>NFKC; invisible characters and emoji removed; whitespace runs collapsed to one space; trimmed.</summary>
    public static string NormalizeStrict(string text)
    {
        // With InvariantGlobalization (this solution's default) Normalize is a no-op for non-ASCII text, so
        // the compatibility forms spammers actually use are also folded explicitly below.
        var source = text.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(source.Length);
        var pendingSpace = false;
        foreach (var original in source.EnumerateRunes())
        {
            var rune = FoldCompatibility(original);
            if (IsInvisible(rune) || IsEmoji(rune)) continue;
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = builder.Length != 0;
                continue;
            }
            if (pendingSpace) builder.Append(' ');
            pendingSpace = false;
            AppendRune(builder, rune);
        }
        return builder.ToString();
    }

    /// <summary>Only letters, spacing marks and digits, lower-cased and with look-alikes folded.</summary>
    public static string NormalizeLoose(string strict)
    {
        var builder = new StringBuilder(strict.Length);
        foreach (var rune in strict.EnumerateRunes())
        {
            switch (Rune.GetUnicodeCategory(rune))
            {
                case UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
                    or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.SpacingCombiningMark
                    or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber:
                    var lower = Rune.ToLowerInvariant(rune);
                    if (lower.Value < 0x10000 && LookAlikes.TryGetValue((char)lower.Value, out var latin)) builder.Append(latin);
                    else AppendRune(builder, lower);
                    break;
            }
        }
        return builder.ToString();
    }

    /// <summary>
    /// A link in any form puts the message under scrutiny: URL/hidden-URL/mention entities, a scheme
    /// (including spaced or defanged ones), t.me links, @usernames and bare or obfuscated domains.
    /// </summary>
    public static bool HasLink(string normalized, IEnumerable<IMessageEntity>? entities)
    {
        if (entities != null && entities.Any(e => e is TMessageEntityUrl or TMessageEntityTextUrl or TMessageEntityMention))
            return true;
        try
        {
            return SchemeRegex().IsMatch(normalized) || TelegramLinkRegex().IsMatch(normalized) ||
                   UsernameRegex().IsMatch(normalized) || DomainRegex().IsMatch(normalized) ||
                   SpacedDomainRegex().IsMatch(normalized);
        }
        catch (RegexMatchTimeoutException)
        {
            // A text crafted to exhaust the matcher is itself suspicious; the repeat rule still applies.
            return true;
        }
    }

    public static string Hex(UInt128 key) => key.ToString("x32", CultureInfo.InvariantCulture);

    private static UInt128 Hash(string value) =>
        BinaryPrimitives.ReadUInt128LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void AppendRune(StringBuilder builder, Rune rune)
    {
        Span<char> buffer = stackalloc char[2];
        builder.Append(buffer[..rune.EncodeToUtf16(buffer)]);
    }

    /// <summary>NFKC equivalents for styled Latin letters and digits, valid even without ICU.</summary>
    public static Rune FoldCompatibility(Rune rune)
    {
        var v = rune.Value;
        return v switch
        {
            >= 0xFF01 and <= 0xFF5E => new Rune(v - 0xFF01 + 0x21), // fullwidth ASCII
            0x3000 => new Rune(' '),
            0x2024 or 0xFF61 or 0x3002 => new Rune('.'),
            >= 0x1D400 and <= 0x1D6A3 => Letter((v - 0x1D400) % 52), // mathematical bold/italic/script/... letters
            >= 0x1D7CE and <= 0x1D7FF => new Rune('0' + (v - 0x1D7CE) % 10), // mathematical digits
            >= 0x24B6 and <= 0x24CF => new Rune('A' + v - 0x24B6), // circled capitals
            >= 0x24D0 and <= 0x24E9 => new Rune('a' + v - 0x24D0), // circled small letters
            >= 0x249C and <= 0x24B5 => new Rune('a' + v - 0x249C), // parenthesized small letters
            >= 0x1F130 and <= 0x1F149 => new Rune('A' + v - 0x1F130), // squared capitals
            >= 0x1F150 and <= 0x1F169 => new Rune('A' + v - 0x1F150), // negative circled capitals
            >= 0x1F170 and <= 0x1F189 => new Rune('A' + v - 0x1F170), // negative squared capitals
            >= 0x2460 and <= 0x2468 => new Rune('1' + v - 0x2460), // circled digits 1-9
            _ => rune
        };

        static Rune Letter(int index) => new(index < 26 ? 'A' + index : 'a' + index - 26);
    }

    private static bool IsInvisible(Rune rune) => rune.Value is
        >= 0x200B and <= 0x200D or 0xFEFF or // zero-width space/non-joiner/joiner, BOM
        >= 0xFE00 and <= 0xFE0F or >= 0xE0100 and <= 0xE01EF or // variation selectors
        0x00AD or 0x034F or 0x061C or 0x180E or >= 0x2060 and <= 0x2064 or 0x200E or 0x200F or
        >= 0x202A and <= 0x202E or >= 0x2066 and <= 0x2069 or >= 0xE0000 and <= 0xE007F;

    private static bool IsEmoji(Rune rune) => rune.Value is
        >= 0x1F000 and <= 0x1FAFF or // emoticons, pictographs, flags, skin tones, symbols
        >= 0x2600 and <= 0x27BF or // miscellaneous symbols, dingbats
        >= 0x2B00 and <= 0x2BFF or 0x20E3 or 0x3030 or 0x303D or 0x3297 or 0x3299 or
        0x203C or 0x2049 or 0x2122 or 0x2139 or >= 0x2194 and <= 0x21AA or >= 0x231A and <= 0x23FF or
        >= 0x25AA and <= 0x25FE;

    private static readonly Dictionary<char, char> LookAlikes = new()
    {
        ['а'] = 'a', ['в'] = 'b', ['е'] = 'e', ['ё'] = 'e', ['к'] = 'k', ['м'] = 'm', ['н'] = 'h', ['о'] = 'o',
        ['р'] = 'p', ['с'] = 'c', ['т'] = 't', ['у'] = 'y', ['х'] = 'x', ['ѕ'] = 's', ['і'] = 'i', ['ї'] = 'i',
        ['ј'] = 'j', ['ԁ'] = 'd', ['ԛ'] = 'q', ['ԝ'] = 'w', ['ɡ'] = 'g', ['α'] = 'a', ['β'] = 'b', ['ε'] = 'e',
        ['ι'] = 'i', ['κ'] = 'k', ['ν'] = 'v', ['ο'] = 'o', ['ρ'] = 'p', ['τ'] = 't', ['υ'] = 'u', ['χ'] = 'x',
    };

    // TLDs common in link spam. Extensions that are mostly file names in chat (md, sh, py, rs, zip, mov)
    // are deliberately absent: a gate that fires on "README.md" would only add false positives.
    private const string Tlds =
        "com|net|org|info|biz|xyz|top|site|online|store|shop|club|live|pro|app|dev|link|click|space|fun|icu|vip|" +
        "win|bet|casino|money|cash|loan|cloud|tech|digital|website|life|today|news|blog|email|page|one|lol|" +
        "ru|рф|su|ua|by|kz|uz|kg|tj|am|az|ge|io|me|co|cc|tv|gg|ly|to|ws|us|uk|de|fr|it|es|nl|pl|eu|in|br|cn|jp|" +
        "kr|tr|ir|vn|id|ph|th|my|sg|hk|tw|ca|au|nz|mx|ar|cl|za|ng|ke|lt|lv|ee|fi|se|no|dk|cz|sk|hu|ro|bg|rs|gr|" +
        "pt|ch|at|be|ie|il|ae|sa|eg|ai|gl|la|ms|nu|st|tk|ml|ga|cf|gq";

    // With spaces around the dot only distinctive TLDs count: "Hello . It works" is prose, "site . com" is not.
    private const string SpacedTlds =
        "com|net|org|info|biz|xyz|top|site|online|store|shop|club|live|app|link|click|space|icu|vip|bet|casino|" +
        "ru|рф|su|ua|kz|io|cc|tv|gg|ly|ws|uk|cn";

    private const string Separator = @"(?:\.|。|\[\s*(?:\.|dot)\s*\]|\(\s*(?:\.|dot)\s*\)|\{\s*(?:\.|dot)\s*\})";
    private const string Label = @"[\p{L}\p{N}][\p{L}\p{N}-]{0,62}";
    private const int TimeoutMs = 100;

    [GeneratedRegex(@"\b(?:h[tx]{2}ps?|tg|ftp)\s*(?::|\[:\])\s*/\s*/|\bwww\s*" + Separator + @"\s*[\p{L}\p{N}]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex SchemeRegex();

    [GeneratedRegex(@"(?<![\p{L}\p{N}_])(?:t|telegram)\s*" + Separator + @"\s*me\s*/\s*[\p{L}\p{N}_+]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex TelegramLinkRegex();

    [GeneratedRegex(@"(?<![\p{L}\p{N}_@])@[A-Za-z][A-Za-z0-9_]{4,31}(?![A-Za-z0-9_])",
        RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex UsernameRegex();

    [GeneratedRegex(@"(?<![\p{L}\p{N}_-])" + Label + Separator + @"(?:" + Tlds + @")(?![\p{L}\p{N}_-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex DomainRegex();

    [GeneratedRegex(@"(?<![\p{L}\p{N}_-])" + Label + @"\s+(?:\.|\[\s*(?:\.|dot)\s*\]|\(\s*(?:\.|dot)\s*\))\s+(?:" + SpacedTlds + @")(?![\p{L}\p{N}_-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex SpacedDomainRegex();
}
