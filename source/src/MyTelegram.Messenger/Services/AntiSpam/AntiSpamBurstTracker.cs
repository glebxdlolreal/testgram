using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace MyTelegram.Messenger.Services.AntiSpam;

/// <summary>Outcome of one observation. <see cref="NewMembers"/> are matching messages not yet enqueued.</summary>
public sealed record AntiSpamVerdict(string Mode, string Key, string BurstId, bool NewBurst, IReadOnlyList<int> NewMembers);

/// <summary>
/// Sliding window per (chat, sender): a ring of the last <see cref="RingSize"/> link-bearing messages with
/// their dates. Every observation first drops entries older than the window, so there are no fixed buckets.
/// Idle keys are swept and the number of tracked senders is capped. State is process-local: a restart or a
/// second command-server replica starts from an empty window, which can only miss a burst, never invent one.
/// </summary>
public sealed class AntiSpamBurstTracker(AntiSpamClassifier classifier, IOptions<AntiSpamOptions> options,
    TimeProvider? timeProvider = null)
{
    public const int RingSize = 16;
    private const int SweepIntervalSeconds = 30;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<(long Chat, long Sender), Ring> _rings = new();
    private long _nextSweep;

    public int TrackedSenders => _rings.Count;
    private int MaxTrackedSenders => Math.Clamp(options.Value.MaxTrackedSenders, 1_000, 1_000_000);

    /// <summary>Records a new message (a replayed event for the same ID replaces its entry).</summary>
    public AntiSpamVerdict? Observe(long chat, long sender, int messageId, long date, AntiSpamKeys keys) =>
        Update(chat, sender, date, ring =>
        {
            ring.Remove(messageId);
            ring.Add(new Entry(messageId, date, keys));
            return ring.Evaluate(messageId, keys, date, classifier);
        });

    /// <summary>
    /// Applies an edit. A text that still has the same keys keeps its single entry; a changed text replaces
    /// it; a text that left scrutiny (null keys) is removed. The entry keeps the original send date.
    /// </summary>
    public AntiSpamVerdict? Edit(long chat, long sender, int messageId, long date, long editDate, AntiSpamKeys? keys)
    {
        if (keys == null)
        {
            if (_rings.TryGetValue((chat, sender), out var existing))
                lock (existing) existing.Remove(messageId);
            return null;
        }
        return Update(chat, sender, editDate, ring =>
        {
            var index = ring.IndexOf(messageId);
            if (index < 0 || ring.At(index).Keys != keys.Value)
            {
                ring.Remove(messageId);
                if (date < editDate - classifier.WindowSeconds) return null;
                ring.Add(new Entry(messageId, date, keys.Value));
            }
            return ring.Evaluate(messageId, keys.Value, editDate, classifier);
        });
    }

    /// <summary>Marks messages as enqueued so later triggers of the same burst do not reload them.</summary>
    public void MarkEnqueued(long chat, long sender, IEnumerable<int> messageIds)
    {
        if (!_rings.TryGetValue((chat, sender), out var ring)) return;
        lock (ring)
            foreach (var id in messageIds)
                ring.MarkEnqueued(id);
    }

    private AntiSpamVerdict? Update(long chat, long sender, long now, Func<Ring, AntiSpamVerdict?> action)
    {
        SweepIfDue();
        while (true)
        {
            var ring = _rings.GetOrAdd((chat, sender), _ => new Ring());
            lock (ring)
            {
                // A sweep may have detached this ring between lookup and lock; retry with a live one.
                if (ring.Detached) continue;
                ring.LastSeen = _time.GetUtcNow().ToUnixTimeSeconds();
                ring.Expire(now - classifier.WindowSeconds);
                return action(ring);
            }
        }
    }

    /// <summary>Removes idle windows; if the cap is still exceeded, evicts the least recently used ones.</summary>
    public void Sweep()
    {
        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        var idleBefore = now - classifier.WindowSeconds;
        foreach (var (key, ring) in _rings)
            if (ring.LastSeen < idleBefore) TryDetach(key, ring, idleBefore);
        var excess = _rings.Count - MaxTrackedSenders * 9 / 10;
        if (_rings.Count <= MaxTrackedSenders || excess <= 0) return;
        foreach (var (key, ring) in _rings.OrderBy(x => x.Value.LastSeen).Take(excess).ToList())
            TryDetach(key, ring, long.MaxValue);
    }

    private void SweepIfDue()
    {
        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        var due = Interlocked.Read(ref _nextSweep);
        if ((now < due && _rings.Count < MaxTrackedSenders) ||
            Interlocked.CompareExchange(ref _nextSweep, now + SweepIntervalSeconds, due) != due) return;
        Sweep();
    }

    private void TryDetach((long, long) key, Ring ring, long idleBefore)
    {
        lock (ring)
        {
            if (ring.LastSeen >= idleBefore || ring.Detached) return;
            ring.Detached = true;
            _rings.TryRemove(new KeyValuePair<(long Chat, long Sender), Ring>(key, ring));
        }
    }

    private record struct Entry(int MessageId, long Date, AntiSpamKeys Keys, bool Enqueued = false);

    private sealed class Ring
    {
        private readonly Entry[] _items = new Entry[RingSize];
        private int _head; // index of the oldest entry
        private int _count;
        private string? _burstId;
        private string? _burstKey;
        private long _burstUntil;
        public long LastSeen;
        public bool Detached;

        public Entry At(int logical) => _items[(_head + logical) % RingSize];

        public int IndexOf(int messageId)
        {
            for (var i = 0; i < _count; i++)
                if (At(i).MessageId == messageId) return i;
            return -1;
        }

        public void Add(Entry entry)
        {
            if (_count == RingSize)
            {
                _head = (_head + 1) % RingSize;
                _count--;
            }
            _items[(_head + _count) % RingSize] = entry;
            _count++;
        }

        public void Remove(int messageId)
        {
            var index = IndexOf(messageId);
            if (index < 0) return;
            for (var i = index; i < _count - 1; i++)
                _items[(_head + i) % RingSize] = _items[(_head + i + 1) % RingSize];
            _count--;
        }

        public void MarkEnqueued(int messageId)
        {
            var index = IndexOf(messageId);
            if (index >= 0) _items[(_head + index) % RingSize] = At(index) with { Enqueued = true };
        }

        /// <summary>Drops every entry older than the cutoff, wherever it sits (edits reorder entries).</summary>
        public void Expire(long cutoff)
        {
            var kept = 0;
            for (var i = 0; i < _count; i++)
            {
                var entry = At(i);
                if (entry.Date >= cutoff) _items[(_head + kept++) % RingSize] = entry;
            }
            _count = kept;
        }

        public AntiSpamVerdict? Evaluate(int messageId, AntiSpamKeys keys, long now, AntiSpamClassifier classifier)
        {
            int strict = 0, loose = 0;
            for (var i = 0; i < _count; i++)
            {
                var entry = At(i).Keys;
                if (entry.Strict == keys.Strict) strict++;
                if (keys.Loose.HasValue && entry.Loose == keys.Loose) loose++;
            }
            string mode;
            if (strict >= classifier.Threshold) mode = AntiSpamClassifier.StrictMode;
            else if (loose >= classifier.LooseThreshold) mode = AntiSpamClassifier.LooseMode;
            else return null;

            var key = keys.Hex(mode)!;
            var fresh = _burstKey != key || _burstUntil < now;
            if (fresh) _burstId = Guid.NewGuid().ToString("N");
            _burstKey = key;
            _burstUntil = now + classifier.WindowSeconds;
            var members = new List<int>();
            for (var i = 0; i < _count; i++)
            {
                var entry = At(i);
                var matches = mode == AntiSpamClassifier.StrictMode
                    ? entry.Keys.Strict == keys.Strict
                    : entry.Keys.Loose == keys.Loose;
                if (matches && (!entry.Enqueued || entry.MessageId == messageId)) members.Add(entry.MessageId);
            }
            return new AntiSpamVerdict(mode, key, _burstId!, fresh, members);
        }
    }
}
