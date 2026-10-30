namespace MyTelegram.Messenger.Services.AntiSpam;

/// <summary>Local repeated-link detection only; reports never feed this classifier.</summary>
public sealed class AntiSpamOptions
{
    public bool Enabled { get; set; } = true;
    public int RepeatThreshold { get; set; } = 4;
    public int WindowSeconds { get; set; } = 120;
    public int MinimumTextLength { get; set; } = 40;
    public int MaxTextLength { get; set; } = 4096;
    public int MaxRecentMessages { get; set; } = 64;
    public int MaxAttempts { get; set; } = 5;
    public int LeaseSeconds { get; set; } = 120;
    public int PollSeconds { get; set; } = 2;
    public int DelaySeconds { get; set; } = 5;
}
