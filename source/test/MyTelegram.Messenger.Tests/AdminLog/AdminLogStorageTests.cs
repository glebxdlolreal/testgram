using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;
using MyTelegram.Messenger.Services.AdminLog;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.AdminLog;

/// <summary>
/// Feature: the admin log — storage and paging of <c>channels.getAdminLog</c>.
///
/// <para>
/// Clients page through the log by passing the id of the oldest event they already hold as <c>max_id</c>,
/// so ids have to increase within a channel and both bounds have to be exclusive; otherwise every page
/// repeats the event it started from. See https://corefork.telegram.org/api/recent-actions
/// </para>
/// </summary>
public class AdminLogStorageTests
{
    private const long ChannelId = 900000000001;
    private const long AdminUserId = 2010001;

    [RequiresMongoDbFact]
    public async Task Event_ids_increase_within_a_channel_even_under_concurrent_writes()
    {
        using var mongo = EmbeddedMongoServer.Start();

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            AdminLogHelper.LogChangeTitle(mongo.Database, ChannelId, AdminUserId, $"old{i}", $"new{i}")));

        var ids = await EventIdsAsync(mongo.Database);

        ids.Count.ShouldBe(20);
        ids.Distinct().Count().ShouldBe(20);
        ids.OrderBy(p => p).ShouldBe(Enumerable.Range(1, 20).Select(p => (long)p));
    }

    [RequiresMongoDbFact]
    public async Task An_antispam_toggle_is_logged_once_per_operation()
    {
        using var mongo = EmbeddedMongoServer.Start();

        await AdminLogHelper.LogToggleAntiSpam(mongo.Database, ChannelId, AdminUserId, true, "antispam-toggle-7");
        await AdminLogHelper.LogToggleAntiSpam(mongo.Database, ChannelId, AdminUserId, true, "antispam-toggle-7");
        await AdminLogHelper.LogToggleAntiSpam(mongo.Database, ChannelId, AdminUserId, false, "antispam-toggle-8");

        var entries = await mongo.Database.GetCollection<BsonDocument>(AdminLogCollection.Name)
            .Find(FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("event_id", 1)).ToListAsync();
        entries.Count.ShouldBe(2);
        entries.ShouldAllBe(e => e["action"]["type"].AsString == nameof(TChannelAdminLogEventActionToggleAntiSpam));
        entries.Select(e => e["event_id"].ToInt64()).Distinct().Count().ShouldBe(2);
    }

    [RequiresMongoDbFact]
    public async Task Each_channel_gets_its_own_id_sequence()
    {
        using var mongo = EmbeddedMongoServer.Start();

        await AdminLogHelper.LogChangeTitle(mongo.Database, ChannelId, AdminUserId, "a", "b");
        await AdminLogHelper.LogChangeTitle(mongo.Database, ChannelId + 1, AdminUserId, "a", "b");

        var first = await EventIdsAsync(mongo.Database, ChannelId);
        var second = await EventIdsAsync(mongo.Database, ChannelId + 1);

        first.ShouldBe([1L]);
        second.ShouldBe([1L]);
    }

    [RequiresMongoDbFact]
    public async Task An_entry_carries_its_filter_tags_search_text_and_referenced_users()
    {
        using var mongo = EmbeddedMongoServer.Start();

        await AdminLogHelper.LogEditBanned(mongo.Database, ChannelId, AdminUserId,
            new TChannelParticipant { UserId = 555, Date = 1 },
            new TChannelParticipantBanned
            {
                Peer = new TPeerUser { UserId = 555 },
                KickedBy = AdminUserId,
                Date = 1,
                BannedRights = new TChatBannedRights { ViewMessages = true }
            });

        var entry = await mongo.Database.GetCollection<BsonDocument>(AdminLogCollection.Name)
            .Find(Builders<BsonDocument>.Filter.Empty)
            .FirstAsync();

        entry["filters"].AsBsonArray.Select(p => p.AsString).ShouldContain(AdminLogMetadata.Kick);
        entry["related_user_ids"].AsBsonArray.Select(p => p.ToInt64()).ShouldBe([555L]);
        entry["user_id"].ToInt64().ShouldBe(AdminUserId);
        entry.Contains("search_text").ShouldBeTrue();
    }

    [RequiresMongoDbFact]
    public async Task Paging_by_max_id_never_repeats_the_event_it_started_from()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var collection = mongo.Database.GetCollection<BsonDocument>(AdminLogCollection.Name);

        for (var i = 0; i < 5; i++)
        {
            await AdminLogHelper.LogChangeTitle(mongo.Database, ChannelId, AdminUserId, $"old{i}", $"new{i}");
        }

        var firstPage = await PageAsync(collection, maxId: 0, limit: 2);
        firstPage.ShouldBe([5L, 4L]);

        var secondPage = await PageAsync(collection, maxId: firstPage[^1], limit: 2);
        secondPage.ShouldBe([3L, 2L]);

        var thirdPage = await PageAsync(collection, maxId: secondPage[^1], limit: 2);
        thirdPage.ShouldBe([1L]);
    }

    [RequiresMongoDbFact]
    public async Task Min_id_is_exclusive_too()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var collection = mongo.Database.GetCollection<BsonDocument>(AdminLogCollection.Name);

        for (var i = 0; i < 3; i++)
        {
            await AdminLogHelper.LogChangeTitle(mongo.Database, ChannelId, AdminUserId, $"old{i}", $"new{i}");
        }

        var filter = AdminLogQuery.Build(ChannelId, maxId: 0, minId: 1, tags: null, adminIds: null,
            query: null, queryUserIds: null);

        var ids = await collection.Find(filter).SortByDescending(e => e["event_id"]).ToListAsync();

        ids.Select(p => p["event_id"].ToInt64()).ShouldBe([3L, 2L]);
    }

    [RequiresMongoDbFact]
    public async Task An_events_filter_selects_only_the_matching_categories()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var collection = mongo.Database.GetCollection<BsonDocument>(AdminLogCollection.Name);

        await AdminLogHelper.LogChangeTitle(mongo.Database, ChannelId, AdminUserId, "a", "b");
        await AdminLogHelper.LogToggleAntiSpam(mongo.Database, ChannelId, AdminUserId, true);
        await AdminLogHelper.LogParticipantJoin(mongo.Database, ChannelId, 555);

        var tags = AdminLogQuery.Tags(new TChannelAdminLogEventsFilter { Settings = true });
        var filter = AdminLogQuery.Build(ChannelId, 0, 0, tags, null, null, null);

        var found = await collection.Find(filter).ToListAsync();

        found.Select(p => p["action"]["type"].AsString)
            .ShouldBe([nameof(TChannelAdminLogEventActionToggleAntiSpam)]);
    }

    [RequiresMongoDbFact]
    public async Task An_events_filter_with_no_flag_set_matches_nothing()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var collection = mongo.Database.GetCollection<BsonDocument>(AdminLogCollection.Name);

        await AdminLogHelper.LogChangeTitle(mongo.Database, ChannelId, AdminUserId, "a", "b");

        var tags = AdminLogQuery.Tags(new TChannelAdminLogEventsFilter());
        var filter = AdminLogQuery.Build(ChannelId, 0, 0, tags, null, null, null);

        (await collection.Find(filter).ToListAsync()).ShouldBeEmpty();
    }

    [RequiresMongoDbFact]
    public async Task A_query_matches_the_message_text_of_an_event()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var collection = mongo.Database.GetCollection<BsonDocument>(AdminLogCollection.Name);

        await AdminLogHelper.LogDeleteMessage(mongo.Database, ChannelId, AdminUserId, Message(1, "Quarterly report"));
        await AdminLogHelper.LogDeleteMessage(mongo.Database, ChannelId, AdminUserId, Message(2, "Cat pictures"));

        var filter = AdminLogQuery.Build(ChannelId, 0, 0, null, null, "quarterly", null);

        (await collection.Find(filter).ToListAsync()).Count.ShouldBe(1);
    }

    [RequiresMongoDbFact]
    public async Task A_query_also_matches_the_participant_an_event_is_about()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var collection = mongo.Database.GetCollection<BsonDocument>(AdminLogCollection.Name);

        await AdminLogHelper.LogEditBanned(mongo.Database, ChannelId, AdminUserId,
            new TChannelParticipant { UserId = 555, Date = 1 },
            new TChannelParticipantBanned
            {
                Peer = new TPeerUser { UserId = 555 },
                KickedBy = AdminUserId,
                Date = 1,
                BannedRights = new TChatBannedRights { ViewMessages = true }
            });

        // The caller resolved the query string to user 555 against the user read model.
        var filter = AdminLogQuery.Build(ChannelId, 0, 0, null, null, "spammer", [555L]);

        (await collection.Find(filter).ToListAsync()).Count.ShouldBe(1);
    }

    [RequiresMongoDbFact]
    public async Task A_regex_in_the_query_is_matched_literally()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var collection = mongo.Database.GetCollection<BsonDocument>(AdminLogCollection.Name);

        await AdminLogHelper.LogChangeTitle(mongo.Database, ChannelId, AdminUserId, "old", "new");

        var filter = AdminLogQuery.Build(ChannelId, 0, 0, null, null, ".*", null);

        (await collection.Find(filter).ToListAsync()).ShouldBeEmpty();
    }

    [RequiresMongoDbFact]
    public async Task The_retention_index_is_created_with_the_configured_window()
    {
        using var mongo = EmbeddedMongoServer.Start();

        await AdminLogCollection.EnsureIndexesAsync(mongo.Database, retentionSeconds: 172800);

        var indexes = await (await mongo.Database.GetCollection<BsonDocument>(AdminLogCollection.Name)
            .Indexes.ListAsync()).ToListAsync();

        var ttl = indexes.Single(p => p["name"].AsString == "admin_log_retention");
        ttl["expireAfterSeconds"].ToInt64().ShouldBe(172800);
    }

    private static IMessage Message(int id, string text) =>
        new TMessage
        {
            Id = id,
            PeerId = new TPeerChannel { ChannelId = ChannelId },
            Message = text,
            Date = 1,
            Media = new TMessageMediaEmpty(),
            Entities = new TVector<IMessageEntity>()
        };

    private static async Task<List<long>> PageAsync(IMongoCollection<BsonDocument> collection, long maxId, int limit)
    {
        var filter = AdminLogQuery.Build(ChannelId, maxId, 0, null, null, null, null);

        var page = await collection
            .Find(filter)
            .SortByDescending(e => e["event_id"])
            .Limit(limit)
            .ToListAsync();

        return page.Select(p => p["event_id"].ToInt64()).ToList();
    }

    private static async Task<List<long>> EventIdsAsync(IMongoDatabase database, long? channelId = null)
    {
        var filter = channelId == null
            ? Builders<BsonDocument>.Filter.Empty
            : Builders<BsonDocument>.Filter.Eq("channel_id", channelId.Value);

        var entries = await database.GetCollection<BsonDocument>(AdminLogCollection.Name)
            .Find(filter)
            .ToListAsync();

        return entries.Select(p => p["event_id"].ToInt64()).ToList();
    }
}
