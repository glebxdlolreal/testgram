using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using MyTelegram.Messenger.Options;

namespace MyTelegram.Messenger.Services.Premium;

public enum FileTransferDirection
{
    Upload,
    Download
}

/// <summary>
/// The speed cap Premium lifts: an account without Premium that transfers faster than
/// <see cref="PremiumSpeedLimitOptions"/> allows is told to wait with <c>420 FLOOD_PREMIUM_WAIT_X</c>.
/// Clients retry the part by themselves after X seconds (tdlib <c>NetQueryDelayer</c>, Android
/// <c>ConnectionsManager::processResponse</c>) and show the "speed limited" Premium offer.
/// See https://corefork.telegram.org/api/files
/// </summary>
public interface IPremiumTransferSpeedLimiter
{
    /// <summary>
    /// Accounts the transfer of <paramref name="bytes"/> against the user's allowance: 0 when it may
    /// go ahead (and is charged), otherwise the seconds to wait (and nothing is charged).
    /// </summary>
    Task<int> AcquireAsync(long userId, FileTransferDirection direction, long bytes);
}

/// <inheritdoc />
public sealed class PremiumTransferSpeedLimiter(
    IUserAppService userAppService,
    IOptionsMonitor<PremiumOptions> options,
    TimeProvider? timeProvider = null) : IPremiumTransferSpeedLimiter, ISingletonDependency
{
    private const int SweepInterval = 4096;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<(long UserId, FileTransferDirection Direction), Allowance> _allowances = new();
    private int _calls;

    public async Task<int> AcquireAsync(long userId, FileTransferDirection direction, long bytes)
    {
        var limit = options.CurrentValue.SpeedLimit;
        var rate = direction == FileTransferDirection.Upload ? limit.UploadBytesPerSecond : limit.DownloadBytesPerSecond;
        if (!limit.Enabled || rate <= 0 || bytes <= 0 || userId == 0 || PeerKindHelper.IsSystemUserId(userId))
        {
            return 0;
        }

        var user = await userAppService.GetAsync(userId);
        if (user == null || user.Premium || user.Bot)
        {
            return 0;
        }

        var now = _time.GetUtcNow().ToUnixTimeMilliseconds() / 1000d;
        if (Interlocked.Increment(ref _calls) % SweepInterval == 0)
        {
            Sweep(now);
        }

        return _allowances.GetOrAdd((userId, direction), _ => new Allowance())
            .Acquire(now, bytes / (double)rate, Math.Max(limit.BurstBytes, bytes) / (double)rate);
    }

    /// <summary>Forgets accounts whose allowance has refilled completely: they are as good as new.</summary>
    private void Sweep(double now)
    {
        foreach (var (key, allowance) in _allowances)
        {
            if (allowance.IsIdle(now))
            {
                _allowances.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    /// A generic cell rate algorithm: <see cref="_busyUntil"/> is when the account would have finished
    /// everything it was allowed so far had it gone at exactly the capped rate. A transfer is allowed
    /// while that point stays within the burst allowance of now.
    /// </summary>
    private sealed class Allowance
    {
        private double _busyUntil;

        public int Acquire(double now, double cost, double burst)
        {
            lock (this)
            {
                var busyUntil = Math.Max(_busyUntil, now) + cost;
                var overshoot = busyUntil - now - burst;
                if (overshoot > 0)
                {
                    return Math.Max(1, (int)Math.Ceiling(overshoot));
                }

                _busyUntil = busyUntil;
                return 0;
            }
        }

        public bool IsIdle(double now)
        {
            lock (this)
            {
                return _busyUntil <= now;
            }
        }
    }
}
