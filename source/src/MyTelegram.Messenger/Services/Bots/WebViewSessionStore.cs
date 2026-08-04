using MongoDB.Bson;
using MongoDB.Driver;
using Microsoft.Extensions.Options;

namespace MyTelegram.Messenger.Services.Bots;

/// <summary>
/// Tracks open mini app (webview) sessions and resolves the URL a client should load.
/// </summary>
/// <remarks>
/// The mini app itself is an ordinary HTTPS page hosted outside this server; the server's job is
/// to validate the request, hand back a URL and keep a session alive while the view is open.
/// See https://corefork.telegram.org/api/bots/webapps .
/// </remarks>
public interface IWebViewSessionStore
{
    /// <summary>Opens a session and returns the query id the client echoes back when prolonging.</summary>
    Task<long> CreateSessionAsync(long botId, long userId, Peer peer, string url);

    /// <summary>
    /// Extends a session's deadline. Returns false when the id is unknown or already expired, which
    /// the caller surfaces as QUERY_ID_INVALID.
    /// </summary>
    Task<bool> ProlongSessionAsync(long queryId, long userId);

    /// <summary>
    /// Resolves the mini app URL for a bot: an explicitly configured URL if the bot has one,
    /// otherwise a per-bot path under the configured base URL.
    /// </summary>
    Task<string> ResolveBotUrlAsync(long botId, string? requestedUrl = null, string? shortName = null);
}

public class WebViewSessionStore(
    IMongoDatabase mongoDatabase,
    IOptions<MyTelegramMessengerServerOptions> options) : IWebViewSessionStore, ISingletonDependency
{
    private const string CollectionName = "web_view_sessions";

    public async Task<long> CreateSessionAsync(long botId, long userId, Peer peer, string url)
    {
        var queryId = Random.Shared.NextInt64();
        var timeout = options.Value.WebApps.SessionTimeoutSeconds;

        await mongoDatabase.GetCollection<BsonDocument>(CollectionName).InsertOneAsync(new BsonDocument
        {
            ["_id"] = $"webview-{queryId}",
            ["query_id"] = queryId,
            ["bot_id"] = botId,
            ["user_id"] = userId,
            ["peer_id"] = peer.PeerId,
            ["peer_type"] = (int)peer.PeerType,
            ["url"] = url,
            ["created_at"] = DateTime.UtcNow.ToTimestamp(),
            ["expires_at"] = DateTime.UtcNow.AddSeconds(timeout).ToTimestamp()
        });

        return queryId;
    }

    public async Task<bool> ProlongSessionAsync(long queryId, long userId)
    {
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("query_id", queryId),
            Builders<BsonDocument>.Filter.Eq("user_id", userId));

        var collection = mongoDatabase.GetCollection<BsonDocument>(CollectionName);
        var session = await collection.Find(filter).FirstOrDefaultAsync();
        if (session == null)
        {
            return false;
        }

        var now = DateTime.UtcNow.ToTimestamp();
        if (GetInt32(session, "expires_at") < now)
        {
            await collection.DeleteOneAsync(filter);
            return false;
        }

        await collection.UpdateOneAsync(filter, Builders<BsonDocument>.Update
            .Set("expires_at", DateTime.UtcNow.AddSeconds(options.Value.WebApps.SessionTimeoutSeconds).ToTimestamp()));

        return true;
    }

    public async Task<string> ResolveBotUrlAsync(long botId, string? requestedUrl = null, string? shortName = null)
    {
        if (!string.IsNullOrEmpty(requestedUrl))
        {
            return requestedUrl;
        }

        // A bot may have a main mini app URL configured through bots.setBotInfo-style state.
        var botApp = await mongoDatabase.GetCollection<BsonDocument>("bot_apps")
            .Find(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("bot_id", botId),
                shortName == null
                    ? Builders<BsonDocument>.Filter.Empty
                    : Builders<BsonDocument>.Filter.Eq("short_name", shortName)))
            .FirstOrDefaultAsync();

        if (botApp != null && botApp.TryGetValue("url", out var urlValue) && urlValue.IsString &&
            !string.IsNullOrEmpty(urlValue.AsString))
        {
            return urlValue.AsString;
        }

        var baseUrl = options.Value.WebApps.BaseUrl.TrimEnd('/');
        return shortName == null
            ? $"{baseUrl}/webapp/{botId}"
            : $"{baseUrl}/webapp/{botId}/{shortName}";
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
