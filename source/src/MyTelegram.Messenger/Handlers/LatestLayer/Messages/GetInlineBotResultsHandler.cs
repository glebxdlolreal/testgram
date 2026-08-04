using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Bots;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Query an inline bot
/// Possible errors
/// Code Type Description
/// 400 BOT_INLINE_DISABLED This bot can't be used in inline mode.
/// 400 BOT_INVALID This is not a valid bot.
/// 400 BOT_RESPONSE_TIMEOUT A timeout occurred while fetching data from the bot.
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 406 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 400 INPUT_USER_DEACTIVATED The specified user was deleted.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// -503 Timeout Timeout while fetching data.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getInlineBotResults"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetInlineBotResultsHandler(
    IMongoDatabase mongoDatabase,
    IQueryProcessor queryProcessor,
    IPeerHelper peerHelper,
    IAccessHashHelper accessHashHelper,
    IUserAppService userAppService,
    IBotUpdatesSender botUpdatesSender,
    IUserConverterService userConverterService) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetInlineBotResults, MyTelegram.Schema.Messages.IBotResults>
{
    private const string PendingCollection = "pending_inline_queries";
    private const string ResultsCollection = "inline_bot_results";

    protected override async Task<MyTelegram.Schema.Messages.IBotResults> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetInlineBotResults obj)
    {
        if (obj.Bot is not TInputUser inputBot)
        {
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
            return null!;
        }

        await accessHashHelper.CheckAccessHashAsync(input, inputBot.UserId, inputBot.AccessHash);

        var botReadModel = await queryProcessor.ProcessAsync(new GetUserByIdQuery(inputBot.UserId));
        if (botReadModel == null || !botReadModel.Bot)
        {
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
        }

        var botState = await mongoDatabase.GetCollection<BsonDocument>("botfather-bot-state")
            .Find(Builders<BsonDocument>.Filter.Eq("BotUserId", inputBot.UserId))
            .FirstOrDefaultAsync();

        // Inline mode is opt-in per bot (BotFather /setinline), mirroring upstream behaviour.
        var inlineEnabled = botState != null && botState.TryGetValue("InlineEnabled", out var enabledValue) &&
                            enabledValue.IsBoolean && enabledValue.AsBoolean;
        if (!inlineEnabled)
        {
            RpcErrors.RpcErrors400.BotInlineDisabled.ThrowRpcError();
        }

        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        var queryId = Random.Shared.NextInt64();

        var update = new TUpdateBotInlineQuery
        {
            QueryId = queryId,
            UserId = input.UserId,
            Query = obj.Query,
            Offset = obj.Offset,
            Geo = ToGeoPoint(obj.GeoPoint),
            PeerType = ResolvePeerType(peer, inputBot.UserId, input.UserId)
        };

        var result = await botUpdatesSender.SendQueryAndWaitAsync(
            PendingCollection,
            queryId,
            inputBot.UserId,
            update,
            new BsonDocument
            {
                ["user_id"] = input.UserId,
                ["query"] = obj.Query,
                ["offset"] = obj.Offset
            });

        if (!result.Success)
        {
            throw new RpcException(new RpcError(400,
                result.Error ?? RpcErrors.RpcErrors400.BotResponseTimeout.Message));
        }

        return await BuildResultsAsync(input, queryId, inputBot.UserId);
    }

    /// <summary>
    /// Reads back the results the bot stored via messages.setInlineBotResults.
    /// </summary>
    private async Task<MyTelegram.Schema.Messages.IBotResults> BuildResultsAsync(IRequestInput input, long queryId, long botId)
    {
        var stored = await mongoDatabase.GetCollection<BsonDocument>(ResultsCollection)
            .Find(Builders<BsonDocument>.Filter.Eq("query_id", queryId))
            .FirstOrDefaultAsync();

        var results = new TVector<IBotInlineResult>();
        var gallery = false;
        var cacheTime = 0;
        string? nextOffset = null;
        IInlineBotSwitchPM? switchPm = null;
        IInlineBotWebView? switchWebview = null;

        if (stored != null)
        {
            gallery = stored.TryGetValue("gallery", out var galleryValue) && galleryValue.IsBoolean &&
                      galleryValue.AsBoolean;
            cacheTime = GetInt32(stored, "cache_time");

            if (stored.TryGetValue("next_offset", out var offsetValue) && offsetValue.IsString &&
                !string.IsNullOrEmpty(offsetValue.AsString))
            {
                nextOffset = offsetValue.AsString;
            }

            switchPm = ReadObject<TInlineBotSwitchPM>(stored, "switch_pm");
            switchWebview = ReadObject<TInlineBotWebView>(stored, "switch_webview");

            var resultsVector = ReadObject<TVector<IBotInlineResult>>(stored, "results");
            if (resultsVector != null)
            {
                results = resultsVector;
            }
        }

        var users = new TVector<IUser>();
        var botUser = await userAppService.GetAsync(botId);
        if (botUser != null)
        {
            users.Add(userConverterService.ToUser(input, botUser, layer: input.Layer));
        }

        return new MyTelegram.Schema.Messages.TBotResults
        {
            Gallery = gallery,
            QueryId = queryId,
            NextOffset = nextOffset,
            SwitchPm = switchPm,
            SwitchWebview = switchWebview,
            Results = results,
            CacheTime = cacheTime,
            Users = users
        };
    }

    /// <summary>
    /// Tells the bot what kind of chat the query was typed in, so it can tailor its results.
    /// </summary>
    private static IInlineQueryPeerType? ResolvePeerType(Peer peer, long botId, long userId)
    {
        switch (peer.PeerType)
        {
            case PeerType.Channel:
                // Broadcast vs megagroup is not distinguishable from the peer alone here; the
                // megagroup case is the one bots care about for permissions, so report it.
                return new TInlineQueryPeerTypeMegagroup();
            case PeerType.Chat:
                return new TInlineQueryPeerTypeChat();
            case PeerType.User when peer.PeerId == botId:
                return new TInlineQueryPeerTypeSameBotPM();
            case PeerType.User when peer.PeerId == userId:
                return new TInlineQueryPeerTypePM();
            default:
                return new TInlineQueryPeerTypePM();
        }
    }

    private static IGeoPoint? ToGeoPoint(IInputGeoPoint? input)
    {
        if (input is not TInputGeoPoint point)
        {
            return null;
        }

        return new TGeoPoint
        {
            Lat = point.Lat,
            Long = point.Long,
            AccessHash = 0,
            AccuracyRadius = point.AccuracyRadius
        };
    }

    private static T? ReadObject<T>(BsonDocument doc, string name) where T : class, IObject
    {
        if (!doc.TryGetValue(name, out var value) || value.BsonType != BsonType.Binary)
        {
            return null;
        }

        var bytes = value.AsBsonBinaryData.Bytes;
        if (bytes.Length == 0)
        {
            return null;
        }

        var buffer = new ReadOnlyMemory<byte>(bytes);
        return buffer.Read<T>();
    }

    private static int GetInt32(BsonDocument doc, string name)
    {
        if (!doc.TryGetValue(name, out var value))
        {
            return 0;
        }

        return value.BsonType switch
        {
            BsonType.Int32 => value.AsInt32,
            BsonType.Int64 => (int)value.AsInt64,
            BsonType.Double => (int)value.AsDouble,
            _ => 0
        };
    }
}
