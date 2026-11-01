using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Domain.Aggregates.Messaging;
using MyTelegram.Messenger.Services.Business;
using MyTelegram.Messenger.Services.Hashing;
using MyTelegram.Messenger.Tests.Stats;
using MongoMessageReadModel = MyTelegram.ReadModel.MongoDB.MessageReadModel;

namespace MyTelegram.Messenger.Tests.Business;

/// <summary>
/// Feature: <a href="https://corefork.telegram.org/api/business#quick-reply-shortcuts">quick reply shortcuts</a>.
///
/// <para>
/// tdlib computes the <c>messages.getQuickReplies</c> and <c>messages.getQuickReplyMessages</c> hashes
/// from its own cache (<c>QuickReplyManager::get_shortcuts_hash</c>/<c>get_quick_reply_messages_hash</c>),
/// so the server has to reproduce them exactly or <c>*NotModified</c> never fires. Shortcut names follow
/// <c>QuickReplyManager::check_shortcut_name</c>.
/// </para>
/// </summary>
public class QuickReplyTests
{
    [Fact]
    public void Md5_string_hash_takes_the_first_eight_digest_bytes_big_endian()
    {
        // md5("hello") = 5d41402abc4b2a76 b9719d911017c592
        QuickReplyService.Md5StringHash("hello").ShouldBe(0x5d41402abc4b2a76UL);
    }

    [Fact]
    public void Shortcuts_hash_folds_id_name_first_message_and_its_edit_date_in_list_order()
    {
        var a = new QuickReplyShortcut(1, "hello");
        var b = new QuickReplyShortcut(2, "bye");

        var expected = VectorHashHelper.ComputeHash([
            1, (long)QuickReplyService.Md5StringHash("hello"), 10, 0,
            2, (long)QuickReplyService.Md5StringHash("bye"), 20, 1700000000
        ]);

        QuickReplyService.ComputeShortcutsHash([(a, 10, 0), (b, 20, 1700000000)]).ShouldBe(expected);
        QuickReplyService.ComputeShortcutsHash([(b, 20, 1700000000), (a, 10, 0)]).ShouldNotBe(expected);
    }

    [Fact]
    public void Messages_hash_is_over_ascending_ids_whatever_order_they_arrive_in()
    {
        var expected = VectorHashHelper.ComputeHash([5, 0, 7, 1700000000]);

        QuickReplyService.ComputeMessagesHash([(7, 1700000000), (5, 0)]).ShouldBe(expected);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("привет")]
    [InlineData("a_1")]
    [InlineData("abcdefghijabcdefghijabcdefghij12")]
    public void Accepts_letters_digits_and_underscore_up_to_32(string name)
    {
        QuickReplyService.IsValidShortcutName(name).ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("with space")]
    [InlineData("dash-ed")]
    [InlineData("/slash")]
    [InlineData("abcdefghijabcdefghijabcdefghij123")]
    public void Rejects_other_names(string name)
    {
        QuickReplyService.IsValidShortcutName(name).ShouldBeFalse();
    }

    /// <summary>
    /// Quick reply messages are stored with their owner like Saved Messages are, so every history query
    /// filters them out. Documents written before the flag existed have no such field and must still match.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task History_filter_skips_quick_reply_messages_and_keeps_documents_without_the_flag()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var raw = mongo.Database.GetCollection<BsonDocument>("messages");
        await raw.InsertManyAsync([
            new BsonDocument { ["_id"] = "a", ["OwnerPeerId"] = 1L, ["MessageId"] = 1 },
            new BsonDocument
                { ["_id"] = "b", ["OwnerPeerId"] = 1L, ["MessageId"] = 2, ["IsQuickReplyMessage"] = false },
            new BsonDocument
                { ["_id"] = "c", ["OwnerPeerId"] = 1L, ["MessageId"] = 3, ["IsQuickReplyMessage"] = true }
        ]);

        var typed = mongo.Database.GetCollection<MongoMessageReadModel>("messages");
        var ids = await typed.Find(p => p.OwnerPeerId == 1 && !p.IsQuickReplyMessage)
            .Project(p => p.MessageId)
            .ToListAsync();

        ids.OrderBy(p => p).ShouldBe([1, 2]);

        // The shortcut filter of GetQuickReplyMessagesQueryHandler.
        await raw.UpdateOneAsync(Builders<BsonDocument>.Filter.Eq("_id", "c"),
            Builders<BsonDocument>.Update.Set("ShortcutId", 7));
        IReadOnlyCollection<int> shortcutIds = [7];
        var quickReplyIds = await typed
            .Find(p => p.OwnerPeerId == 1 && p.IsQuickReplyMessage && shortcutIds.Contains(p.ShortcutId!.Value))
            .Project(p => p.MessageId)
            .ToListAsync();
        quickReplyIds.ShouldBe([3]);
    }

    [Fact]
    public void Shortcut_ids_and_message_aggregate_ids_do_not_collide_with_chat_messages()
    {
        MessageId.Create(1, 5, true).Value.ShouldNotBe(MessageId.Create(1, 5).Value);
        QuickReplyListId.Create(1).Value.ShouldNotBe(QuickReplyListId.Create(2).Value);
    }
}
