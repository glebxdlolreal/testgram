using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Handlers.LatestLayer.Payments;
using StackExchange.Redis;

namespace MyTelegram.Messenger.Services.Bots;

/// <summary>
/// The bot broadcast limit and paid broadcasts.
/// <para>
/// "For bulk notifications, bots are not able to broadcast more than about 30 messages per second, unless
/// they enable paid broadcasts to increase the limit. Enabling paid broadcasts in @BotFather allows a bot to
/// broadcast up to 1000 messages per second. Each message broadcasted over the free amount of 30 per second
/// incurs a cost of 0.1 Stars per message, paid with Telegram Stars from the bot's balance."
/// (https://core.telegram.org/bots/faq). A send opts in with <c>allow_paid_floodskip</c>.
/// </para>
/// </summary>
public interface IBotBroadcastLimiter
{
    /// <summary>
    /// Counts <paramref name="messageCount"/> messages a bot is about to send, charging the paid ones.
    /// Throws <c>FLOOD_WAIT_1</c> past the limit and <c>BALANCE_TOO_LOW</c> when paid messages cannot be paid.
    /// Users are not limited here.
    /// </summary>
    Task CheckAsync(long senderUserId, int messageCount, bool allowPaidFloodskip);

    Task<bool> IsPaidBroadcastEnabledAsync(long botUserId);

    Task SetPaidBroadcastAsync(long botUserId, bool enabled);

    /// <summary>Distinct users who wrote to the bot in the last 30 days; also stored as <c>user.bot_active_users</c>.</summary>
    Task<int> CountMonthlyActiveUsersAsync(long botUserId);
}

public class BotBroadcastLimiter(
    IConnectionMultiplexer redis,
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IUserAppService userAppService,
    IReadModelCacheHelper<IUserReadModel> userReadModelCacheHelper,
    IEventBus eventBus) : IBotBroadcastLimiter, ITransientDependency
{
    public const int FreeMessagesPerSecond = 30;
    public const int PaidMessagesPerSecond = 1000;

    /// <summary>0.1 Stars per paid message: the ledger keeps whole Stars, so one Star is taken per ten.</summary>
    public const int PaidMessagesPerStar = 10;

    /// <summary>The minimum real BotFather showed for this account's bot (2026-11-02).</summary>
    public const int MinMonthlyActiveUsers = 10000;

    /// <summary>"A bot must have at least 100,000 Stars on its balance" (Bot FAQ).</summary>
    public const long MinStarsBalance = 100000;

    private const string BotCollection = "botfather-bot-state";

    public async Task CheckAsync(long senderUserId, int messageCount, bool allowPaidFloodskip)
    {
        if (messageCount <= 0 || !peerHelper.IsBotUser(senderUserId))
        {
            return;
        }

        var sender = await userAppService.GetAsync(senderUserId);
        if (sender is not { Bot: true })
        {
            return;
        }

        var db = redis.GetDatabase();
        var windowKey = $"bot-broadcast:{senderUserId}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        var total = await db.StringIncrementAsync(windowKey, messageCount);
        if (total == messageCount)
        {
            await db.KeyExpireAsync(windowKey, TimeSpan.FromSeconds(2));
        }

        if (total <= FreeMessagesPerSecond)
        {
            return;
        }

        var paid = allowPaidFloodskip && total <= PaidMessagesPerSecond && await IsPaidBroadcastEnabledAsync(senderUserId);
        if (!paid)
        {
            await db.StringDecrementAsync(windowKey, messageCount);
            RpcErrors.RpcErrors420.FloodWaitX.ThrowRpcError(1);
        }

        // Only the messages over the free 30 are billed.
        var billed = (int)Math.Min(messageCount, total - FreeMessagesPerSecond);
        var tenthsKey = $"bot-broadcast-billed:{senderUserId}";
        var billedTotal = await db.StringIncrementAsync(tenthsKey, billed);
        var starsDue = billedTotal / PaidMessagesPerStar - (billedTotal - billed) / PaidMessagesPerStar;
        if (starsDue == 0)
        {
            return;
        }

        if (!await StarsBalanceHelper.TryDebitAsync(mongoDatabase, senderUserId, starsDue))
        {
            await db.StringDecrementAsync(tenthsKey, billed);
            await db.StringDecrementAsync(windowKey, messageCount);
            RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();
        }

        await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, senderUserId, -starsDue,
            title: "Paid broadcast", floodskipNumber: (int)(starsDue * PaidMessagesPerStar));
    }

    public async Task<bool> IsPaidBroadcastEnabledAsync(long botUserId)
    {
        var bot = await mongoDatabase.GetCollection<BsonDocument>(BotCollection)
            .Find(Builders<BsonDocument>.Filter.Eq("BotUserId", botUserId)).FirstOrDefaultAsync();
        return bot != null && bot.TryGetValue("PaidBroadcast", out var value) && value.IsBoolean && value.AsBoolean;
    }

    public Task SetPaidBroadcastAsync(long botUserId, bool enabled)
    {
        return mongoDatabase.GetCollection<BsonDocument>(BotCollection).UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("BotUserId", botUserId),
            Builders<BsonDocument>.Update.Set("PaidBroadcast", enabled));
    }

    public async Task<int> CountMonthlyActiveUsersAsync(long botUserId)
    {
        var since = DateTime.UtcNow.AddDays(-30).ToTimestamp();

        // Served by the { OwnerPeerId, Out, Date } index: the bot's copies of what users sent it.
        var senders = await mongoDatabase.GetCollection<BsonDocument>("eventflow-messagereadmodel")
            .Distinct<long>("SenderPeerId",
                Builders<BsonDocument>.Filter.Eq("OwnerPeerId", botUserId) &
                Builders<BsonDocument>.Filter.Eq("Out", false) &
                Builders<BsonDocument>.Filter.Gte("Date", since))
            .ToListAsync();
        var count = senders.Count(p => p != botUserId);

        // user.bot_active_users is what clients print under the bot's name.
        await mongoDatabase.GetCollection<BsonDocument>("eventflow-userreadmodel").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("UserId", botUserId),
            Builders<BsonDocument>.Update.Set("BotActiveUsers", count));
        userReadModelCacheHelper.RemoveById(botUserId);
        await eventBus.PublishAsync(new BotUserChangedEvent(botUserId));

        return count;
    }
}
