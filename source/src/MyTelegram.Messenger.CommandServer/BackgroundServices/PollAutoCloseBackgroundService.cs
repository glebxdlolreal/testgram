using EventFlow.Queries;
using Microsoft.Extensions.Hosting;
using MyTelegram.Domain.Aggregates.Poll;
using MyTelegram.Queries;

namespace MyTelegram.Messenger.CommandServer.BackgroundServices;

/// <summary>
/// Closes polls once their deadline passes. Polls created with <c>close_period</c> or
/// <c>close_date</c> carry an absolute deadline; clients render a live countdown against it and
/// expect the poll to actually stop accepting votes when it hits zero.
/// </summary>
/// <remarks>
/// A timer is armed per poll rather than polling on a coarse interval, because the countdown a
/// client shows is second-accurate. A periodic rescan picks up polls created after the last pass
/// and re-arms anything missed across a restart.
/// </remarks>
public class PollAutoCloseBackgroundService(
    IQueryProcessor queryProcessor,
    ICommandBus commandBus,
    ILogger<PollAutoCloseBackgroundService> logger)
    : BackgroundService
{
    /// <summary>How far ahead each rescan arms timers. Anything later is picked up by a later pass.</summary>
    private static readonly TimeSpan ScheduleHorizon = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan RescanInterval = TimeSpan.FromMinutes(1);

    private readonly Dictionary<long, Timer> _timers = new();
    private readonly object _timersLock = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Poll auto-close service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScheduleExpiringPollsAsync(stoppingToken);
                await Task.Delay(RescanInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in poll auto-close loop");

                // Don't spin on a persistent failure (e.g. mongo down).
                try
                {
                    await Task.Delay(RescanInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        lock (_timersLock)
        {
            foreach (var timer in _timers.Values)
            {
                timer.Dispose();
            }

            _timers.Clear();
        }

        logger.LogInformation("Poll auto-close service stopped");
    }

    private async Task ScheduleExpiringPollsAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow.ToTimestamp();
        var horizon = now + (int)ScheduleHorizon.TotalSeconds;

        var polls = await queryProcessor.ProcessAsync(
            new GetActivePollsWithCloseDateQuery(horizon), cancellationToken);

        foreach (var poll in polls)
        {
            if (poll.CloseDate == null)
            {
                continue;
            }

            lock (_timersLock)
            {
                if (_timers.ContainsKey(poll.PollId))
                {
                    continue;
                }
            }

            var delaySeconds = poll.CloseDate.Value - now;
            if (delaySeconds <= 0)
            {
                // Deadline already passed — most likely while the server was down.
                await ClosePollAsync(poll.PollId, CancellationToken.None);
                continue;
            }

            ArmTimer(poll.PollId, TimeSpan.FromSeconds(delaySeconds));
        }
    }

    private void ArmTimer(long pollId, TimeSpan delay)
    {
        lock (_timersLock)
        {
            if (_timers.ContainsKey(pollId))
            {
                return;
            }

            // Fires once; ClosePollAsync disposes the timer afterwards.
            var timer = new Timer(
                _ => _ = ClosePollAsync(pollId, CancellationToken.None),
                null,
                delay,
                Timeout.InfiniteTimeSpan);

            _timers[pollId] = timer;
        }
    }

    private async Task ClosePollAsync(long pollId, CancellationToken cancellationToken)
    {
        lock (_timersLock)
        {
            if (_timers.Remove(pollId, out var timer))
            {
                timer.Dispose();
            }
        }

        try
        {
            // ClosePoll is idempotent, so racing with a manual stop is harmless.
            await commandBus.PublishAsync(new ClosePollCommand(PollId.Create(pollId), DateTime.UtcNow.ToTimestamp()),
                cancellationToken);

            logger.LogInformation("Auto-closed poll {PollId}", pollId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to auto-close poll {PollId}", pollId);
        }
    }
}
