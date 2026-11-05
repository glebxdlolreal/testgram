using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Bots;

/// <summary>
/// The admin rights a bot suggests when it is added as an admin, set through
/// <c>bots.setBotGroupDefaultAdminRights</c> / <c>bots.setBotBroadcastDefaultAdminRights</c> or
/// @BotFather, and served as <c>userFull.bot_group_admin_rights</c> / <c>bot_broadcast_admin_rights</c>.
/// See https://corefork.telegram.org/api/rights#suggested-bot-rights
/// </summary>
public static class BotDefaultAdminRights
{
    public const string Collection = "bot_default_admin_rights";
    public const string Group = "group";
    public const string Broadcast = "broadcast";

    private static readonly (string Field, Func<IChatAdminRights, bool> Get, Action<TChatAdminRights, bool> Set)[] Fields =
    [
        ("ChangeInfo", p => p.ChangeInfo, (p, v) => p.ChangeInfo = v),
        ("PostMessages", p => p.PostMessages, (p, v) => p.PostMessages = v),
        ("EditMessages", p => p.EditMessages, (p, v) => p.EditMessages = v),
        ("DeleteMessages", p => p.DeleteMessages, (p, v) => p.DeleteMessages = v),
        ("BanUsers", p => p.BanUsers, (p, v) => p.BanUsers = v),
        ("InviteUsers", p => p.InviteUsers, (p, v) => p.InviteUsers = v),
        ("PinMessages", p => p.PinMessages, (p, v) => p.PinMessages = v),
        ("AddAdmins", p => p.AddAdmins, (p, v) => p.AddAdmins = v),
        ("Anonymous", p => p.Anonymous, (p, v) => p.Anonymous = v),
        ("ManageCall", p => p.ManageCall, (p, v) => p.ManageCall = v),
        ("Other", p => p.Other, (p, v) => p.Other = v),
        ("ManageTopics", p => p.ManageTopics, (p, v) => p.ManageTopics = v),
        ("PostStories", p => p.PostStories, (p, v) => p.PostStories = v),
        ("EditStories", p => p.EditStories, (p, v) => p.EditStories = v),
        ("DeleteStories", p => p.DeleteStories, (p, v) => p.DeleteStories = v),
        ("ManageDirectMessages", p => p.ManageDirectMessages, (p, v) => p.ManageDirectMessages = v)
    ];

    /// <summary>The rights real BotFather lists for groups, with its labels and in its order.</summary>
    public static readonly (string Field, string Label)[] GroupChoices =
    [
        ("ChangeInfo", "Change group name, photo, etc."), ("DeleteMessages", "Delete messages"),
        ("BanUsers", "Restrict, ban or unban members"), ("InviteUsers", "Invite new users"),
        ("PinMessages", "Pin messages"), ("AddAdmins", "Add new administrators"),
        ("ManageCall", "Manage voice chats"), ("Anonymous", "Promote anonymous admins"),
        ("ManageTopics", "Manage Topics"), ("PostStories", "Post stories"),
        ("EditStories", "Edit stories"), ("DeleteStories", "Delete stories"),
        ("Other", "Manage chat")
    ];

    /// <summary>The rights real BotFather lists for channels, with its labels and in its order.</summary>
    public static readonly (string Field, string Label)[] BroadcastChoices =
    [
        ("ChangeInfo", "Change the chat name, photo, etc."), ("PostMessages", "Post in the channel"),
        ("EditMessages", "Edit messages of other users, pin messages"), ("DeleteMessages", "Delete messages"),
        ("BanUsers", "Delete participants"), ("AddAdmins", "Add new administrators"),
        ("InviteUsers", "Invite via link"), ("ManageCall", "Manage video chat / live stream"),
        ("ManageDirectMessages", "Manage direct messages"), ("PostStories", "Post stories"),
        ("EditStories", "Edit stories"), ("DeleteStories", "Delete stories"),
        ("Other", "Manage channel")
    ];

    public static BsonDocument ToDocument(IChatAdminRights rights)
    {
        var document = new BsonDocument();
        foreach (var (field, get, _) in Fields)
        {
            document[field] = get(rights);
        }

        return document;
    }

    public static TChatAdminRights FromDocument(BsonDocument? document)
    {
        var rights = new TChatAdminRights();
        if (document == null)
        {
            return rights;
        }

        foreach (var (field, _, set) in Fields)
        {
            set(rights, document.TryGetValue(field, out var value) && value.IsBoolean && value.AsBoolean);
        }

        return rights;
    }

    public static bool IsKnownField(string field) => Fields.Any(p => p.Field == field);

    /// <summary>The stored rights, or null when none were ever set.</summary>
    public static async Task<TChatAdminRights?> GetAsync(IMongoDatabase database, long botId, string type)
    {
        var document = await database.GetCollection<BsonDocument>(Collection)
            .Find(Builders<BsonDocument>.Filter.Eq("BotId", botId) & Builders<BsonDocument>.Filter.Eq("Type", type))
            .FirstOrDefaultAsync();

        return document != null && document.TryGetValue("AdminRights", out var rights) && rights.IsBsonDocument
            ? FromDocument(rights.AsBsonDocument)
            : null;
    }

    public static Task SetAsync(IMongoDatabase database, long botId, string type, IChatAdminRights rights)
    {
        return database.GetCollection<BsonDocument>(Collection).UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("BotId", botId) & Builders<BsonDocument>.Filter.Eq("Type", type),
            Builders<BsonDocument>.Update
                .Set("BotId", botId)
                .Set("Type", type)
                .Set("AdminRights", ToDocument(rights))
                .Set("UpdatedAt", DateTime.UtcNow),
            new UpdateOptions { IsUpsert = true });
    }
}
