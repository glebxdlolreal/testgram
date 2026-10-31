using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Impl;

/// <summary>
/// Hands out message ids from one counter per owner (a user, or a channel) shared by every process.
/// </summary>
/// <remarks>
/// The HiLo generator used before seeded each process from the newest stored message and then issued
/// a whole block without reserving it anywhere, so the command and query servers issued the same ids
/// and the next send failed with <c>AggregateIsNewSpecification</c>; a deleted newest message plus a
/// restart did the same. Here every id is one atomic <c>$inc</c>. A counter is seeded once per process
/// with <c>$max</c> of the highest id stored so far, which never lowers a counter another process has
/// already moved on.
/// </remarks>
public interface IMessageIdAllocator
{
    /// <summary>Reserves <paramref name="step"/> consecutive ids and returns the first of them.</summary>
    Task<long> NextAsync(long ownerPeerId, int step, Func<Task<long>> highestStoredId, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class MongoMessageIdAllocator(IMongoDatabase mongoDatabase) : IMessageIdAllocator, ISingletonDependency
{
    public const string CollectionName = "message-id-counters";

    private readonly ConcurrentDictionary<long, bool> _seeded = new();

    private IMongoCollection<BsonDocument> Collection => mongoDatabase.GetCollection<BsonDocument>(CollectionName);

    public async Task<long> NextAsync(long ownerPeerId, int step, Func<Task<long>> highestStoredId,
        CancellationToken cancellationToken = default)
    {
        step = Math.Max(1, step);
        var filter = Builders<BsonDocument>.Filter.Eq("_id", ownerPeerId);

        if (!_seeded.ContainsKey(ownerPeerId))
        {
            var stored = await highestStoredId();
            await Collection.UpdateOneAsync(filter, Builders<BsonDocument>.Update.Max("Value", stored),
                new UpdateOptions { IsUpsert = true }, cancellationToken);
            _seeded[ownerPeerId] = true;
        }

        var counter = await Collection.FindOneAndUpdateAsync(filter, Builders<BsonDocument>.Update.Inc("Value", (long)step),
            new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After }, cancellationToken);

        return counter["Value"].ToInt64() - step + 1;
    }
}
