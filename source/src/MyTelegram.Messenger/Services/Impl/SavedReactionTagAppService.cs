using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Impl;

/// <summary>
/// MongoDB-backed store for saved message tags. One document per (user, reaction); the Count field
/// tracks how many Saved Messages currently carry that reaction.
/// See https://corefork.telegram.org/api/saved-messages#tags
/// </summary>
public class SavedReactionTagAppService(IMongoDatabase database) : ISavedReactionTagAppService, ITransientDependency
{
    private const string CollectionName = "saved_reaction_tags";

    private IMongoCollection<BsonDocument> Collection => database.GetCollection<BsonDocument>(CollectionName);

    public async Task<List<SavedReactionTagItem>> GetTagsAsync(long userId)
    {
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("UserId", userId),
            Builders<BsonDocument>.Filter.Gt("Count", 0));
        var sort = Builders<BsonDocument>.Sort.Descending("Count");
        var docs = await Collection.Find(filter).Sort(sort).ToListAsync();

        var tags = new List<SavedReactionTagItem>(docs.Count);
        foreach (var doc in docs)
        {
            var reaction = ToReaction(doc);
            if (reaction == null)
            {
                continue;
            }

            var title = doc.TryGetValue("Title", out var titleValue) && titleValue.IsString
                ? titleValue.AsString
                : null;
            tags.Add(new SavedReactionTagItem(reaction, string.IsNullOrEmpty(title) ? null : title,
                GetInt32(doc, "Count")));
        }

        return tags;
    }

    public async Task SetTitleAsync(long userId, IReaction reaction, string? title)
    {
        var reactionId = reaction.GetReactionId();
        var update = Builders<BsonDocument>.Update
            .SetOnInsert("UserId", userId)
            .SetOnInsert("ReactionId", reactionId)
            .SetOnInsert("Count", 0)
            .Set("Type", reaction is TReactionCustomEmoji ? "custom" : "emoji")
            .Set("Emoticon", GetEmoticonValue(reaction))
            .Set("DocumentId", GetDocumentIdValue(reaction))
            .Set("Title", string.IsNullOrEmpty(title) ? BsonNull.Value : (BsonValue)title);

        await Collection.UpdateOneAsync(GetIdFilter(userId, reactionId), update, new UpdateOptions { IsUpsert = true });
    }

    public async Task UpdateTagCountsAsync(long userId, List<IReaction> removedReactions, List<IReaction> addedReactions)
    {
        var removedIds = removedReactions.Select(r => r.GetReactionId()).ToHashSet();
        var addedIds = addedReactions.Select(r => r.GetReactionId()).ToHashSet();

        // Reactions kept across the edit must not double-count.
        foreach (var reaction in addedReactions.Where(r => !removedIds.Contains(r.GetReactionId())))
        {
            await IncrementAsync(userId, reaction, 1);
        }

        foreach (var reaction in removedReactions.Where(r => !addedIds.Contains(r.GetReactionId())))
        {
            await IncrementAsync(userId, reaction, -1);
        }
    }

    private async Task IncrementAsync(long userId, IReaction reaction, int delta)
    {
        var reactionId = reaction.GetReactionId();
        var update = Builders<BsonDocument>.Update
            .SetOnInsert("UserId", userId)
            .SetOnInsert("ReactionId", reactionId)
            .Set("Type", reaction is TReactionCustomEmoji ? "custom" : "emoji")
            .Set("Emoticon", GetEmoticonValue(reaction))
            .Set("DocumentId", GetDocumentIdValue(reaction))
            .Inc("Count", delta);

        await Collection.UpdateOneAsync(GetIdFilter(userId, reactionId), update, new UpdateOptions { IsUpsert = true });

        if (delta < 0)
        {
            // Guard against drift making the counter go negative.
            await Collection.UpdateOneAsync(
                Builders<BsonDocument>.Filter.And(
                    GetIdFilter(userId, reactionId),
                    Builders<BsonDocument>.Filter.Lt("Count", 0)),
                Builders<BsonDocument>.Update.Set("Count", 0));
        }
    }

    private static FilterDefinition<BsonDocument> GetIdFilter(long userId, long reactionId)
    {
        return Builders<BsonDocument>.Filter.Eq("_id", $"tag-{userId}-{reactionId}");
    }

    private static BsonValue GetEmoticonValue(IReaction reaction)
    {
        return reaction is TReactionEmoji emoji ? emoji.Emoticon : BsonNull.Value;
    }

    private static BsonValue GetDocumentIdValue(IReaction reaction)
    {
        return reaction is TReactionCustomEmoji custom ? custom.DocumentId : BsonNull.Value;
    }

    private static IReaction? ToReaction(BsonDocument doc)
    {
        var type = doc.TryGetValue("Type", out var typeValue) && typeValue.IsString ? typeValue.AsString : "emoji";
        if (type == "custom")
        {
            return doc.TryGetValue("DocumentId", out var documentId) && !documentId.IsBsonNull
                ? new TReactionCustomEmoji { DocumentId = GetInt64(documentId) }
                : null;
        }

        return doc.TryGetValue("Emoticon", out var emoticon) && emoticon.IsString && emoticon.AsString.Length > 0
            ? new TReactionEmoji { Emoticon = emoticon.AsString }
            : null;
    }

    private static int GetInt32(BsonDocument doc, string name)
    {
        return doc.TryGetValue(name, out var value)
            ? value.BsonType switch
            {
                BsonType.Int32 => value.AsInt32,
                BsonType.Int64 => (int)value.AsInt64,
                BsonType.Double => (int)value.AsDouble,
                _ => 0
            }
            : 0;
    }

    private static long GetInt64(BsonValue value)
    {
        return value.BsonType switch
        {
            BsonType.Int64 => value.AsInt64,
            BsonType.Int32 => value.AsInt32,
            BsonType.Double => (long)value.AsDouble,
            _ => 0
        };
    }
}
