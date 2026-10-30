using System.Diagnostics.Metrics;

namespace MyTelegram.Messenger.Services.AntiSpam;

/// <summary>
/// Calibration signals for the App:AntiSpam thresholds, published on the "MyTelegram.AntiSpam" meter
/// (readable with dotnet-counters or any System.Diagnostics.Metrics listener). Every increment is also
/// logged by its caller, so the numbers can be recovered from logs when no listener is attached.
/// </summary>
public static class AntiSpamMetrics
{
    public const string MeterName = "MyTelegram.AntiSpam";
    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Triggers = Meter.CreateCounter<long>("antispam.triggers",
        description: "Bursts that reached a repeat threshold (tag mode = strict|loose).");
    private static readonly Counter<long> Enqueued = Meter.CreateCounter<long>("antispam.messages_enqueued",
        description: "Messages queued for deletion, including earlier burst members.");
    private static readonly Counter<long> RecheckCancellations = Meter.CreateCounter<long>("antispam.recheck_cancellations",
        description: "Queued deletions abandoned by the pre-deletion recheck (tag reason).");
    private static readonly Counter<long> Deletions = Meter.CreateCounter<long>("antispam.deletions_confirmed",
        description: "Deletions confirmed by a persisted channel deletion event.");
    private static readonly Counter<long> FalsePositives = Meter.CreateCounter<long>("antispam.false_positive_reports",
        description: "Distinct administrator \"not spam\" reports on antispam deletions.");

    private static Func<int>? _trackedSenders;

    static AntiSpamMetrics() =>
        Meter.CreateObservableGauge("antispam.tracked_senders", () => _trackedSenders?.Invoke() ?? 0,
            description: "In-memory (chat, sender) windows.");

    public static void Track(AntiSpamBurstTracker tracker) => _trackedSenders = () => tracker.TrackedSenders;

    public static void Triggered(string mode) => Triggers.Add(1, new KeyValuePair<string, object?>("mode", mode));
    public static void MessagesEnqueued(int count) => Enqueued.Add(count);
    public static void RecheckCancelled(string reason) =>
        RecheckCancellations.Add(1, new KeyValuePair<string, object?>("reason", reason));
    public static void DeletionConfirmed() => Deletions.Add(1);
    public static void FalsePositiveReported() => FalsePositives.Add(1);
}
