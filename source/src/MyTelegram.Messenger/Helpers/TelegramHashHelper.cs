namespace MyTelegram.Messenger.Helpers;

/// <summary>
/// The hash algorithms Telegram clients use to detect that a cached list is still up to date.
/// Both must be reproducible across processes, so <see cref="string.GetHashCode()"/> (randomized
/// per process in .NET) must never be used for them.
/// See https://corefork.telegram.org/api/offsets#hash-generation
/// </summary>
public static class TelegramHashHelper
{
    /// <summary>
    /// 64-bit vector hash, as used by messages.getSavedReactionTags and friends.
    /// </summary>
    public static long GetVectorHash(IEnumerable<long> numbers)
    {
        var acc = 0UL;
        foreach (var number in numbers)
        {
            acc ^= acc >> 21;
            acc ^= acc << 35;
            acc ^= acc >> 4;
            acc += (ulong)number;
        }

        return (long)acc;
    }

    /// <summary>
    /// 32-bit hash, as used by messages.getAvailableReactions.
    /// </summary>
    public static int GetInt32Hash(IEnumerable<long> numbers)
    {
        var hash = 0L;
        foreach (var number in numbers)
        {
            hash = hash * 20261 + 0x80000000L + number;
            hash %= 0x80000000L;
        }

        return (int)(hash % 0x80000000L);
    }

    /// <summary>
    /// Stable per-string number for hashing, replacing the process-randomized string.GetHashCode().
    /// </summary>
    public static long GetStringNumber(string value)
    {
        var hash = 0L;
        foreach (var c in value)
        {
            hash = (hash * 31 + c) % 0x7FFFFFFFL;
        }

        return hash;
    }
}
