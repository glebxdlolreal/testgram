using System.Buffers;
using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.AdminLog;
using MyTelegram.Messenger.Services.AntiSpam;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.AntiSpam;

public class AntiSpamReportStoreTests
{
    private const long Channel = 800000000001;

    [RequiresMongoDbFact]
    public async Task Concurrent_reports_are_idempotent_and_do_not_change_eventflow_data()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new AntiSpamReportStore(mongo.Database);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => store.SaveAsync(2010001, Channel, 42, true)));
        var reports = mongo.Database.GetCollection<BsonDocument>(AntiSpamReportStore.CollectionName);
        (await reports.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty)).ShouldBe(1);
        var entry = await reports.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        entry["_id"].AsString.ShouldBe($"false-positive:{Channel}:42:2010001");
        entry["date"].IsInt64.ShouldBeTrue();
        var collections = await (await mongo.Database.ListCollectionNamesAsync()).ToListAsync();
        collections.ShouldNotContain(c => c.StartsWith("eventflow-"));
    }

    [RequiresMongoDbFact]
    public async Task Only_the_dedicated_actors_delete_action_can_be_reported()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new AntiSpamReportStore(mongo.Database);
        var logs = mongo.Database.GetCollection<BsonDocument>(AdminLogCollection.Name);
        await logs.InsertManyAsync([
            Entry(1, 2010001, "TChannelAdminLogEventActionDeleteMessage"),
            Entry(2, MyTelegramConsts.AntiSpamBotUserId, "TChannelAdminLogEventActionEditMessage"),
            Entry(3, MyTelegramConsts.AntiSpamBotUserId, "TChannelAdminLogEventActionDeleteMessage")
        ]);
        (await store.HasAntiSpamDeletionAsync(Channel, 1)).ShouldBeFalse();
        (await store.HasAntiSpamDeletionAsync(Channel, 2)).ShouldBeFalse();
        (await store.HasAntiSpamDeletionAsync(Channel, 3)).ShouldBeTrue();
        (await store.HasAntiSpamDeletionAsync(Channel + 1, 3)).ShouldBeFalse();
        (await store.HasAntiSpamDeletionAsync(Channel, 999)).ShouldBeFalse();
    }

    [RequiresMongoDbFact]
    public async Task Historical_binary_delete_entries_match_message_not_event_id()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var action = new TChannelAdminLogEventActionDeleteMessage
        {
            Message = new TMessage { Id = 42, PeerId = new TPeerChannel { ChannelId = Channel }, Message = "original" }
        };
        var writer = new ArrayBufferWriter<byte>();
        action.Serialize(writer);
        var entry = Entry(42, MyTelegramConsts.AntiSpamBotUserId, nameof(TChannelAdminLogEventActionDeleteMessage));
        entry.Remove("message_id");
        entry["event_id"] = 1000L;
        entry["action"]["data"] = new BsonBinaryData(writer.WrittenSpan.ToArray());
        await mongo.Database.GetCollection<BsonDocument>(AdminLogCollection.Name).InsertOneAsync(entry);
        var store = new AntiSpamReportStore(mongo.Database);
        (await store.HasAntiSpamDeletionAsync(Channel, 42)).ShouldBeTrue();
        (await store.HasAntiSpamDeletionAsync(Channel, 1000)).ShouldBeFalse();
    }

    private static BsonDocument Entry(int messageId, long actorId, string type) => new()
    {
        ["channel_id"] = Channel,
        ["user_id"] = actorId,
        ["message_id"] = messageId,
        ["action"] = new BsonDocument { ["type"] = type }
    };
}
