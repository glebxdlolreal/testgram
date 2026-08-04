using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Bots;

/// <summary>
/// Resolved <c>botApp</c> record: the app itself plus the bot that owns it.
/// </summary>
public readonly record struct BotAppLookup(long BotId, BsonDocument Document);

/// <summary>
/// Looks up direct-link mini apps (<c>bots/webapps#direct-link-mini-apps</c>) by short name or id.
/// </summary>
public interface IBotAppStore
{
    /// <summary>
    /// Resolves an <c>inputBotApp</c>. Returns null when no such app exists.
    /// </summary>
    /// <param name="resolveBotId">
    /// Resolves the bot id behind an <c>inputUser</c>, so this store stays free of access-hash logic.
    /// </param>
    Task<BotAppLookup?> ResolveAsync(IInputBotApp app, Func<IInputUser, long?> resolveBotId);

    /// <summary>Builds the TL <c>botApp</c> for a stored record.</summary>
    MyTelegram.Schema.IBotApp ToBotApp(BsonDocument document);
}

public class BotAppStore(IMongoDatabase mongoDatabase) : IBotAppStore, ISingletonDependency
{
    private const string CollectionName = "bot_apps";

    public async Task<BotAppLookup?> ResolveAsync(IInputBotApp app, Func<IInputUser, long?> resolveBotId)
    {
        var collection = mongoDatabase.GetCollection<BsonDocument>(CollectionName);

        switch (app)
        {
            case TInputBotAppShortName shortName:
            {
                var botId = resolveBotId(shortName.BotId);
                if (botId == null)
                {
                    return null;
                }

                var doc = await collection.Find(Builders<BsonDocument>.Filter.And(
                        Builders<BsonDocument>.Filter.Eq("bot_id", botId.Value),
                        Builders<BsonDocument>.Filter.Eq("short_name", shortName.ShortName)))
                    .FirstOrDefaultAsync();

                return doc == null ? null : new BotAppLookup(botId.Value, doc);
            }

            case TInputBotAppID id:
            {
                var doc = await collection.Find(Builders<BsonDocument>.Filter.And(
                        Builders<BsonDocument>.Filter.Eq("app_id", id.Id),
                        Builders<BsonDocument>.Filter.Eq("access_hash", id.AccessHash)))
                    .FirstOrDefaultAsync();

                return doc == null ? null : new BotAppLookup(GetInt64(doc, "bot_id"), doc);
            }

            default:
                return null;
        }
    }

    public MyTelegram.Schema.IBotApp ToBotApp(BsonDocument document)
    {
        return new MyTelegram.Schema.TBotApp
        {
            Id = GetInt64(document, "app_id"),
            AccessHash = GetInt64(document, "access_hash"),
            ShortName = GetString(document, "short_name"),
            Title = GetString(document, "title"),
            Description = GetString(document, "description"),
            // Mini app icons are optional; an empty photo keeps clients from dereferencing null.
            Photo = new TPhotoEmpty(),
            Hash = GetInt64(document, "hash")
        };
    }

    private static string GetString(BsonDocument doc, string name)
    {
        return doc.TryGetValue(name, out var value) && value.IsString ? value.AsString : string.Empty;
    }

    private static long GetInt64(BsonDocument doc, string name)
    {
        if (!doc.TryGetValue(name, out var value))
        {
            return 0;
        }

        return value.BsonType switch
        {
            BsonType.Int32 => value.AsInt32,
            BsonType.Int64 => value.AsInt64,
            BsonType.Double => (long)value.AsDouble,
            _ => 0
        };
    }
}
