using System.Globalization;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.AntiSpam;

/// <summary>
/// Write-through copy of the in-memory windows so a command-server restart does not forget a burst in
/// progress. Only read when a (chat, sender) window is cold; entries expire with the window via TTL.
/// </summary>
public sealed class AntiSpamWindowStore(IMongoDatabase database, AntiSpamClassifier classifier)
{
    public const string CollectionName = "antispam_window";
    private IMongoCollection<BsonDocument> Window => database.GetCollection<BsonDocument>(CollectionName);
    private static string Id(long chat, int message) => $"{chat}-{message}";

    public async Task InitializeAsync(CancellationToken ct) => await Window.Indexes.CreateManyAsync([
        new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("expires"),
            new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }),
        new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("chat").Ascending("sender").Descending("date"))], ct);

    public Task SaveAsync(long chat, long sender, int message, long date, AntiSpamKeys keys, CancellationToken ct)
    {
        var doc = new BsonDocument { ["_id"] = Id(chat, message), ["chat"] = chat, ["sender"] = sender,
            ["message"] = message, ["date"] = date, ["strict"] = keys.StrictHex,
            ["loose"] = keys.LooseHex is { } loose ? loose : BsonNull.Value,
            ["expires"] = DateTimeOffset.FromUnixTimeSeconds(date + classifier.WindowSeconds).UtcDateTime };
        return Window.ReplaceOneAsync(new BsonDocument("_id", doc["_id"]), doc, new ReplaceOptions { IsUpsert = true }, ct);
    }

    public Task RemoveAsync(long chat, int message, CancellationToken ct) =>
        Window.DeleteOneAsync(new BsonDocument("_id", Id(chat, message)), ct);

    public async Task<List<AntiSpamWindowEntry>> LoadAsync(long chat, long sender, long now, CancellationToken ct)
    {
        var docs = await Window.Find(new BsonDocument { ["chat"] = chat, ["sender"] = sender,
                ["date"] = new BsonDocument("$gte", now - classifier.WindowSeconds) })
            .Sort(new BsonDocument("date", -1)).Limit(AntiSpamBurstTracker.RingSize).ToListAsync(ct);
        return docs.AsEnumerable().Reverse().Select(d => new AntiSpamWindowEntry(d["message"].AsInt32, d["date"].ToInt64(),
            new AntiSpamKeys(Parse(d["strict"].AsString), d["loose"].IsString ? Parse(d["loose"].AsString) : null))).ToList();
    }

    private static UInt128 Parse(string hex) => UInt128.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}

public readonly record struct AntiSpamWindowEntry(int MessageId, long Date, AntiSpamKeys Keys);
