using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MyTelegram.Messenger.Services.Premium;

namespace MyTelegram.Messenger.CommandServer.BackgroundServices;

/// <summary>
/// Drives <see cref="IPremiumSubscriptionService.RevokeExpiredAsync"/>: turns Premium off once an
/// account's paid term has run out. Nothing did this before, so a one-month purchase lasted forever.
/// Subscriptions with recurring payments (<see cref="IPremiumAutoRenewService"/>) are charged and
/// extended just before that.
/// </summary>
/// <remarks>
/// Accounts holding Premium with no recorded term (seeded, or granted on sign-up through
/// <c>SetPremiumToTrueAfterUserCreated</c>) have nothing to expire and are left alone.
/// </remarks>
public class PremiumExpiryBackgroundService(
    IServiceScopeFactory serviceScopeFactory,
    ILogger<PremiumExpiryBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Premium expiry worker started");
        var normalized = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = serviceScopeFactory.CreateScope();
                var subscriptionService = scope.ServiceProvider.GetRequiredService<IPremiumSubscriptionService>();

                if (!normalized)
                {
                    var count = await subscriptionService.NormalizeLegacyTermsAsync(stoppingToken);
                    normalized = true;
                    if (count > 0)
                    {
                        logger.LogInformation("Normalized {Count} legacy Premium subscription records", count);
                    }
                }

                // Renewals first: a term about to end is extended before it can be revoked.
                var autoRenewService = scope.ServiceProvider.GetRequiredService<IPremiumAutoRenewService>();
                var premiumBotService = scope.ServiceProvider.GetRequiredService<IPremiumBotService>();
                foreach (var outcome in await autoRenewService.RenewDueAsync(stoppingToken))
                {
                    await premiumBotService.NotifyRenewalAsync(outcome);
                }

                await subscriptionService.RevokeExpiredAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Premium expiry worker error");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }
}
