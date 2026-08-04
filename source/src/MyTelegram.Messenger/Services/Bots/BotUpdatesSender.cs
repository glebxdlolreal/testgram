using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Caching;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Services.Bots;

/// <summary>
/// Result of waiting for a bot to answer a server-initiated query.
/// </summary>
/// <param name="Success">True when the bot answered before the timeout elapsed.</param>
/// <param name="Error">RPC error string to surface to the caller when <paramref name="Success"/> is false.</param>
/// <param name="Document">The pending document as it looked when the bot answered; null on timeout.</param>
public readonly record struct BotQueryResult(bool Success, string? Error, BsonDocument? Document);

/// <summary>
/// Delivers bot facing updates (inline queries, callback queries, ...) to a bot's connected
/// sessions and, where the flow is request/response, waits for the bot to answer.
/// </summary>
public interface IBotUpdatesSender
{
    /// <summary>
    /// Pushes a single update to every active session of <paramref name="botId"/>.
    /// </summary>
    Task PushUpdateToBotAsync(long botId, IUpdate update, IList<IUser>? users = null, IList<IChat>? chats = null);

    /// <summary>
    /// Same as <see cref="PushUpdateToBotAsync(long, IUpdate, IList{IUser}, IList{IChat})"/>, but the
    /// update is built from the allocated qts. Use this for updates that carry a qts field of their
    /// own, such as updateBotMessageReaction.
    /// </summary>
    Task PushUpdateToBotAsync(long botId, Func<int, IUpdate> updateFactory, IList<IUser>? users = null,
        IList<IChat>? chats = null);

    /// <summary>
    /// Allocates the next qts value for <paramref name="botId"/>. Bot updates are qts sequenced
    /// so the bot can recover missed ones via updates.getDifference.
    /// </summary>
    Task<int> NextQtsAsync(long botId);

    /// <summary>
    /// Registers a pending query, pushes <paramref name="update"/> to the bot and polls until the
    /// bot answers or <paramref name="timeout"/> elapses. The pending document is always removed
    /// before returning, so callers never leak rows.
    /// </summary>
    /// <param name="collectionName">Mongo collection holding the pending queries.</param>
    /// <param name="queryId">Correlation id echoed back by the bot.</param>
    /// <param name="botId">Bot that should answer.</param>
    /// <param name="update">Update delivered to the bot.</param>
    /// <param name="extraFields">Additional fields stored on the pending document.</param>
    /// <param name="timeout">How long to wait; defaults to <see cref="DefaultTimeout"/>.</param>
    Task<BotQueryResult> SendQueryAndWaitAsync(
        string collectionName,
        long queryId,
        long botId,
        IUpdate update,
        BsonDocument? extraFields = null,
        TimeSpan? timeout = null);
}

public class BotUpdatesSender(
    IObjectMessageSender objectMessageSender,
    IMongoDatabase mongoDatabase,
    IPtsHelper ptsHelper,
    ILogger<BotUpdatesSender> logger) : IBotUpdatesSender, ISingletonDependency
{
    /// <summary>
    /// Telegram clients give a bot roughly this long to answer before showing BOT_RESPONSE_TIMEOUT.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    public async Task<int> NextQtsAsync(long botId)
    {
        var currentQts = (await ptsHelper.GetPtsForUserAsync(botId)).Qts;
        return await ptsHelper.IncrementQtsAsync(botId, currentQts);
    }

    public async Task PushUpdateToBotAsync(long botId, IUpdate update, IList<IUser>? users = null,
        IList<IChat>? chats = null)
    {
        var qts = await NextQtsAsync(botId);
        await PushAsync(botId, update, qts, users, chats);
    }

    public async Task PushUpdateToBotAsync(long botId, Func<int, IUpdate> updateFactory, IList<IUser>? users = null,
        IList<IChat>? chats = null)
    {
        var qts = await NextQtsAsync(botId);
        await PushAsync(botId, updateFactory(qts), qts, users, chats);
    }

    private async Task PushAsync(long botId, IUpdate update, int qts, IList<IUser>? users, IList<IChat>? chats)
    {
        var updates = new TUpdates
        {
            Updates = new TVector<IUpdate>(update),
            Users = users == null ? new TVector<IUser>() : new TVector<IUser>(users),
            Chats = chats == null ? new TVector<IChat>() : new TVector<IChat>(chats),
            Date = DateTime.UtcNow.ToTimestamp(),
            Seq = 0
        };

        await objectMessageSender.PushMessageToPeerAsync(new Peer(PeerType.User, botId), updates, qts: qts);
    }

    public async Task<BotQueryResult> SendQueryAndWaitAsync(
        string collectionName,
        long queryId,
        long botId,
        IUpdate update,
        BsonDocument? extraFields = null,
        TimeSpan? timeout = null)
    {
        var collection = mongoDatabase.GetCollection<BsonDocument>(collectionName);
        var filter = Builders<BsonDocument>.Filter.Eq("query_id", queryId);

        var pending = new BsonDocument
        {
            ["_id"] = $"{collectionName}-{queryId}",
            ["query_id"] = queryId,
            ["bot_id"] = botId,
            ["created_at"] = DateTime.UtcNow.ToTimestamp(),
            ["success"] = false,
            ["error"] = string.Empty,
            ["responded_at"] = 0
        };

        if (extraFields != null)
        {
            foreach (var element in extraFields)
            {
                pending[element.Name] = element.Value;
            }
        }

        await collection.InsertOneAsync(pending);

        try
        {
            await PushUpdateToBotAsync(botId, update);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to push query to bot: collection={Collection} queryId={QueryId} botId={BotId}",
                collectionName, queryId, botId);
            await collection.DeleteOneAsync(filter);
            return new BotQueryResult(false, RpcErrors.RpcErrors400.BotResponseTimeout.Message, null);
        }

        var deadline = DateTime.UtcNow.Add(timeout ?? DefaultTimeout);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollInterval);

            var doc = await collection.Find(filter).FirstOrDefaultAsync();
            if (doc == null)
            {
                // Someone else consumed or cleaned up the query; treat as no answer.
                break;
            }

            if (GetInt32(doc, "responded_at") <= 0)
            {
                continue;
            }

            await collection.DeleteOneAsync(filter);

            var success = doc.TryGetValue("success", out var successValue) && successValue.IsBoolean &&
                          successValue.AsBoolean;
            var error = doc.TryGetValue("error", out var errorValue) && errorValue.IsString &&
                        !string.IsNullOrEmpty(errorValue.AsString)
                ? errorValue.AsString
                : null;

            return new BotQueryResult(success, error, doc);
        }

        await collection.DeleteOneAsync(filter);
        logger.LogWarning("Bot did not answer in time: collection={Collection} queryId={QueryId} botId={BotId}",
            collectionName, queryId, botId);

        return new BotQueryResult(false, RpcErrors.RpcErrors400.BotResponseTimeout.Message, null);
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
