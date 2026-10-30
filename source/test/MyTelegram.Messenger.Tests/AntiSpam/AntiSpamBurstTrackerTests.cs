using MyTelegram.Messenger.Services.AntiSpam;

namespace MyTelegram.Messenger.Tests.AntiSpam;

public class AntiSpamBurstTrackerTests
{
    internal sealed class ManualTime(long unixSeconds) : TimeProvider
    {
        public long Now { get; set; } = unixSeconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Now);
    }

    private const long T0 = 1_800_000_000;
    private static readonly AntiSpamKeys Spam = new(1, 100);
    private static readonly AntiSpamKeys SpamVariant = new(2, 100); // same loose key, different strict key
    private static readonly AntiSpamKeys Other = new(3, 300);

    private static (AntiSpamBurstTracker Tracker, ManualTime Time) Create(AntiSpamOptions? options = null)
    {
        var o = Microsoft.Extensions.Options.Options.Create(options ?? new AntiSpamOptions());
        var time = new ManualTime(T0);
        return (new AntiSpamBurstTracker(new AntiSpamClassifier(o), o, time), time);
    }

    [Fact]
    public void Four_strict_matches_within_the_window_trigger_with_the_whole_burst()
    {
        var (tracker, _) = Create();
        for (var i = 1; i <= 3; i++) tracker.Observe(10, 20, i, T0 + i * 30, Spam).ShouldBeNull();
        var verdict = tracker.Observe(10, 20, 4, T0 + 110, Spam)!;
        verdict.Mode.ShouldBe(AntiSpamClassifier.StrictMode);
        verdict.NewMembers.ShouldBe([1, 2, 3, 4]);
        verdict.NewBurst.ShouldBeTrue();
    }

    [Fact]
    public void Loose_matches_need_five()
    {
        var (tracker, _) = Create();
        tracker.Observe(10, 20, 1, T0, Spam).ShouldBeNull();
        for (var i = 2; i <= 4; i++) tracker.Observe(10, 20, i, T0 + i, SpamVariant with { Strict = (UInt128)(10 + i) }).ShouldBeNull();
        var verdict = tracker.Observe(10, 20, 5, T0 + 5, SpamVariant)!;
        verdict.Mode.ShouldBe(AntiSpamClassifier.LooseMode);
        verdict.NewMembers.Count.ShouldBe(5);
    }

    [Fact]
    public void Window_slides_per_message_without_fixed_buckets()
    {
        var (tracker, _) = Create();
        // Four messages 130 s apart never share a 120 s window.
        for (var i = 0; i < 4; i++) tracker.Observe(10, 20, i + 1, T0 + i * 130, Spam).ShouldBeNull();
        // Across a minute boundary that fixed buckets would split: 59 s .. 61 s .. 119 s.
        var (sliding, _) = Create();
        sliding.Observe(10, 20, 1, T0 + 59, Spam);
        sliding.Observe(10, 20, 2, T0 + 61, Spam);
        sliding.Observe(10, 20, 3, T0 + 100, Spam);
        sliding.Observe(10, 20, 4, T0 + 179, Spam).ShouldNotBeNull();
        sliding.Observe(10, 20, 5, T0 + 300, Spam).ShouldBeNull();
    }

    [Fact]
    public void Keys_are_per_chat_and_sender_and_texts_do_not_mix()
    {
        var (tracker, _) = Create();
        tracker.Observe(10, 20, 1, T0, Spam);
        tracker.Observe(10, 20, 2, T0, Other);
        tracker.Observe(10, 21, 3, T0, Spam);
        tracker.Observe(11, 20, 4, T0, Spam);
        tracker.Observe(10, 20, 5, T0, Spam).ShouldBeNull();
        tracker.Observe(10, 20, 6, T0, Spam).ShouldBeNull();
        tracker.Observe(10, 20, 7, T0, Spam)!.NewMembers.ShouldBe([1, 5, 6, 7]);
    }

    [Fact]
    public void Ring_keeps_only_the_last_sixteen_entries_and_replays_do_not_double_count()
    {
        var (tracker, _) = Create();
        tracker.Observe(10, 20, 1, T0, Spam);
        tracker.Observe(10, 20, 2, T0, Spam);
        for (var i = 0; i < AntiSpamBurstTracker.RingSize; i++) tracker.Observe(10, 20, 100 + i, T0, Other);
        tracker.Observe(10, 20, 3, T0, Spam).ShouldBeNull(); // 1 and 2 were pushed out
        for (var i = 0; i < 5; i++) tracker.Observe(10, 20, 3, T0, Spam).ShouldBeNull(); // same message ID
    }

    [Fact]
    public void Later_triggers_of_the_same_burst_reuse_its_identity_and_skip_enqueued_members()
    {
        var (tracker, _) = Create();
        for (var i = 1; i <= 3; i++) tracker.Observe(10, 20, i, T0, Spam);
        var first = tracker.Observe(10, 20, 4, T0, Spam)!;
        tracker.MarkEnqueued(10, 20, first.NewMembers);
        var second = tracker.Observe(10, 20, 5, T0 + 1, Spam)!;
        second.BurstId.ShouldBe(first.BurstId);
        second.NewBurst.ShouldBeFalse();
        second.NewMembers.ShouldBe([5]);
    }

    [Fact]
    public void Edit_that_still_matches_keeps_a_single_entry()
    {
        var (tracker, _) = Create();
        for (var i = 1; i <= 3; i++) tracker.Observe(10, 20, i, T0, Spam);
        for (var i = 0; i < 3; i++) tracker.Edit(10, 20, 3, T0, T0 + 10, Spam).ShouldBeNull();
        tracker.Observe(10, 20, 4, T0 + 11, Spam).ShouldNotBeNull();
    }

    [Fact]
    public void Edit_away_from_the_text_removes_the_entry()
    {
        var (tracker, _) = Create();
        for (var i = 1; i <= 3; i++) tracker.Observe(10, 20, i, T0, Spam);
        tracker.Edit(10, 20, 3, T0, T0 + 10, Other).ShouldBeNull();
        tracker.Observe(10, 20, 4, T0 + 11, Spam).ShouldBeNull();
        tracker.Edit(10, 20, 4, T0 + 11, T0 + 12, null).ShouldBeNull(); // no longer under scrutiny
        tracker.Observe(10, 20, 5, T0 + 13, Spam).ShouldBeNull();
        tracker.Observe(10, 20, 6, T0 + 14, Spam).ShouldNotBeNull();
    }

    [Fact]
    public void Edit_onto_the_common_text_triggers_but_not_after_the_window()
    {
        var (tracker, _) = Create();
        for (var i = 1; i <= 3; i++) tracker.Observe(10, 20, i, T0, Spam);
        tracker.Observe(10, 20, 4, T0 + 5, Other).ShouldBeNull();
        tracker.Edit(10, 20, 4, T0 + 5, T0 + 20, Spam)!.NewMembers.ShouldBe([1, 2, 3, 4]);

        var (late, _) = Create();
        for (var i = 1; i <= 3; i++) late.Observe(10, 20, i, T0, Spam);
        late.Observe(10, 20, 4, T0 + 5, Other);
        late.Edit(10, 20, 4, T0 + 5, T0 + 200, Spam).ShouldBeNull();
    }

    [Fact]
    public void Idle_windows_are_swept_and_memory_is_capped()
    {
        var (tracker, time) = Create(new AntiSpamOptions { MaxTrackedSenders = 1_000 });
        for (var i = 0; i < 500; i++) tracker.Observe(10, i, 1, T0, Spam);
        tracker.TrackedSenders.ShouldBe(500);
        time.Now += 121;
        tracker.Sweep();
        tracker.TrackedSenders.ShouldBe(0);

        // All active at once, so nothing is idle: the cap evicts the least recently seen windows.
        for (var i = 0; i < 1_500; i++) tracker.Observe(10, i, 1, time.Now, Spam);
        tracker.TrackedSenders.ShouldBeLessThanOrEqualTo(1_000);
        // The sender seen last keeps its window.
        for (var i = 1; i <= 3; i++) tracker.Observe(10, 1_499, 1 + i, time.Now, Spam);
        tracker.Observe(10, 1_499, 10, time.Now, Spam).ShouldNotBeNull();
    }
}
