using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Bots;

/// <summary>
/// Stores and ranks game high scores. Scores are keyed by the message the game lives in, so a chat
/// game and an inline game are independent leaderboards.
/// </summary>
public interface IGameScoreStore
{
    /// <summary>
    /// Records a score for a player. By default only personal bests are kept, matching the API's
    /// documented behaviour; <paramref name="force"/> allows a lower score through (used to fix
    /// mistakes or ban cheaters).
    /// </summary>
    /// <returns>True when the stored score actually changed.</returns>
    Task<bool> SetScoreAsync(string gameKey, long userId, int score, bool force);

    /// <summary>
    /// Returns the leaderboard for a game, ordered best first, with 1-based positions.
    /// </summary>
    Task<List<(long UserId, int Score)>> GetHighScoresAsync(string gameKey);

    /// <summary>Builds the storage key for a game attached to a regular chat message.</summary>
    static string ChatGameKey(long peerId, int messageId) => $"chat:{peerId}:{messageId}";

    /// <summary>Builds the storage key for a game attached to an inline message.</summary>
    static string InlineGameKey(long id) => $"inline:{id}";
}

public class GameScoreStore(IMongoDatabase mongoDatabase) : IGameScoreStore, ISingletonDependency
{
    private const string CollectionName = "game_high_scores";

    /// <summary>Upstream returns a bounded leaderboard rather than every player ever.</summary>
    private const int MaxScores = 100;

    public async Task<bool> SetScoreAsync(string gameKey, long userId, int score, bool force)
    {
        var collection = mongoDatabase.GetCollection<BsonDocument>(CollectionName);
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("game_key", gameKey),
            Builders<BsonDocument>.Filter.Eq("user_id", userId));

        var existing = await collection.Find(filter).FirstOrDefaultAsync();
        if (existing != null && !force && GetInt32(existing, "score") >= score)
        {
            // Not a personal best and the bot did not ask to overwrite: keep the old score.
            return false;
        }

        await collection.ReplaceOneAsync(
            filter,
            new BsonDocument
            {
                ["_id"] = $"game-score-{gameKey}-{userId}",
                ["game_key"] = gameKey,
                ["user_id"] = userId,
                ["score"] = score,
                ["updated_at"] = DateTime.UtcNow.ToTimestamp()
            },
            new ReplaceOptions { IsUpsert = true });

        return true;
    }

    public async Task<List<(long UserId, int Score)>> GetHighScoresAsync(string gameKey)
    {
        var docs = await mongoDatabase.GetCollection<BsonDocument>(CollectionName)
            .Find(Builders<BsonDocument>.Filter.Eq("game_key", gameKey))
            .Sort(Builders<BsonDocument>.Sort.Descending("score").Ascending("updated_at"))
            .Limit(MaxScores)
            .ToListAsync();

        return docs
            .Select(doc => (GetInt64(doc, "user_id"), GetInt32(doc, "score")))
            .ToList();
    }

    private static int GetInt32(BsonDocument doc, string name) => (int)GetInt64(doc, name);

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
