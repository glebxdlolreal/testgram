using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace MyTelegram.Messenger.Services.AntiSpam;

public sealed class AntiSpamClassifier(IOptions<AntiSpamOptions> options)
{
    public int Threshold => Math.Clamp(options.Value.RepeatThreshold, 4, 32);
    public int WindowSeconds => Math.Clamp(options.Value.WindowSeconds, 10, 300);

    public string? Fingerprint(string? text)
    {
        if (!options.Value.Enabled || string.IsNullOrWhiteSpace(text) ||
            text.Length > Math.Clamp(options.Value.MaxTextLength, 128, 16384)) return null;
        // Preserve case and punctuation: URLs can be case sensitive. Only collapse whitespace.
        var normalized = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length < Math.Clamp(options.Value.MinimumTextLength, 40, 1024)) return null;
        var hasLink = normalized.Split(' ').Any(word =>
            Uri.TryCreate(word, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
            !string.IsNullOrEmpty(uri.Host));
        return hasLink ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))) : null;
    }

    public bool IsSpam(string fingerprint, IEnumerable<(int MessageId, long Date, string Fingerprint)> messages,
        long currentDate) => messages.Take(Math.Clamp(options.Value.MaxRecentMessages, Threshold, 256))
        .Where(x => x.Date >= currentDate - WindowSeconds && x.Date <= currentDate && x.Fingerprint == fingerprint)
        .Select(x => x.MessageId).Distinct().Take(Threshold).Count() >= Threshold;
}
