using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Payments;

/// <summary>
/// A hand-edited per-account list of who may not buy something (Premium, Stars):
/// <c>db["&lt;collection&gt;"].updateOne({_id: NumberLong(123)}, {$set: {Blocked: true}}, {upsert: true})</c>.
/// </summary>
/// <remarks>
/// It lives in its own collection rather than on the user read model because read models are only
/// ever written through aggregate events and are cached without expiry in every process; a hand edit
/// there would neither survive a rebuild nor be seen until a restart. Lookups are cached for
/// <see cref="CacheDuration"/>, so an edit takes effect within that.
/// </remarks>
public abstract class PurchaseBlockList(IMongoDatabase mongoDatabase, string collectionName)
{
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<long, (bool Blocked, DateTime ReadAt)> _cache = new();

    public async Task<bool> IsBlockedAsync(long userId)
    {
        if (userId <= 0)
        {
            return false;
        }

        if (_cache.TryGetValue(userId, out var cached) && DateTime.UtcNow - cached.ReadAt < CacheDuration)
        {
            return cached.Blocked;
        }

        // MongoDB compares numbers by value across Int32/Int64/Double, so a hand-typed {_id: 123}
        // matches as well.
        var document = await mongoDatabase.GetCollection<BsonDocument>(collectionName)
            .Find(Builders<BsonDocument>.Filter.Eq("_id", userId))
            .FirstOrDefaultAsync();

        // A document with no Blocked field still means blocked: inserting {_id: id} is the shortest
        // way to switch an account off by hand.
        var blocked = document != null && (!document.Contains("Blocked") || document["Blocked"].ToBoolean());
        _cache[userId] = (blocked, DateTime.UtcNow);

        return blocked;
    }

    public async Task EnsureAllowedAsync(params long[] userIds)
    {
        foreach (var userId in userIds.Distinct())
        {
            if (await IsBlockedAsync(userId))
            {
                ThrowBlocked();
            }
        }
    }

    protected abstract void ThrowBlocked();
}
