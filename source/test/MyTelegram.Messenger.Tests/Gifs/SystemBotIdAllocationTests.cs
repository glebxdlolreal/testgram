using System.Reflection;
using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Bots;
using MyTelegram.Messenger.Tests.Stats;

namespace MyTelegram.Messenger.Tests.Gifs;

/// <summary>
/// The block of user ids reserved for built-in system bots (<c>@gif</c> and whatever follows it) must
/// stay out of BotFather's reach. It does not come from a counter — the next bot id is the highest bot
/// id in the read model plus one — so without an explicit exclusion the allocator walks into the block
/// as soon as a bot is seeded inside it, and hands a user's new bot the id of a system bot.
/// </summary>
public class SystemBotIdAllocationTests
{
    [Fact]
    public void The_gif_bot_sits_inside_the_reserved_block()
    {
        MyTelegramConsts.IsReservedSystemBotUserId(MyTelegramConsts.GifSearchBotUserId).ShouldBeTrue();
        MyTelegramConsts.IsReservedSystemBotUserId(MyTelegramConsts.SystemBotUserIdBase - 1).ShouldBeFalse();
        MyTelegramConsts
            .IsReservedSystemBotUserId(MyTelegramConsts.SystemBotUserIdBase +
                                      MyTelegramConsts.SystemBotUserIdReservedCount)
            .ShouldBeFalse();

        // Ordinary bots start well below the block, so the ids already handed out are unaffected.
        MyTelegramConsts.SystemBotUserIdBase.ShouldBeGreaterThan(MyTelegramConsts.BotUserInitId);
    }

    [RequiresMongoDbFact]
    public async Task A_seeded_system_bot_does_not_move_the_next_ordinary_bot_id()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await InsertBotAsync(mongo.Database, MyTelegramConsts.BotUserInitId + 5);
        await InsertBotAsync(mongo.Database, MyTelegramConsts.GifSearchBotUserId);

        (await GetNextUserIdAsync(mongo.Database)).ShouldBe(MyTelegramConsts.BotUserInitId + 6);
    }

    [RequiresMongoDbFact]
    public async Task An_id_that_lands_in_the_reserved_block_is_pushed_past_it()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await InsertBotAsync(mongo.Database, MyTelegramConsts.SystemBotUserIdBase - 1);

        (await GetNextUserIdAsync(mongo.Database)).ShouldBe(MyTelegramConsts.SystemBotUserIdBase +
                                                           MyTelegramConsts.SystemBotUserIdReservedCount);
    }

    private static Task InsertBotAsync(IMongoDatabase database, long userId)
    {
        return database.GetCollection<BsonDocument>("eventflow-userreadmodel").InsertOneAsync(
            new BsonDocument { { "_id", $"user-{userId}" }, { "UserId", userId }, { "Bot", true } });
    }

    /// <summary>
    /// <c>GetNextUserIdAsync</c> is private and only touches the injected database, so the remaining
    /// constructor arguments are left null rather than mocked.
    /// </summary>
    private static async Task<long> GetNextUserIdAsync(IMongoDatabase database)
    {
        var parameters = typeof(BotFatherBotService).GetConstructors().Single().GetParameters();
        var arguments = parameters.Select(p => p.ParameterType == typeof(IMongoDatabase) ? database : null).ToArray();
        var service = (BotFatherBotService)Activator.CreateInstance(typeof(BotFatherBotService), arguments)!;

        var method = typeof(BotFatherBotService).GetMethod("GetNextUserIdAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        return await (Task<long>)method.Invoke(service, [])!;
    }
}
