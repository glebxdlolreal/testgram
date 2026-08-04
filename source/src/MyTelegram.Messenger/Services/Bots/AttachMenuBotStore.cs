using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Bots;

/// <summary>
/// Tracks which bots a user has added to their attachment / side menu.
/// See https://corefork.telegram.org/api/bots/attach .
/// </summary>
public interface IAttachMenuBotStore
{
    /// <summary>Adds or removes a bot from a user's attachment menu.</summary>
    Task SetEnabledAsync(long userId, long botId, bool enabled, bool writeAllowed);

    /// <summary>Returns the bots a user has enabled, oldest first.</summary>
    Task<List<BsonDocument>> GetEnabledAsync(long userId);

    /// <summary>Returns a single enabled entry, or null when the bot was never added.</summary>
    Task<BsonDocument?> GetAsync(long userId, long botId);

    /// <summary>
    /// Builds the TL <c>attachMenuBot</c>. Icons are required by the constructor but are supplied
    /// by the bot owner; an empty list is returned when none were configured.
    /// </summary>
    IAttachMenuBot ToAttachMenuBot(long botId, string shortName, BsonDocument? document);

    /// <summary>
    /// Hash over the enabled bot ids, used by clients to skip unchanged responses. Computed with
    /// the algorithm from https://corefork.telegram.org/api/offsets#hash-generation .
    /// </summary>
    long ComputeHash(IEnumerable<long> botIds);
}

public class AttachMenuBotStore(IMongoDatabase mongoDatabase) : IAttachMenuBotStore, ISingletonDependency
{
    private const string CollectionName = "attach_menu_bots";

    public async Task SetEnabledAsync(long userId, long botId, bool enabled, bool writeAllowed)
    {
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("user_id", userId),
            Builders<BsonDocument>.Filter.Eq("bot_id", botId));

        var collection = mongoDatabase.GetCollection<BsonDocument>(CollectionName);

        if (!enabled)
        {
            await collection.DeleteOneAsync(filter);
            return;
        }

        await collection.UpdateOneAsync(
            filter,
            Builders<BsonDocument>.Update
                .SetOnInsert("user_id", userId)
                .SetOnInsert("bot_id", botId)
                .SetOnInsert("added_at", DateTime.UtcNow.ToTimestamp())
                .Set("write_allowed", writeAllowed),
            new UpdateOptions { IsUpsert = true });
    }

    public async Task<List<BsonDocument>> GetEnabledAsync(long userId)
    {
        return await mongoDatabase.GetCollection<BsonDocument>(CollectionName)
            .Find(Builders<BsonDocument>.Filter.Eq("user_id", userId))
            .Sort(Builders<BsonDocument>.Sort.Ascending("added_at"))
            .ToListAsync();
    }

    public async Task<BsonDocument?> GetAsync(long userId, long botId)
    {
        return await mongoDatabase.GetCollection<BsonDocument>(CollectionName)
            .Find(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("user_id", userId),
                Builders<BsonDocument>.Filter.Eq("bot_id", botId)))
            .FirstOrDefaultAsync();
    }

    public IAttachMenuBot ToAttachMenuBot(long botId, string shortName, BsonDocument? document)
    {
        return new TAttachMenuBot
        {
            BotId = botId,
            ShortName = shortName,
            // No stored entry means the bot is offered but not added yet.
            Inactive = document == null,
            RequestWriteAccess = document == null ||
                                 !(document.TryGetValue("write_allowed", out var allowed) && allowed.IsBoolean &&
                                   allowed.AsBoolean),
            ShowInAttachMenu = true,
            Icons = new TVector<IAttachMenuBotIcon>(),
            PeerTypes = new TVector<IAttachMenuPeerType>
            {
                new TAttachMenuPeerTypeSameBotPM(),
                new TAttachMenuPeerTypeBotPM(),
                new TAttachMenuPeerTypePM(),
                new TAttachMenuPeerTypeChat(),
                new TAttachMenuPeerTypeBroadcast()
            }
        };
    }

    public long ComputeHash(IEnumerable<long> botIds)
    {
        var hash = 0L;
        foreach (var botId in botIds)
        {
            hash ^= hash >> 21;
            hash ^= hash << 35;
            hash ^= hash >> 4;
            hash += botId;
        }

        return hash;
    }
}
