using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using MyTelegram.Messenger.Handlers.LatestLayer.Payments;

namespace MyTelegram.Messenger.Services.Premium;

/// <summary>
/// Recurring payments for a Premium subscription bought through <c>@PremiumBot</c>: the card the
/// invoice was paid with, kept on a Stripe customer, and whether it is charged again when the term ends.
/// </summary>
public sealed class PremiumAutoRenewalDocument
{
    [BsonId]
    public long UserId { get; set; }

    /// <summary>Off after <c>/stop</c> or a declined renewal; <c>/resume</c> turns it back on.</summary>
    public bool Enabled { get; set; }

    /// <summary>The term each renewal buys, and its price, as quoted on the invoice that saved the card.</summary>
    public int Months { get; set; }
    public long Amount { get; set; }
    public string Currency { get; set; } = string.Empty;

    public string StripeCustomerId { get; set; } = string.Empty;
    public string StripePaymentMethodId { get; set; } = string.Empty;

    /// <summary>The term end the last renewal was claimed for, so one term is never charged twice.</summary>
    public long LastRenewedPeriodEnd { get; set; }

    /// <summary>Declined attempts for the current term; each retry uses a fresh Stripe idempotency key.</summary>
    public int FailedAttempts { get; set; }

    /// <summary>Unix seconds before which a declined renewal is not retried.</summary>
    public long NextAttemptAt { get; set; }

    /// <summary>Why it was switched off: the reason picked under <c>/stop</c>, or <c>charge-failed</c>.</summary>
    public string? StopReason { get; set; }
    public long UpdatedAt { get; set; }
}

/// <summary>
/// One renewal attempt. <see cref="WillRetry"/>: it was declined but the term has time left, so it is
/// tried again after <see cref="PremiumAutoRenewService.RetryInterval"/>.
/// </summary>
public sealed record PremiumRenewalOutcome(long UserId, bool Succeeded, int Months, long Amount, string Currency, long ExpiresAt,
    bool WillRetry = false);

public interface IPremiumAutoRenewService
{
    Task<PremiumAutoRenewalDocument?> GetAsync(long userId);

    /// <summary>Keeps the card a subscription invoice was paid with and turns renewals on for its term.</summary>
    Task SaveCardAsync(long userId, int months, long amount, string currency, string customerId, string paymentMethodId);

    /// <summary>Switches renewals on or off; false when there is no card to charge.</summary>
    Task<bool> SetEnabledAsync(long userId, bool enabled, string? stopReason = null);

    /// <summary>Charges every subscription whose term ends within the lead time and extends it.</summary>
    Task<IReadOnlyList<PremiumRenewalOutcome>> RenewDueAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class PremiumAutoRenewService(
    IMongoDatabase mongoDatabase,
    IOptions<MyTelegramMessengerServerOptions> options,
    IPremiumSubscriptionService premiumSubscriptionService,
    IPremiumPurchaseGate premiumPurchaseGate,
    ILogger<PremiumAutoRenewService> logger) : IPremiumAutoRenewService, ITransientDependency
{
    public const string CollectionName = "premium-auto-renewals";

    /// <summary>
    /// How long before the term ends the renewal is first charged: early enough that a declined card can be
    /// retried a few times before Premium lapses.
    /// </summary>
    public static readonly TimeSpan RenewalLead = TimeSpan.FromHours(24);

    /// <summary>"We will try again in several hours."</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromHours(6);

    private IMongoCollection<PremiumAutoRenewalDocument> Collection =>
        mongoDatabase.GetCollection<PremiumAutoRenewalDocument>(CollectionName);

    public async Task<PremiumAutoRenewalDocument?> GetAsync(long userId) =>
        await Collection.Find(x => x.UserId == userId).FirstOrDefaultAsync();

    public Task SaveCardAsync(long userId, int months, long amount, string currency, string customerId, string paymentMethodId) =>
        Collection.UpdateOneAsync(x => x.UserId == userId,
            Builders<PremiumAutoRenewalDocument>.Update
                .Set(x => x.Enabled, true)
                .Set(x => x.Months, months)
                .Set(x => x.Amount, amount)
                .Set(x => x.Currency, currency)
                .Set(x => x.StripeCustomerId, customerId)
                .Set(x => x.StripePaymentMethodId, paymentMethodId)
                .Set(x => x.StopReason, null)
                .Set(x => x.FailedAttempts, 0)
                .Set(x => x.NextAttemptAt, 0)
                .Set(x => x.UpdatedAt, DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            new UpdateOptions { IsUpsert = true });

    public async Task<bool> SetEnabledAsync(long userId, bool enabled, string? stopReason = null)
    {
        var result = await Collection.UpdateOneAsync(
            x => x.UserId == userId && x.StripePaymentMethodId != string.Empty,
            Builders<PremiumAutoRenewalDocument>.Update
                .Set(x => x.Enabled, enabled)
                .Set(x => x.StopReason, enabled ? null : stopReason)
                .Set(x => x.UpdatedAt, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

        return result.MatchedCount > 0;
    }

    public async Task<IReadOnlyList<PremiumRenewalOutcome>> RenewDueAsync(CancellationToken cancellationToken = default)
    {
        var outcomes = new List<PremiumRenewalOutcome>();
        var secretKey = options.Value.Stripe.SecretKey;
        if (string.IsNullOrEmpty(secretKey))
        {
            // No Stripe account, no saved cards: nothing can be renewed.
            return outcomes;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var enabled = await Collection.Find(x => x.Enabled && x.NextAttemptAt <= now).Limit(500).ToListAsync(cancellationToken);
        var dueBy = DateTimeOffset.UtcNow.Add(RenewalLead).ToUnixTimeSeconds();

        foreach (var renewal in enabled)
        {
            var expiresAt = await premiumSubscriptionService.GetExpiresAtAsync(renewal.UserId);
            if (expiresAt == null)
            {
                // The term lapsed before it could be renewed (the account lost Premium some other way).
                await SetEnabledAsync(renewal.UserId, false, "expired");
                continue;
            }

            if (expiresAt.Value > dueBy || renewal.LastRenewedPeriodEnd == expiresAt.Value)
            {
                continue;
            }

            // Claimed for this term end first, so two workers (or a retry) never charge it twice.
            var claim = await Collection.UpdateOneAsync(
                x => x.UserId == renewal.UserId && x.Enabled && x.LastRenewedPeriodEnd != expiresAt.Value,
                Builders<PremiumAutoRenewalDocument>.Update.Set(x => x.LastRenewedPeriodEnd, expiresAt.Value),
                cancellationToken: cancellationToken);
            if (claim.ModifiedCount == 0)
            {
                continue;
            }

            outcomes.Add(await RenewAsync(renewal, expiresAt.Value, secretKey));
        }

        return outcomes;
    }

    private async Task<PremiumRenewalOutcome> RenewAsync(PremiumAutoRenewalDocument renewal, long periodEnd, string secretKey)
    {
        var failed = new PremiumRenewalOutcome(renewal.UserId, false, renewal.Months, renewal.Amount, renewal.Currency, periodEnd);

        if (await premiumPurchaseGate.IsBlockedAsync(renewal.UserId))
        {
            await SetEnabledAsync(renewal.UserId, false, "purchase-blocked");
            logger.LogInformation("Premium renewal skipped, purchase is blocked: userId={UserId}", renewal.UserId);
            return failed;
        }

        string status;
        string paymentIntentId;
        try
        {
            (paymentIntentId, status) = await StripeHelper.ChargeOffSessionAsync(secretKey, renewal.StripeCustomerId,
                renewal.StripePaymentMethodId, renewal.Amount, renewal.Currency, $"Telegram Premium renewal ({renewal.Months} months)",
                $"premium-renewal-{renewal.UserId}-{periodEnd}-{renewal.FailedAttempts}");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Premium renewal declined: userId={UserId} periodEnd={PeriodEnd} attempt={Attempt}",
                renewal.UserId, periodEnd, renewal.FailedAttempts + 1);
            return await DeclinedAsync(renewal, periodEnd, failed);
        }

        if (status != "succeeded")
        {
            logger.LogWarning("Premium renewal not settled: userId={UserId} status={Status} intent={PaymentIntentId}",
                renewal.UserId, status, paymentIntentId);
            return await DeclinedAsync(renewal, periodEnd, failed);
        }

        var result = await premiumSubscriptionService.GrantAsync(renewal.UserId, PremiumDuration.DaysFromMonths(renewal.Months),
            "premium-renewal", renewal.UserId);
        await Collection.UpdateOneAsync(x => x.UserId == renewal.UserId,
            Builders<PremiumAutoRenewalDocument>.Update.Set(x => x.FailedAttempts, 0).Set(x => x.NextAttemptAt, 0));
        logger.LogInformation("Premium renewed: userId={UserId} months={Months} amount={Amount} {Currency} intent={PaymentIntentId} expiresAt={ExpiresAt}",
            renewal.UserId, renewal.Months, renewal.Amount, renewal.Currency, paymentIntentId, result.ExpiresAt);

        return new PremiumRenewalOutcome(renewal.UserId, true, renewal.Months, renewal.Amount, renewal.Currency, result.ExpiresAt);
    }

    /// <summary>
    /// A declined renewal is retried while the term has time left; the last chance switches recurring
    /// payments off and lets the term lapse.
    /// </summary>
    private async Task<PremiumRenewalOutcome> DeclinedAsync(PremiumAutoRenewalDocument renewal, long periodEnd,
        PremiumRenewalOutcome failed)
    {
        var nextAttempt = DateTimeOffset.UtcNow.Add(RetryInterval).ToUnixTimeSeconds();
        if (nextAttempt < periodEnd)
        {
            // Released for this term again, so the next attempt can claim it.
            await Collection.UpdateOneAsync(x => x.UserId == renewal.UserId,
                Builders<PremiumAutoRenewalDocument>.Update
                    .Set(x => x.LastRenewedPeriodEnd, 0)
                    .Inc(x => x.FailedAttempts, 1)
                    .Set(x => x.NextAttemptAt, nextAttempt));
            return failed with { WillRetry = true };
        }

        await SetEnabledAsync(renewal.UserId, false, "charge-failed");
        return failed;
    }
}
