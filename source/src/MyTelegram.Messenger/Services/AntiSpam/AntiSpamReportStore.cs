using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.AntiSpam;

public interface IAntiSpamReportStore
{
    Task<bool> HasAntiSpamDeletionAsync(long channelId, int messageId, CancellationToken cancellationToken = default);
    Task SaveAsync(long reporterUserId, long channelId, int messageId, bool falsePositive, long? participantPeerId = null, CancellationToken cancellationToken = default);
}

/// <summary>Stores moderation feedback only. Reports never enqueue a deletion or restore a message.</summary>
public sealed class AntiSpamReportStore(IMongoDatabase database) : IAntiSpamReportStore, ITransientDependency
{
    public const string CollectionName = "antispam_reports";

    public async Task<bool> HasAntiSpamDeletionAsync(long channelId, int messageId, CancellationToken cancellationToken = default)
    {
        if (messageId <= 0) return false;
        var f = Builders<BsonDocument>.Filter;
        var filter = f.Eq("channel_id", channelId) & f.Eq("user_id", MyTelegramConsts.AntiSpamBotUserId) &
                     f.Eq("action.type", nameof(TChannelAdminLogEventActionDeleteMessage)) &
                     (f.Eq("message_id", messageId) | f.Exists("message_id", false));
        using var cursor = await database.GetCollection<BsonDocument>(AdminLogCollection.Name)
            .FindAsync(filter, cancellationToken: cancellationToken);
        while (await cursor.MoveNextAsync(cancellationToken))
            foreach (var entry in cursor.Current)
                if (MatchesDeletion(entry, channelId, messageId)) return true;
        return false;
    }

    internal static bool MatchesDeletion(BsonDocument entry, long channelId, int messageId)
    {
        if (messageId <= 0 || !NumberEquals(entry, "channel_id", channelId) ||
            !NumberEquals(entry, "user_id", MyTelegramConsts.AntiSpamBotUserId) ||
            !entry.TryGetValue("action", out var value) || value is not BsonDocument action ||
            action.GetValue("type", BsonNull.Value) != nameof(TChannelAdminLogEventActionDeleteMessage)) return false;
        if (entry.Contains("message_id")) return NumberEquals(entry, "message_id", messageId);
        if (!action.TryGetValue("data", out var data) || !data.IsBsonBinaryData) return false;
        try
        {
            ReadOnlyMemory<byte> buffer = data.AsBsonBinaryData.Bytes;
            var deleted = buffer.Read<IChannelAdminLogEventAction>() as TChannelAdminLogEventActionDeleteMessage;
            return deleted?.Message is TMessage message && message.Id == messageId &&
                   message.PeerId is TPeerChannel peer && peer.ChannelId == channelId;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Corrupt historical entries cannot prove an antispam deletion.
            return false;
        }
    }

    private static bool NumberEquals(BsonDocument doc, string key, long expected) =>
        doc.TryGetValue(key, out var value) && value.IsNumeric && value.ToDecimal() == expected;

    public async Task SaveAsync(long reporterUserId, long channelId, int messageId, bool falsePositive,
        long? participantPeerId = null, CancellationToken cancellationToken = default)
    {
        if (messageId <= 0) RpcErrors.RpcErrors400.MsgIdInvalid.ThrowRpcError();
        var id = $"{(falsePositive ? "false-positive" : "spam")}:{channelId}:{messageId}:{reporterUserId}";
        var update = Builders<BsonDocument>.Update
            .SetOnInsert("reporter_user_id", reporterUserId)
            .SetOnInsert("channel_id", channelId)
            .SetOnInsert("message_id", messageId)
            .SetOnInsert("false_positive", falsePositive)
            .SetOnInsert("participant_peer_id", participantPeerId.HasValue ? (BsonValue)participantPeerId.Value : BsonNull.Value)
            .SetOnInsert("date", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        try
        {
            await database.GetCollection<BsonDocument>(CollectionName).UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", id), update, new UpdateOptions { IsUpsert = true }, cancellationToken);
        }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // Concurrent identical upserts have the same immutable report identity.
        }
    }
}
