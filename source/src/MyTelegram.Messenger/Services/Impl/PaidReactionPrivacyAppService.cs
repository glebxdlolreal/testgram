using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Impl;

/// <summary>
/// MongoDB-backed store for paid reaction privacy. Two document shapes share one collection:
/// "prp-{userId}" holds the account-wide default and "prp-msg-{userId}-{peerId}-{msgId}" the
/// per-message override.
/// See https://corefork.telegram.org/api/reactions#paid-reactions
/// </summary>
public class PaidReactionPrivacyAppService(IMongoDatabase database)
    : IPaidReactionPrivacyAppService, ITransientDependency
{
    private const string CollectionName = "paid_reaction_privacy";

    private IMongoCollection<BsonDocument> Collection => database.GetCollection<BsonDocument>(CollectionName);

    public async Task<PaidReactionPrivacySetting> GetDefaultAsync(long userId)
    {
        var doc = await Collection.Find(Builders<BsonDocument>.Filter.Eq("_id", DefaultId(userId)))
            .FirstOrDefaultAsync();
        return ToSetting(doc);
    }

    public async Task<PaidReactionPrivacySetting> GetForMessageAsync(long userId, long peerId, int msgId)
    {
        var doc = await Collection.Find(Builders<BsonDocument>.Filter.Eq("_id", MessageId(userId, peerId, msgId)))
            .FirstOrDefaultAsync();
        return doc != null ? ToSetting(doc) : await GetDefaultAsync(userId);
    }

    public async Task SetForMessageAsync(long userId, long peerId, int msgId, PaidReactionPrivacySetting setting)
    {
        var update = Builders<BsonDocument>.Update
            .Set("UserId", userId)
            .Set("PeerId", setting.PeerId)
            .Set("Type", ToStorageValue(setting.Type));

        await Collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", MessageId(userId, peerId, msgId)),
            Builders<BsonDocument>.Update.Combine(update,
                Builders<BsonDocument>.Update.Set("MsgId", msgId).Set("ChatId", peerId)),
            new UpdateOptions { IsUpsert = true });

        // The most recent explicit choice also becomes the account-wide default, matching the
        // behaviour clients expect from messages.getPaidReactionPrivacy.
        await Collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", DefaultId(userId)),
            update,
            new UpdateOptions { IsUpsert = true });
    }

    private static string DefaultId(long userId) => $"prp-{userId}";

    private static string MessageId(long userId, long peerId, int msgId) => $"prp-msg-{userId}-{peerId}-{msgId}";

    private static string ToStorageValue(PaidReactionPrivacyType type)
    {
        return type switch
        {
            PaidReactionPrivacyType.Anonymous => "anonymous",
            PaidReactionPrivacyType.Peer => "peer",
            _ => "default"
        };
    }

    private static PaidReactionPrivacySetting ToSetting(BsonDocument? doc)
    {
        if (doc == null || !doc.TryGetValue("Type", out var typeValue) || !typeValue.IsString)
        {
            return new PaidReactionPrivacySetting(PaidReactionPrivacyType.Default);
        }

        var peerId = doc.TryGetValue("PeerId", out var peerValue)
            ? peerValue.BsonType switch
            {
                BsonType.Int64 => peerValue.AsInt64,
                BsonType.Int32 => peerValue.AsInt32,
                _ => 0L
            }
            : 0L;

        return typeValue.AsString switch
        {
            "anonymous" => new PaidReactionPrivacySetting(PaidReactionPrivacyType.Anonymous),
            "peer" when peerId != 0 => new PaidReactionPrivacySetting(PaidReactionPrivacyType.Peer, peerId),
            _ => new PaidReactionPrivacySetting(PaidReactionPrivacyType.Default)
        };
    }
}
