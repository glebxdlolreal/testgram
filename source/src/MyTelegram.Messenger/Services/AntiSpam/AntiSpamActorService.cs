using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.AntiSpam;

/// <summary>Fails closed unless the seeded, locally owned actor and its provenance still match.</summary>
public sealed class AntiSpamActorService(IMongoDatabase database, IQueryProcessor queryProcessor)
    : IAntiSpamActorService, ITransientDependency
{
    public async Task EnsureAvailableAsync(CancellationToken cancellationToken = default)
    {
        var marker = await database.GetCollection<BsonDocument>(MyTelegramConsts.AntiSpamActorCollection)
            .Find(new BsonDocument("_id", "actor")).FirstOrDefaultAsync(cancellationToken);
        var rows = await database.GetCollection<BsonDocument>("eventflow-userreadmodel")
            .Find(Builders<BsonDocument>.Filter.Or(
                new BsonDocument("UserId", MyTelegramConsts.AntiSpamBotUserId),
                new BsonDocument("UserName", new BsonRegularExpression("^" + MyTelegramConsts.AntiSpamBotUserName + "$", "i"))))
            .Limit(2).ToListAsync(cancellationToken);
        var user = await queryProcessor.ProcessAsync(new GetUserByIdQuery(MyTelegramConsts.AntiSpamBotUserId), cancellationToken);
        if (marker == null || marker.GetValue("ready", false) != BsonBoolean.True ||
            !MatchesInteger(marker, "user_id", MyTelegramConsts.AntiSpamBotUserId) ||
            !marker.TryGetValue("access_hash", out var hash) || !hash.IsInt64 && !hash.IsInt32 || hash.ToInt64() == 0 ||
            rows.Count != 1 || !MatchesInteger(rows[0], "UserId", MyTelegramConsts.AntiSpamBotUserId) ||
            !MatchesInteger(rows[0], "AccessHash", hash.ToInt64()) ||
            rows[0].GetValue("Bot", false) != BsonBoolean.True ||
            rows[0].GetValue("Verified", false) != BsonBoolean.True ||
            rows[0].GetValue("IsDeleted", BsonNull.Value) != BsonNull.Value && rows[0].GetValue("IsDeleted") != BsonBoolean.False ||
            rows[0].GetValue("UserName", BsonNull.Value) != MyTelegramConsts.AntiSpamBotUserName ||
            rows[0].GetValue("FirstName", BsonNull.Value) != "Testgram Anti-Spam" ||
            user == null || user.UserId != MyTelegramConsts.AntiSpamBotUserId || !user.Bot || !user.Verified || user.IsDeleted == true ||
            user.UserName != MyTelegramConsts.AntiSpamBotUserName || user.FirstName != "Testgram Anti-Spam" ||
            user.AccessHash != hash.ToInt64())
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
        }
    }

    private static bool MatchesInteger(BsonDocument document, string field, long expected) =>
        document.TryGetValue(field, out var value) && (value.IsInt64 || value.IsInt32) && value.ToInt64() == expected;
}
