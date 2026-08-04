using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Set the callback answer to a user button press (bots only)
/// Possible errors
/// Code Type Description
/// 400 MESSAGE_TOO_LONG The provided message is too long.
/// 400 QUERY_ID_INVALID The query ID is invalid.
/// 400 URL_INVALID Invalid URL provided.
/// 400 USER_BOT_REQUIRED This method can only be called by a bot.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.setBotCallbackAnswer"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✖] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class SetBotCallbackAnswerHandler(
    IMongoDatabase mongoDatabase,
    IQueryProcessor queryProcessor,
    ILogger<SetBotCallbackAnswerHandler> logger) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestSetBotCallbackAnswer, IBool>
{
    private const string PendingCollection = "pending_callback_queries";

    /// <summary>Callback toasts are short; anything longer is rejected like upstream does.</summary>
    private const int MaxMessageLength = 200;

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestSetBotCallbackAnswer obj)
    {
        var botReadModel = await queryProcessor.ProcessAsync(new GetUserByIdQuery(input.UserId));
        if (botReadModel == null || !botReadModel.Bot)
        {
            RpcErrors.RpcErrors400.UserBotRequired.ThrowRpcError();
        }

        if (obj.Message is { Length: > MaxMessageLength })
        {
            RpcErrors.RpcErrors400.MessageTooLong.ThrowRpcError();
        }

        if (!string.IsNullOrEmpty(obj.Url) && !Uri.TryCreate(obj.Url, UriKind.Absolute, out _))
        {
            RpcErrors.RpcErrors400.UrlInvalid.ThrowRpcError();
        }

        var collection = mongoDatabase.GetCollection<BsonDocument>(PendingCollection);
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("query_id", obj.QueryId),
            Builders<BsonDocument>.Filter.Eq("bot_id", input.UserId));

        var update = Builders<BsonDocument>.Update
            .Set("success", true)
            .Set("error", string.Empty)
            .Set("alert", obj.Alert)
            .Set("message", obj.Message ?? string.Empty)
            .Set("url", obj.Url ?? string.Empty)
            .Set("cache_time", obj.CacheTime)
            .Set("responded_at", DateTime.UtcNow.ToTimestamp());

        var result = await collection.UpdateOneAsync(filter, update);
        if (result.MatchedCount == 0)
        {
            // Either the user already gave up (BOT_RESPONSE_TIMEOUT) or the id was never issued.
            logger.LogWarning("Callback query not found: queryId={QueryId} botId={BotId}", obj.QueryId, input.UserId);
            RpcErrors.RpcErrors400.QueryIdInvalid.ThrowRpcError();
        }

        return new TBoolTrue();
    }
}
