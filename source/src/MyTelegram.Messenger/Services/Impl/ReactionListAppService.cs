using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Impl;

/// <summary>
/// Reads the "reactions" collection. Documents are seeded by scripts/seed_reactions.py and carry
/// Reaction, Order, Inactive and Premium fields.
/// </summary>
public class ReactionListAppService(IMongoDatabase database) : IReactionListAppService, ITransientDependency
{
    private const string CollectionName = "reactions";

    public async Task<List<IReaction>> GetActiveEmojiReactionsAsync(int limit)
    {
        var collection = database.GetCollection<BsonDocument>(CollectionName);
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Ne("Inactive", true),
            Builders<BsonDocument>.Filter.Ne("Premium", true));
        var sort = Builders<BsonDocument>.Sort.Ascending("Order");

        var docs = await collection.Find(filter).Sort(sort).Limit(limit).ToListAsync();

        return docs
            .Where(d => d.TryGetValue("Reaction", out var r) && r.IsString && r.AsString.Length > 0)
            .Select(d => (IReaction)new TReactionEmoji { Emoticon = d["Reaction"].AsString })
            .ToList();
    }

    public async Task<bool> IsKnownEmoticonAsync(string emoticon)
    {
        var collection = database.GetCollection<BsonDocument>(CollectionName);
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("Reaction", emoticon),
            Builders<BsonDocument>.Filter.Ne("Inactive", true));

        return await collection.Find(filter).AnyAsync();
    }
}
