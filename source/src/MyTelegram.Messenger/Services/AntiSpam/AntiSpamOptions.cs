namespace MyTelegram.Messenger.Services.AntiSpam;

/// <summary>Local repeated-link detection only; reports never feed this classifier.</summary>
public sealed class AntiSpamOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Messages with the same strict key (case and punctuation significant).</summary>
    public int RepeatThreshold { get; set; } = 4;
    /// <summary>Messages with the same loose key (case, punctuation, emoji and spacing ignored).</summary>
    public int LooseRepeatThreshold { get; set; } = 5;
    public int WindowSeconds { get; set; } = 120;
    public int MinimumTextLength { get; set; } = 40;
    public int MaxTextLength { get; set; } = 4096;
    /// <summary>Delete every message of the burst within the window, not only the triggering one.</summary>
    public bool DeleteBurst { get; set; } = true;
    /// <summary>Upper bound of in-memory (chat, sender) windows; the stalest are evicted beyond it.</summary>
    public int MaxTrackedSenders { get; set; } = 50_000;
    public int MaxAttempts { get; set; } = 5;
    public int LeaseSeconds { get; set; } = 120;
    public int PollSeconds { get; set; } = 2;
    public int DelaySeconds { get; set; } = 5;
}
