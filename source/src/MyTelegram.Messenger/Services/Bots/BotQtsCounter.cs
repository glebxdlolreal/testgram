using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Bots;

/// <summary>
/// The qts sequence of a bot, shared by every process. Bot updates are produced by both the command and the
/// query server; per-process counters handed out overlapping values, and a client drops an update whose qts it
/// has already seen as a duplicate (found with business bot updates, 2026-11-01).
/// </summary>
public interface IBotQtsCounter
{
    Task<int> NextAsync(long botId);

    /// <summary>The last allocated value, or 0 when the account never got a qts update from it.</summary>
    Task<int> GetAsync(long userId);
}

public sealed class BotQtsCounter(IMongoDatabase mongoDatabase, IPtsHelper ptsHelper, IQueryProcessor queryProcessor)
    : IBotQtsCounter, ISingletonDependency
{
    public const string CollectionName = "bot-qts-counters";

    private readonly ConcurrentDictionary<long, bool> _seeded = new();

    private IMongoCollection<BsonDocument> Collection => mongoDatabase.GetCollection<BsonDocument>(CollectionName);

    public async Task<int> NextAsync(long botId)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("_id", botId);
        if (!_seeded.ContainsKey(botId))
        {
            // Never go below what this process or the persisted state already told the bot.
            var cached = (await ptsHelper.GetPtsForUserAsync(botId)).Qts;
            var stored = (await queryProcessor.ProcessAsync(new GetPtsByPeerIdQuery(botId)))?.Qts ?? 0;
            await Collection.UpdateOneAsync(filter, Builders<BsonDocument>.Update.Max("Value", Math.Max(cached, stored)),
                new UpdateOptions { IsUpsert = true });
            _seeded[botId] = true;
        }

        var counter = await Collection.FindOneAndUpdateAsync(filter, Builders<BsonDocument>.Update.Inc("Value", 1),
            new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After });
        return counter["Value"].ToInt32();
    }

    public async Task<int> GetAsync(long userId)
    {
        var document = await Collection.Find(Builders<BsonDocument>.Filter.Eq("_id", userId)).FirstOrDefaultAsync();
        return document == null ? 0 : document["Value"].ToInt32();
    }
}
