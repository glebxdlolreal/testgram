using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Boosts;

/// <summary>
/// Hands out boost slots in <c>channel_boosts</c>, the collection <c>premium.getMyBoosts</c> and
/// <c>premium.applyBoost</c> read.
/// </summary>
/// <remarks>
/// A slot not yet applied to a channel carries <c>ChannelId = 0</c>. Slot numbers are per user and
/// must not collide, so new ones continue after the highest the user already holds.
/// See https://corefork.telegram.org/api/boost
/// </remarks>
public static class BoostSlotHelper
{
    public const string CollectionName = "channel_boosts";

    public static async Task GrantAsync(IMongoDatabase mongoDatabase, long userId, int count, long expiresAt, bool gift)
    {
        if (count <= 0)
        {
            return;
        }

        var collection = mongoDatabase.GetCollection<BsonDocument>(CollectionName);
        var highest = await collection.Find(Builders<BsonDocument>.Filter.Eq("UserId", userId))
            .Sort(Builders<BsonDocument>.Sort.Descending("Slot"))
            .Limit(1)
            .FirstOrDefaultAsync();
        var nextSlot = highest != null && highest.Contains("Slot") ? highest["Slot"].ToInt32() + 1 : 1;

        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expires = (int)Math.Min(expiresAt, int.MaxValue);
        var documents = Enumerable.Range(0, count).Select(i => new BsonDocument
        {
            ["_id"] = $"boost-slot-{userId}-{nextSlot + i}-{Guid.NewGuid():N}",
            ["UserId"] = userId,
            ["Slot"] = nextSlot + i,
            ["ChannelId"] = 0L,
            ["Date"] = now,
            ["Expires"] = expires,
            ["Multiplier"] = 1,
            ["Gift"] = gift,
            ["Giveaway"] = false,
            ["Unclaimed"] = false
        });

        await collection.InsertManyAsync(documents);
    }
}
