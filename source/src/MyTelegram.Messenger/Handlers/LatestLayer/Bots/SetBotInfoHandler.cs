using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Bots;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Bots;
/// <summary>
/// Set localized name, about text and description of a bot (or of the current account, if called by a bot).
/// Possible errors
/// Code Type Description
/// 400 BOT_INVALID This is not a valid bot.
/// 400 USER_BOT_INVALID User accounts must provide the <code>bot</code> method parameter when calling this method. If there is no such method parameter, this method can only be invoked by bot accounts.
/// <para><c>See <a href="https://corefork.telegram.org/method/bots.setBotInfo"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class SetBotInfoHandler(
    IMongoDatabase mongoDatabase,
    IQueryProcessor queryProcessor,
    IBotOwnershipChecker botOwnershipChecker,
    IUserAppService userAppService,
    IEventBus eventBus) : RpcResultObjectHandler<MyTelegram.Schema.Bots.RequestSetBotInfo, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Bots.RequestSetBotInfo obj)
    {
        long targetBotId;

        if (obj.Bot != null)
        {
            if (obj.Bot is TInputUser inputUser)
            {
                targetBotId = inputUser.UserId;
                var targetBot = await queryProcessor.ProcessAsync(new GetUserByIdQuery(targetBotId));
                if (targetBot == null || !targetBot.Bot)
                    RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();

                // Being a bot is not enough: without an ownership check any user could rewrite
                // the public name/about/description of any bot on the server.
                if (targetBotId != input.UserId && !await botOwnershipChecker.IsOwnerAsync(targetBotId, input.UserId))
                    RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
            }
            else
            {
                RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
                return null!;
            }
        }
        else
        {
            var currentUser = await queryProcessor.ProcessAsync(new GetUserByIdQuery(input.UserId));
            if (currentUser == null || !currentUser.Bot)
                RpcErrors.RpcErrors400.UserBotInvalid.ThrowRpcError();
            targetBotId = input.UserId;
        }

        if (!string.IsNullOrEmpty(obj.LangCode) && obj.LangCode.Length > 10)
            RpcErrors.RpcErrors400.LangCodeInvalid.ThrowRpcError();

        var collection = mongoDatabase.GetCollection<BsonDocument>("bot_info");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("BotId", targetBotId),
            Builders<BsonDocument>.Filter.Eq("LangCode", obj.LangCode ?? "")
        );

        var update = Builders<BsonDocument>.Update
            .Set("BotId", targetBotId)
            .Set("LangCode", obj.LangCode ?? "")
            .Set("UpdatedAt", DateTime.UtcNow);

        if (obj.Name != null)
            update = update.Set("Name", obj.Name);

        if (obj.About != null)
            update = update.Set("About", obj.About);

        if (obj.Description != null)
            update = update.Set("Description", obj.Description);

        await collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });

        if (string.IsNullOrEmpty(obj.LangCode) && (obj.Name != null || obj.About != null))
        {
            await ApplyToProfileAsync(targetBotId, obj.Name, obj.About);
        }

        return new TBoolTrue();
    }

    /// <summary>
    /// The default-language name and about text are the bot's profile: <c>user.first_name</c> and
    /// <c>userFull.about</c> come from the user read model, not from <c>bot_info</c>. Bots made through
    /// BotFather have no aggregate behind them, so they are written where BotFather writes them.
    /// </summary>
    private async Task ApplyToProfileAsync(long botId, string? name, string? about)
    {
        var state = mongoDatabase.GetCollection<BsonDocument>("botfather-bot-state");
        if (!await state.Find(Builders<BsonDocument>.Filter.Eq("BotUserId", botId)).AnyAsync())
        {
            return;
        }

        var userUpdate = new List<UpdateDefinition<BsonDocument>>();
        if (!string.IsNullOrEmpty(name))
        {
            userUpdate.Add(Builders<BsonDocument>.Update.Set("FirstName", name));
            await state.UpdateOneAsync(Builders<BsonDocument>.Filter.Eq("BotUserId", botId),
                Builders<BsonDocument>.Update.Set("Name", name));
        }

        if (about != null)
        {
            userUpdate.Add(Builders<BsonDocument>.Update.Set("About", about));
        }

        if (userUpdate.Count == 0)
        {
            return;
        }

        await mongoDatabase.GetCollection<BsonDocument>("eventflow-userreadmodel").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("UserId", botId), Builders<BsonDocument>.Update.Combine(userUpdate));
        userAppService.InvalidateCache(botId);
        await eventBus.PublishAsync(new BotUserChangedEvent(botId));
    }
}