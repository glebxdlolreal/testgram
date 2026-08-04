using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Bots;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Answer an inline query, for bots only
/// Possible errors
/// Code Type Description
/// 400 NEXT_OFFSET_INVALID The specified offset is longer than 64 bytes.
/// 400 QUERY_ID_INVALID The query ID is invalid.
/// 400 RESULTS_TOO_MUCH Too many results were provided.
/// 400 RESULT_ID_DUPLICATE You provided a duplicate result ID.
/// 400 SWITCH_PM_TEXT_EMPTY The switch_pm.text field was empty.
/// 400 SWITCH_WEBVIEW_URL_INVALID The URL specified in switch_webview.url is invalid!
/// 400 USER_BOT_REQUIRED This method can only be called by a bot.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.setInlineBotResults"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✖] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class SetInlineBotResultsHandler(
    IMongoDatabase mongoDatabase,
    IQueryProcessor queryProcessor,
    ILogger<SetInlineBotResultsHandler> logger) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestSetInlineBotResults, IBool>
{
    private const string PendingCollection = "pending_inline_queries";
    private const string ResultsCollection = "inline_bot_results";

    /// <summary>Upstream caps a single answer at 50 results.</summary>
    private const int MaxResults = 50;

    /// <summary>next_offset is limited to 64 bytes by the API.</summary>
    private const int MaxNextOffsetLength = 64;

    /// <summary>How long a stored answer stays available for messages.sendInlineBotResult.</summary>
    private static readonly TimeSpan ResultsRetention = TimeSpan.FromHours(1);

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestSetInlineBotResults obj)
    {
        var botReadModel = await queryProcessor.ProcessAsync(new GetUserByIdQuery(input.UserId));
        if (botReadModel == null || !botReadModel.Bot)
        {
            RpcErrors.RpcErrors400.UserBotRequired.ThrowRpcError();
        }

        if (obj.Results.Count > MaxResults)
        {
            RpcErrors.RpcErrors400.ResultsTooMuch.ThrowRpcError();
        }

        if (obj.NextOffset is { Length: > MaxNextOffsetLength })
        {
            RpcErrors.RpcErrors400.NextOffsetInvalid.ThrowRpcError();
        }

        if (obj.SwitchPm is TInlineBotSwitchPM switchPm && string.IsNullOrEmpty(switchPm.Text))
        {
            RpcErrors.RpcErrors400.SwitchPmTextEmpty.ThrowRpcError();
        }

        if (obj.SwitchWebview is TInlineBotWebView switchWebview &&
            !Uri.TryCreate(switchWebview.Url, UriKind.Absolute, out _))
        {
            RpcErrors.RpcErrors400.SwitchWebviewUrlInvalid.ThrowRpcError();
        }

        var pendingCollection = mongoDatabase.GetCollection<BsonDocument>(PendingCollection);
        var pendingFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("query_id", obj.QueryId),
            Builders<BsonDocument>.Filter.Eq("bot_id", input.UserId));

        var pending = await pendingCollection.Find(pendingFilter).FirstOrDefaultAsync();
        if (pending == null)
        {
            // The user already timed out, or this query id was never issued to this bot.
            logger.LogWarning("Inline query not found: queryId={QueryId} botId={BotId}", obj.QueryId, input.UserId);
            RpcErrors.RpcErrors400.QueryIdInvalid.ThrowRpcError();
        }

        var converted = ConvertResults(obj.Results);

        await mongoDatabase.GetCollection<BsonDocument>(ResultsCollection).ReplaceOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", $"inline-results-{obj.QueryId}"),
            new BsonDocument
            {
                ["_id"] = $"inline-results-{obj.QueryId}",
                ["query_id"] = obj.QueryId,
                ["bot_id"] = input.UserId,
                ["user_id"] = pending!.TryGetValue("user_id", out var userIdValue) ? userIdValue : BsonNull.Value,
                ["query"] = pending.TryGetValue("query", out var queryValue) ? queryValue : string.Empty,
                ["gallery"] = obj.Gallery,
                ["private"] = obj.Private,
                ["cache_time"] = obj.CacheTime,
                ["next_offset"] = obj.NextOffset ?? string.Empty,
                ["switch_pm"] = obj.SwitchPm?.ToBytes() ?? Array.Empty<byte>(),
                ["switch_webview"] = obj.SwitchWebview?.ToBytes() ?? Array.Empty<byte>(),
                ["results"] = converted.ToBytes(),
                // Raw input results are kept so sendInlineBotResult can rebuild the outgoing message.
                ["input_results"] = obj.Results.ToBytes(),
                ["expires_at"] = DateTime.UtcNow.Add(ResultsRetention).ToTimestamp()
            },
            new ReplaceOptions { IsUpsert = true });

        await pendingCollection.UpdateOneAsync(pendingFilter, Builders<BsonDocument>.Update
            .Set("success", true)
            .Set("error", string.Empty)
            .Set("responded_at", DateTime.UtcNow.ToTimestamp()));

        return new TBoolTrue();
    }

    /// <summary>
    /// Maps the bot's input results to their client-facing shape. Results whose type is not
    /// recognised are dropped rather than aborting the whole answer.
    /// </summary>
    private static TVector<IBotInlineResult> ConvertResults(TVector<IInputBotInlineResult> results)
    {
        var converted = new TVector<IBotInlineResult>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var result in results)
        {
            var botInlineResult = InlineResultConverter.ToBotInlineResult(result);
            if (botInlineResult == null)
            {
                continue;
            }

            var id = botInlineResult switch
            {
                TBotInlineResult r => r.Id,
                TBotInlineMediaResult r => r.Id,
                _ => null
            };

            if (id == null)
            {
                continue;
            }

            if (!seenIds.Add(id))
            {
                RpcErrors.RpcErrors400.ResultIdDuplicate.ThrowRpcError();
            }

            converted.Add(botInlineResult);
        }

        return converted;
    }
}
