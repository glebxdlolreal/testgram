using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using MyTelegram.Messenger.Handlers.LatestLayer.Payments;
using MyTelegram.Messenger.Services.Bots;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Services.Services;
using StackExchange.Redis;

namespace MyTelegram.Messenger.Tests.Bots;

/// <summary>
/// Feature: the bot broadcast limit and paid broadcasts.
///
/// <para>
/// "Bots are not able to broadcast more than about 30 messages per second, unless they enable paid
/// broadcasts ... up to 1000 messages per second. Each message broadcasted over the free amount of 30 per
/// second incurs a cost of 0.1 Stars per message, paid with Telegram Stars from the bot's balance." A send
/// opts in with allow_paid_floodskip; users are never limited here.
/// </para>
/// </summary>
public class BotBroadcastLimiterTests
{
    private const long Bot = 600000000777;

    [Fact]
    public async Task Thirty_messages_a_second_are_free()
    {
        var limiter = Create(out _);

        for (var i = 0; i < BotBroadcastLimiter.FreeMessagesPerSecond; i++)
        {
            await limiter.CheckAsync(Bot, 1, allowPaidFloodskip: false);
        }
    }

    [Fact]
    public async Task The_thirty_first_message_waits_without_paid_broadcasts()
    {
        var limiter = Create(out _);

        var error = await Should.ThrowAsync<RpcException>(() =>
            limiter.CheckAsync(Bot, BotBroadcastLimiter.FreeMessagesPerSecond + 1, allowPaidFloodskip: false));

        error.RpcError.Message.ShouldBe("FLOOD_WAIT_1");
    }

    [RequiresMongoDbFact]
    public async Task Asking_for_the_paid_path_does_not_help_a_bot_that_never_turned_it_on()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await StarsBalanceHelper.AddBalanceAsync(mongo.Database, Bot, 100);
        var limiter = Create(out _, mongo.Database);

        var error = await Should.ThrowAsync<RpcException>(() =>
            limiter.CheckAsync(Bot, BotBroadcastLimiter.FreeMessagesPerSecond + 1, allowPaidFloodskip: true));

        error.RpcError.Message.ShouldBe("FLOOD_WAIT_1");
        (await StarsBalanceHelper.GetBalanceAsync(mongo.Database, Bot)).ShouldBe(100);
    }

    [Fact]
    public async Task A_refused_send_is_not_counted()
    {
        var limiter = Create(out var counters);

        await Should.ThrowAsync<RpcException>(() => limiter.CheckAsync(Bot, 40, allowPaidFloodskip: false));

        counters.Where(p => p.Key.StartsWith("bot-broadcast:")).ShouldAllBe(p => p.Value == 0);
    }

    [Fact]
    public async Task Users_are_not_limited()
    {
        var limiter = Create(out _);

        await limiter.CheckAsync(2010001, 500, allowPaidFloodskip: false);
    }

    [RequiresMongoDbFact]
    public async Task Paid_messages_cost_a_star_per_ten_and_are_on_the_ledger()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await StarsBalanceHelper.AddBalanceAsync(mongo.Database, Bot, 100);
        var limiter = Create(out var counters, mongo.Database);
        await mongo.Database.GetCollection<BsonDocument>("botfather-bot-state")
            .InsertOneAsync(new BsonDocument { ["BotUserId"] = Bot, ["PaidBroadcast"] = true });

        // 30 free, then 25 paid: two Stars now, the remaining five tenths carried over.
        await limiter.CheckAsync(Bot, 55, allowPaidFloodskip: true);

        (await StarsBalanceHelper.GetBalanceAsync(mongo.Database, Bot)).ShouldBe(98);
        var row = await mongo.Database.GetCollection<BsonDocument>("star-transactions")
            .Find(Builders<BsonDocument>.Filter.Eq("UserId", Bot)).SingleAsync();
        row["Amount"].ToInt64().ShouldBe(-2);
        row["FloodskipNumber"].ToInt32().ShouldBe(20);

        // The five tenths that do not make a Star yet are carried to the next paid messages.
        counters[$"bot-broadcast-billed:{Bot}"].ShouldBe(25);
    }

    [RequiresMongoDbFact]
    public async Task Paid_messages_the_balance_cannot_cover_are_refused()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var limiter = Create(out _, mongo.Database);
        await mongo.Database.GetCollection<BsonDocument>("botfather-bot-state")
            .InsertOneAsync(new BsonDocument { ["BotUserId"] = Bot, ["PaidBroadcast"] = true });

        var error = await Should.ThrowAsync<RpcException>(() => limiter.CheckAsync(Bot, 40, allowPaidFloodskip: true));

        error.RpcError.Message.ShouldBe("BALANCE_TOO_LOW");
    }

    private static BotBroadcastLimiter Create(out Dictionary<string, long> counters, IMongoDatabase? database = null)
    {
        var store = new Dictionary<string, long>();
        counters = store;

        var db = new Mock<IDatabase>();
        db.Setup(p => p.StringIncrementAsync(It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey key, long value, CommandFlags _) => store[key!] = store.GetValueOrDefault(key!) + value);
        db.Setup(p => p.StringDecrementAsync(It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey key, long value, CommandFlags _) => store[key!] = store.GetValueOrDefault(key!) - value);
        db.Setup(p => p.KeyExpireAsync(It.IsAny<RedisKey>(), It.IsAny<TimeSpan?>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);
        var redis = new Mock<IConnectionMultiplexer>();
        redis.Setup(p => p.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(db.Object);

        var peerHelper = new Mock<IPeerHelper>();
        peerHelper.Setup(p => p.IsBotUser(It.IsAny<long>()))
            .Returns((long id) => id >= MyTelegramConsts.BotUserInitId && id < MyTelegramConsts.ChatIdInitId);

        var users = new Mock<IUserAppService>();
        users.Setup(p => p.GetAsync(It.IsAny<long>()))
            .ReturnsAsync((long id) => Mock.Of<IUserReadModel>(u => u.UserId == id && u.Bot));

        return new BotBroadcastLimiter(redis.Object, database ?? new Mock<IMongoDatabase>().Object,
            peerHelper.Object, users.Object, Mock.Of<IReadModelCacheHelper<IUserReadModel>>());
    }
}
