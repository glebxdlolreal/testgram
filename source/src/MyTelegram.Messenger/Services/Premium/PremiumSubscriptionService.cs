using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Boosts;

namespace MyTelegram.Messenger.Services.Premium;

/// <summary>
/// How long a Premium purchase lasts, in the units clients convert back to months.
/// </summary>
/// <remarks>
/// tdlib turns <c>messageActionGiftPremium.days</c> back into a month count with this exact formula
/// (<c>get_premium_duration_day_count</c> in td/telegram/misc.cpp), so 1/3/6/12/24 months become
/// 30/91/182/365/730 days and round-trip to the same month count.
/// </remarks>
public static class PremiumDuration
{
    public static int DaysFromMonths(int months) => months * 30 + months / 3 + months / 12;
}

/// <summary>What a grant did to the account.</summary>
/// <param name="ExpiresAt">Unix seconds at which the subscription now ends.</param>
/// <param name="BecamePremium">False when the account already had Premium and was only extended.</param>
public sealed record PremiumGrantResult(long ExpiresAt, bool BecamePremium);

/// <summary>
/// The one place that turns Premium on and off for an account.
/// </summary>
/// <remarks>
/// The flag itself lives on the user aggregate and is only ever changed through
/// <c>UpdateUserPremiumStatusCommand</c>: every process keeps user read models in an in-memory cache
/// that only domain events refresh, so writing the read model directly left the other servers
/// believing the old status until they restarted. The subscription term is kept beside it in
/// <see cref="CollectionName"/>, one document per account.
/// </remarks>
public interface IPremiumSubscriptionService
{
    /// <summary>
    /// Extends the account's subscription by <paramref name="days"/> from whichever is later, now or
    /// the current end, and turns Premium on if it was off.
    /// </summary>
    /// <param name="gift">
    /// A gift brings the recipient one boost slot of its own, per the
    /// <a href="https://corefork.telegram.org/api/boost">boost docs</a>; a purchase for oneself brings
    /// <see cref="PremiumSubscriptionService.SubscriptionBoostSlots"/> the first time the account becomes Premium.
    /// </param>
    Task<PremiumGrantResult> GrantAsync(long userId, int days, string source, long? purchaserUserId = null, bool gift = false);

    /// <summary>The end of the account's subscription, or null when it has no recorded term.</summary>
    Task<long?> GetExpiresAtAsync(long userId);

    /// <summary>Turns Premium off for every account whose term has ended; returns how many were revoked.</summary>
    Task<int> RevokeExpiredAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Folds terms written by older code (keyed by a random id instead of <c>premium-{userId}</c>, no
    /// <c>Active</c> flag) into the current layout so they take part in expiry.
    /// </summary>
    Task<int> NormalizeLegacyTermsAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class PremiumSubscriptionService(
    IMongoDatabase mongoDatabase,
    ICommandBus commandBus,
    IUserAppService userAppService,
    ILogger<PremiumSubscriptionService> logger) : IPremiumSubscriptionService, ITransientDependency
{
    public const string CollectionName = "premium_subscriptions";

    /// <summary>Boost slots that come with becoming Premium, granted once rather than on every renewal.</summary>
    public const int SubscriptionBoostSlots = 4;

    public static string DocumentId(long userId) => $"premium-{userId}";

    public async Task<PremiumGrantResult> GrantAsync(long userId, int days, string source, long? purchaserUserId = null, bool gift = false)
    {
        if (days <= 0)
        {
            RpcErrors.RpcErrors400.MonthInvalid.ThrowRpcError();
        }

        var user = await userAppService.GetAsync(userId);
        if (user == null || user.IsDeleted == true)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var collection = mongoDatabase.GetCollection<BsonDocument>(CollectionName);

        // One atomic read-modify-write, so two purchases landing together both count: the new end is
        // computed from the stored one inside the update instead of from a value read beforehand.
        // A term that has already lapsed (or none at all) restarts from now.
        var currentEnd = new BsonDocument("$cond", new BsonArray
        {
            new BsonDocument("$eq", new BsonArray { "$Active", true }),
            new BsonDocument("$toLong", new BsonDocument("$ifNull", new BsonArray { "$ExpiresAt", 0L })),
            0L
        });
        var pipeline = new[]
        {
            new BsonDocument("$set", new BsonDocument
            {
                ["UserId"] = userId,
                ["ExpiresAt"] = new BsonDocument("$add", new BsonArray
                {
                    new BsonDocument("$max", new BsonArray { currentEnd, now }),
                    (long)days * 86400
                }),
                ["ActivatedAt"] = new BsonDocument("$ifNull", new BsonArray { "$ActivatedAt", now }),
                ["Active"] = true,
                ["Source"] = source,
                ["LastPurchaserUserId"] = purchaserUserId.HasValue ? purchaserUserId.Value : BsonNull.Value,
                ["UpdatedAt"] = now
            })
        };

        var updated = await collection.FindOneAndUpdateAsync(
            Builders<BsonDocument>.Filter.Eq("_id", DocumentId(userId)),
            new PipelineUpdateDefinition<BsonDocument>(pipeline),
            new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After });

        var expiresAt = ToInt64(updated["ExpiresAt"]);
        var becamePremium = !user!.Premium;

        if (becamePremium)
        {
            await commandBus.PublishAsync(new UpdateUserPremiumStatusCommand(UserId.Create(userId), true), default);
        }

        if (gift)
        {
            await BoostSlotHelper.GrantAsync(mongoDatabase, userId, 1, expiresAt, gift: true);
        }
        else if (becamePremium)
        {
            await BoostSlotHelper.GrantAsync(mongoDatabase, userId, SubscriptionBoostSlots, expiresAt, gift: false);
        }

        logger.LogInformation("Premium granted: userId={UserId} days={Days} source={Source} purchaser={Purchaser} expiresAt={ExpiresAt} becamePremium={BecamePremium}",
            userId, days, source, purchaserUserId, expiresAt, becamePremium);

        return new PremiumGrantResult(expiresAt, becamePremium);
    }

    public async Task<long?> GetExpiresAtAsync(long userId)
    {
        var document = await mongoDatabase.GetCollection<BsonDocument>(CollectionName)
            .Find(Builders<BsonDocument>.Filter.Eq("_id", DocumentId(userId)))
            .FirstOrDefaultAsync();

        if (document == null || !document.GetValue("Active", false).ToBoolean() || !document.Contains("ExpiresAt"))
        {
            return null;
        }

        return ToInt64(document["ExpiresAt"]);
    }

    public async Task<int> RevokeExpiredAsync(CancellationToken cancellationToken = default)
    {
        var collection = mongoDatabase.GetCollection<BsonDocument>(CollectionName);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expired = await collection
            .Find(Builders<BsonDocument>.Filter.Eq("Active", true) & Builders<BsonDocument>.Filter.Lt("ExpiresAt", now))
            .Limit(500)
            .ToListAsync(cancellationToken);

        var revoked = 0;
        foreach (var document in expired)
        {
            var userId = ToInt64(document["UserId"]);

            // Claimed first, conditionally on the term still being the one that lapsed: a renewal that
            // lands meanwhile moves ExpiresAt forward and this update then matches nothing.
            var claim = await collection.UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", document["_id"]) &
                Builders<BsonDocument>.Filter.Eq("Active", true) &
                Builders<BsonDocument>.Filter.Lt("ExpiresAt", now),
                Builders<BsonDocument>.Update.Set("Active", false).Set("UpdatedAt", now),
                cancellationToken: cancellationToken);
            if (claim.ModifiedCount == 0)
            {
                continue;
            }

            await commandBus.PublishAsync(new UpdateUserPremiumStatusCommand(UserId.Create(userId), false), cancellationToken);
            revoked++;
            logger.LogInformation("Premium expired: userId={UserId} expiresAt={ExpiresAt}", userId, document["ExpiresAt"]);
        }

        return revoked;
    }

    public async Task<int> NormalizeLegacyTermsAsync(CancellationToken cancellationToken = default)
    {
        var collection = mongoDatabase.GetCollection<BsonDocument>(CollectionName);
        var legacy = await collection
            .Find(Builders<BsonDocument>.Filter.Not(Builders<BsonDocument>.Filter.Regex("_id", new BsonRegularExpression("^premium-"))))
            .ToListAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var document in legacy)
        {
            if (!document.Contains("UserId") || !document.Contains("ExpiresAt"))
            {
                continue;
            }

            var userId = ToInt64(document["UserId"]);
            var expiresAt = ToInt64(document["ExpiresAt"]);
            var activatedAt = document.Contains("ActivatedAt") ? ToInt64(document["ActivatedAt"]) : now;

            // Keeps whichever end is later if the account already has a current-layout term.
            var pipeline = new[]
            {
                new BsonDocument("$set", new BsonDocument
                {
                    ["UserId"] = userId,
                    ["ExpiresAt"] = new BsonDocument("$max", new BsonArray
                    {
                        new BsonDocument("$toLong", new BsonDocument("$ifNull", new BsonArray { "$ExpiresAt", 0L })),
                        expiresAt
                    }),
                    ["ActivatedAt"] = new BsonDocument("$ifNull", new BsonArray { "$ActivatedAt", activatedAt }),
                    ["Active"] = true,
                    ["Source"] = new BsonDocument("$ifNull", new BsonArray { "$Source", "legacy" }),
                    ["UpdatedAt"] = now
                })
            };
            await collection.UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", DocumentId(userId)),
                new PipelineUpdateDefinition<BsonDocument>(pipeline),
                new UpdateOptions { IsUpsert = true },
                cancellationToken);
            await collection.DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("_id", document["_id"]), cancellationToken);
        }

        // The Stars gift path also wrote premium-{userId} documents, just without the flag.
        var unflagged = await collection.UpdateManyAsync(
            Builders<BsonDocument>.Filter.Regex("_id", new BsonRegularExpression("^premium-")) &
            Builders<BsonDocument>.Filter.Exists("Active", false),
            Builders<BsonDocument>.Update.Set("Active", true).Set("UpdatedAt", now),
            cancellationToken: cancellationToken);

        return legacy.Count + (int)unflagged.ModifiedCount;
    }

    internal static long ToInt64(BsonValue value) => value.BsonType switch
    {
        BsonType.Int64 => value.AsInt64,
        BsonType.Int32 => value.AsInt32,
        BsonType.Double => (long)value.AsDouble,
        _ => 0
    };
}
