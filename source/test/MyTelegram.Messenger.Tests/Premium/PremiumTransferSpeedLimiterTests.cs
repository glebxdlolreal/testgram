using Microsoft.Extensions.Options;
using Moq;
using MyTelegram.Messenger.Options;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Premium;

namespace MyTelegram.Messenger.Tests.Premium;

/// <summary>
/// The FLOOD_PREMIUM_WAIT_X speed cap: a burst at full speed, then the configured rate, and nothing at
/// all for Premium accounts. See https://corefork.telegram.org/api/files
/// </summary>
public class PremiumTransferSpeedLimiterTests
{
    private const long UserId = 2010001;
    private const long Rate = 1000;

    [Fact]
    public async Task The_burst_goes_through_then_the_client_is_told_how_long_to_wait()
    {
        var clock = new ManualClock();
        var limiter = Limiter(clock, premium: false, burst: 5000);

        for (var i = 0; i < 5; i++)
        {
            (await limiter.AcquireAsync(UserId, FileTransferDirection.Download, 1000)).ShouldBe(0);
        }

        (await limiter.AcquireAsync(UserId, FileTransferDirection.Download, 1000)).ShouldBe(1);
        (await limiter.AcquireAsync(UserId, FileTransferDirection.Download, 3000)).ShouldBe(3);
    }

    [Fact]
    public async Task A_refused_part_is_not_charged_and_the_allowance_refills_at_the_rate()
    {
        var clock = new ManualClock();
        var limiter = Limiter(clock, premium: false, burst: 2000);
        (await limiter.AcquireAsync(UserId, FileTransferDirection.Upload, 2000)).ShouldBe(0);
        (await limiter.AcquireAsync(UserId, FileTransferDirection.Upload, 1000)).ShouldBe(1);
        (await limiter.AcquireAsync(UserId, FileTransferDirection.Upload, 1000)).ShouldBe(1);

        clock.Advance(TimeSpan.FromSeconds(1));

        (await limiter.AcquireAsync(UserId, FileTransferDirection.Upload, 1000)).ShouldBe(0);
        (await limiter.AcquireAsync(UserId, FileTransferDirection.Upload, 1000)).ShouldBe(1);
    }

    [Fact]
    public async Task Upload_and_download_have_separate_allowances()
    {
        var limiter = Limiter(new ManualClock(), premium: false, burst: 1000);

        (await limiter.AcquireAsync(UserId, FileTransferDirection.Upload, 1000)).ShouldBe(0);
        (await limiter.AcquireAsync(UserId, FileTransferDirection.Download, 1000)).ShouldBe(0);
        (await limiter.AcquireAsync(UserId, FileTransferDirection.Upload, 1000)).ShouldBe(1);
    }

    [Fact]
    public async Task Premium_accounts_are_never_limited()
    {
        var limiter = Limiter(new ManualClock(), premium: true, burst: 1000);

        for (var i = 0; i < 100; i++)
        {
            (await limiter.AcquireAsync(UserId, FileTransferDirection.Download, 1000)).ShouldBe(0);
        }
    }

    [Fact]
    public async Task A_part_larger_than_the_burst_still_gets_through_once_idle()
    {
        var limiter = Limiter(new ManualClock(), premium: false, burst: 100);

        (await limiter.AcquireAsync(UserId, FileTransferDirection.Download, 1000)).ShouldBe(0);
    }

    [Fact]
    public async Task Disabled_means_no_limit()
    {
        var limiter = Limiter(new ManualClock(), premium: false, burst: 1000, enabled: false);

        for (var i = 0; i < 10; i++)
        {
            (await limiter.AcquireAsync(UserId, FileTransferDirection.Upload, 1000)).ShouldBe(0);
        }
    }

    private static PremiumTransferSpeedLimiter Limiter(TimeProvider clock, bool premium, long burst, bool enabled = true)
    {
        var options = new Mock<IOptionsMonitor<PremiumOptions>>();
        options.SetupGet(o => o.CurrentValue).Returns(new PremiumOptions
        {
            SpeedLimit = new PremiumSpeedLimitOptions
            {
                Enabled = enabled, UploadBytesPerSecond = Rate, DownloadBytesPerSecond = Rate, BurstBytes = burst
            }
        });

        return new PremiumTransferSpeedLimiter(PremiumPricingAndLimitTests.Users(premium).Object, options.Object, clock);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
