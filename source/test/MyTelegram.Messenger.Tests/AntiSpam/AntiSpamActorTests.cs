using EventFlow.Queries;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using MyTelegram.Messenger.Services.AntiSpam;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.Queries;
using MyTelegram.ReadModel.Interfaces;

namespace MyTelegram.Messenger.Tests.AntiSpam;

public class AntiSpamActorTests
{
    [RequiresMongoDbTheory]
    [InlineData("missing-marker")]
    [InlineData("not-ready")]
    [InlineData("wrong-marker-id")]
    [InlineData("missing-user")]
    [InlineData("ordinary-user")]
    [InlineData("wrong-hash")]
    [InlineData("wrong-username")]
    [InlineData("wrong-name")]
    [InlineData("deleted")]
    [InlineData("unverified")]
    [InlineData("duplicate-users")]
    [InlineData("typed-id")]
    [InlineData("raw-hash")]
    [InlineData("raw-name")]
    [InlineData("raw-bot")]
    [InlineData("malformed-marker-hash")]
    [InlineData("duplicate-username")]
    public async Task Actor_provenance_mismatches_fail_closed(string defect)
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = await CreateAsync(mongo.Database, defect);
        var error = await Should.ThrowAsync<RpcException>(() => service.EnsureAvailableAsync());
        error.RpcError.Message.ShouldBe("USER_ID_INVALID");
    }

    [RequiresMongoDbFact]
    public async Task Valid_owned_verified_actor_is_available()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = await CreateAsync(mongo.Database, "none");
        await service.EnsureAvailableAsync();
    }

    private static async Task<AntiSpamActorService> CreateAsync(IMongoDatabase database, string defect)
    {
        if (defect != "missing-marker")
            await database.GetCollection<BsonDocument>(MyTelegramConsts.AntiSpamActorCollection).InsertOneAsync(new BsonDocument
            {
                ["_id"] = "actor",
                ["ready"] = defect != "not-ready",
                ["user_id"] = MyTelegramConsts.AntiSpamBotUserId + (defect == "wrong-marker-id" ? 1 : 0),
                ["access_hash"] = defect == "malformed-marker-hash" ? new BsonString("invalid") : new BsonInt64(987654321L)
            });
        if (defect != "missing-user")
        {
            var users = database.GetCollection<BsonDocument>("eventflow-userreadmodel");
            await users.InsertOneAsync(new BsonDocument
            {
                ["_id"] = "actor-user", ["UserId"] = MyTelegramConsts.AntiSpamBotUserId,
                ["AccessHash"] = defect == "raw-hash" ? 123L : 987654321L,
                ["Bot"] = defect != "raw-bot", ["Verified"] = true, ["IsDeleted"] = false,
                ["FirstName"] = defect == "raw-name" ? "Other actor" : "Testgram Anti-Spam",
                ["UserName"] = MyTelegramConsts.AntiSpamBotUserName
            });
            if (defect == "duplicate-username")
                await users.InsertOneAsync(new BsonDocument
                {
                    ["_id"] = "colliding-name", ["UserId"] = MyTelegramConsts.AntiSpamBotUserId + 1,
                    ["UserName"] = MyTelegramConsts.AntiSpamBotUserName.ToUpperInvariant()
                });
            if (defect == "duplicate-users")
                await users.InsertOneAsync(new BsonDocument { ["_id"] = "colliding-user", ["UserId"] = MyTelegramConsts.AntiSpamBotUserId });
        }
        var user = new Mock<IUserReadModel>();
        user.SetupGet(u => u.UserId).Returns(MyTelegramConsts.AntiSpamBotUserId + (defect == "typed-id" ? 1 : 0));
        user.SetupGet(u => u.Bot).Returns(defect != "ordinary-user");
        user.SetupGet(u => u.Verified).Returns(defect != "unverified");
        user.SetupGet(u => u.IsDeleted).Returns(defect == "deleted");
        user.SetupGet(u => u.UserName).Returns(defect == "wrong-username" ? "unowned_bot" : MyTelegramConsts.AntiSpamBotUserName);
        user.SetupGet(u => u.FirstName).Returns(defect == "wrong-name" ? "Other actor" : "Testgram Anti-Spam");
        user.SetupGet(u => u.AccessHash).Returns(defect == "wrong-hash" ? 111L : 987654321L);
        var query = new Mock<IQueryProcessor>();
        query.Setup(q => q.ProcessAsync(It.IsAny<GetUserByIdQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(defect == "missing-user" ? null : user.Object);
        return new AntiSpamActorService(database, query.Object);
    }
}
