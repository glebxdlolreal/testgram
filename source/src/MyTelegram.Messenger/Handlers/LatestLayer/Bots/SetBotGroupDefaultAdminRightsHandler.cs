using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Bots;
/// <summary>
/// Set the default <a href="https://corefork.telegram.org/api/rights#suggested-bot-rights">suggested admin rights</a> for bots being added as admins to groups, see <a href="https://corefork.telegram.org/api/rights#suggested-bot-rights">here for more info on how to handle them »</a>.
/// Possible errors
/// Code Type Description
/// 400 RIGHTS_NOT_MODIFIED The new admin rights are equal to the old rights, no change was made.
/// 400 USER_BOT_REQUIRED This method can only be called by a bot.
/// <para><c>See <a href="https://corefork.telegram.org/method/bots.setBotGroupDefaultAdminRights"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✖] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class SetBotGroupDefaultAdminRightsHandler(
    IMongoDatabase mongoDatabase,
    IQueryProcessor queryProcessor) : RpcResultObjectHandler<MyTelegram.Schema.Bots.RequestSetBotGroupDefaultAdminRights, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Bots.RequestSetBotGroupDefaultAdminRights obj)
    {
        var userReadModel = await queryProcessor.ProcessAsync(new GetUserByIdQuery(input.UserId));
        if (userReadModel == null || !userReadModel.Bot)
            RpcErrors.RpcErrors400.UserBotRequired.ThrowRpcError();

        var collection = mongoDatabase.GetCollection<BsonDocument>("bot_default_admin_rights");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("BotId", input.UserId),
            Builders<BsonDocument>.Filter.Eq("Type", "group")
        );

        var existingDoc = await collection.Find(filter).FirstOrDefaultAsync();

        var newRightsDoc = MyTelegram.Messenger.Services.Bots.BotDefaultAdminRights.ToDocument(obj.AdminRights);

        if (existingDoc != null && existingDoc.Contains("AdminRights"))
        {
            var oldRights = existingDoc["AdminRights"].AsBsonDocument;
            if (oldRights.Equals(newRightsDoc))
                RpcErrors.RpcErrors400.RightsNotModified.ThrowRpcError();
        }

        var update = Builders<BsonDocument>.Update
            .Set("BotId", input.UserId)
            .Set("Type", "group")
            .Set("AdminRights", newRightsDoc)
            .Set("UpdatedAt", DateTime.UtcNow);

        await collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });

        return new TBoolTrue();
    }
}