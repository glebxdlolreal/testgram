using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Business;

/// <summary><c>businessBotRights</c>: what a connected bot may do on behalf of the account.</summary>
public record BusinessBotRightsItem(
    bool Reply,
    bool ReadMessages,
    bool DeleteSentMessages,
    bool DeleteReceivedMessages,
    bool EditName,
    bool EditBio,
    bool EditProfilePhoto,
    bool EditUsername,
    bool ViewGifts,
    bool SellGifts,
    bool ChangeGiftSettings,
    bool TransferAndUpgradeGifts,
    bool TransferStars,
    bool ManageStories)
{
    public static BusinessBotRightsItem None { get; } =
        new(false, false, false, false, false, false, false, false, false, false, false, false, false, false);

    public static BusinessBotRightsItem From(IBusinessBotRights? rights) => rights is TBusinessBotRights r
        ? new(r.Reply, r.ReadMessages, r.DeleteSentMessages, r.DeleteReceivedMessages, r.EditName, r.EditBio,
            r.EditProfilePhoto, r.EditUsername, r.ViewGifts, r.SellGifts, r.ChangeGiftSettings,
            r.TransferAndUpgradeGifts, r.TransferStars, r.ManageStories)
        : None;

    public TBusinessBotRights ToTl() => new()
    {
        Reply = Reply, ReadMessages = ReadMessages, DeleteSentMessages = DeleteSentMessages,
        DeleteReceivedMessages = DeleteReceivedMessages, EditName = EditName, EditBio = EditBio,
        EditProfilePhoto = EditProfilePhoto, EditUsername = EditUsername, ViewGifts = ViewGifts,
        SellGifts = SellGifts, ChangeGiftSettings = ChangeGiftSettings,
        TransferAndUpgradeGifts = TransferAndUpgradeGifts, TransferStars = TransferStars,
        ManageStories = ManageStories
    };
}

/// <summary>
/// The <a href="https://corefork.telegram.org/api/bots/connected-business-bots">business bot</a> connected to
/// an account. An account has at most one. <see cref="ConnectionId"/> stays the same while the bot stays
/// connected: the bot caches it and quotes it in <c>invokeWithBusinessConnection</c>.
/// </summary>
public record BusinessConnection(
    long UserId,
    long BotId,
    string ConnectionId,
    BusinessRecipientsItem Recipients,
    List<long> ExcludeUserIds,
    BusinessBotRightsItem Rights,
    int Date);

public interface IBusinessConnectionStore
{
    Task<BusinessConnection?> GetByUserAsync(long userId);
    Task<BusinessConnection?> GetByConnectionIdAsync(string connectionId);
    Task<List<BusinessConnection>> GetByBotAsync(long botId);

    /// <summary>Stores the account's connection, replacing any other bot it had.</summary>
    Task SaveAsync(BusinessConnection connection);

    Task DeleteAsync(long userId);
    Task<bool> IsPausedAsync(long userId, long peerId);
    Task SetPausedAsync(long userId, long peerId, bool paused);
    Task ClearPausedAsync(long userId);

    /// <summary>
    /// Whether the connected bot works with <paramref name="peerUserId"/>: private chat partners selected by
    /// the recipients and not paused.
    /// </summary>
    Task<bool> AppliesToAsync(BusinessConnection connection, long peerUserId);

    /// <summary>Whether the recipients select <paramref name="peerUserId"/>, regardless of pausing.</summary>
    Task<bool> SelectsAsync(BusinessConnection connection, long peerUserId);

    string NewConnectionId();
}

public sealed class BusinessConnectionStore(IMongoDatabase mongoDatabase, IQueryProcessor queryProcessor)
    : IBusinessConnectionStore, ITransientDependency
{
    public const string CollectionName = "connected_business_bots";
    public const string PausedCollectionName = "paused_business_bot_chats";

    private IMongoCollection<BsonDocument> Collection => mongoDatabase.GetCollection<BsonDocument>(CollectionName);
    private IMongoCollection<BsonDocument> Paused => mongoDatabase.GetCollection<BsonDocument>(PausedCollectionName);

    public async Task<BusinessConnection?> GetByUserAsync(long userId)
    {
        var document = await Collection.Find(Builders<BsonDocument>.Filter.Eq("UserId", userId))
            .Sort(Builders<BsonDocument>.Sort.Descending("UpdatedAt"))
            .FirstOrDefaultAsync();
        return document == null ? null : Read(document);
    }

    public async Task<BusinessConnection?> GetByConnectionIdAsync(string connectionId)
    {
        var document = await Collection.Find(Builders<BsonDocument>.Filter.Eq("ConnectionId", connectionId))
            .FirstOrDefaultAsync();
        return document == null ? null : Read(document);
    }

    public async Task<List<BusinessConnection>> GetByBotAsync(long botId)
    {
        var documents = await Collection.Find(Builders<BsonDocument>.Filter.Eq("BotId", botId)).ToListAsync();
        return documents.Select(Read).ToList();
    }

    public async Task SaveAsync(BusinessConnection connection)
    {
        await Collection.DeleteManyAsync(Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("UserId", connection.UserId),
            Builders<BsonDocument>.Filter.Ne("BotId", connection.BotId)));

        var r = connection.Recipients;
        var rights = connection.Rights;
        var now = DateTime.UtcNow;
        await Collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("UserId", connection.UserId),
                Builders<BsonDocument>.Filter.Eq("BotId", connection.BotId)),
            Builders<BsonDocument>.Update
                .Set("ConnectionId", connection.ConnectionId)
                .Set("Date", connection.Date)
                .Set("Recipients", new BsonDocument
                {
                    ["ExistingChats"] = r.ExistingChats,
                    ["NewChats"] = r.NewChats,
                    ["Contacts"] = r.Contacts,
                    ["NonContacts"] = r.NonContacts,
                    ["ExcludeSelected"] = r.ExcludeSelected,
                    ["Users"] = new BsonArray(r.UserIds),
                    ["ExcludeUsers"] = new BsonArray(connection.ExcludeUserIds)
                })
                .Set("Rights", new BsonDocument
                {
                    ["Reply"] = rights.Reply,
                    ["ReadMessages"] = rights.ReadMessages,
                    ["DeleteSentMessages"] = rights.DeleteSentMessages,
                    ["DeleteReceivedMessages"] = rights.DeleteReceivedMessages,
                    ["EditName"] = rights.EditName,
                    ["EditBio"] = rights.EditBio,
                    ["EditProfilePhoto"] = rights.EditProfilePhoto,
                    ["EditUsername"] = rights.EditUsername,
                    ["ViewGifts"] = rights.ViewGifts,
                    ["SellGifts"] = rights.SellGifts,
                    ["ChangeGiftSettings"] = rights.ChangeGiftSettings,
                    ["TransferAndUpgradeGifts"] = rights.TransferAndUpgradeGifts,
                    ["TransferStars"] = rights.TransferStars,
                    ["ManageStories"] = rights.ManageStories
                })
                .Set("UpdatedAt", now)
                .SetOnInsert("CreatedAt", now),
            new UpdateOptions { IsUpsert = true });
    }

    public Task DeleteAsync(long userId) =>
        Collection.DeleteManyAsync(Builders<BsonDocument>.Filter.Eq("UserId", userId));

    public async Task<bool> IsPausedAsync(long userId, long peerId) =>
        await Paused.Find(PausedFilter(userId, peerId)).AnyAsync();

    public async Task SetPausedAsync(long userId, long peerId, bool paused)
    {
        if (paused)
        {
            await Paused.UpdateOneAsync(PausedFilter(userId, peerId),
                Builders<BsonDocument>.Update.Set("PausedAt", DateTime.UtcNow), new UpdateOptions { IsUpsert = true });
        }
        else
        {
            await Paused.DeleteManyAsync(PausedFilter(userId, peerId));
        }
    }

    public Task ClearPausedAsync(long userId) =>
        Paused.DeleteManyAsync(Builders<BsonDocument>.Filter.Eq("UserId", userId));

    public async Task<bool> AppliesToAsync(BusinessConnection connection, long peerUserId) =>
        await SelectsAsync(connection, peerUserId) && !await IsPausedAsync(connection.UserId, peerUserId);

    public async Task<bool> SelectsAsync(BusinessConnection connection, long peerUserId)
    {
        if (peerUserId == connection.UserId || peerUserId == connection.BotId)
        {
            return false;
        }

        var contacts = await queryProcessor.ProcessAsync(
            new GetContactListBySelfIdAndTargetUserIdQuery(connection.UserId, peerUserId));
        var isContact = contacts?.Any(p => p.SelfUserId == connection.UserId && p.TargetUserId == peerUserId) ?? false;
        var dialog = await queryProcessor.ProcessAsync(new GetDialogByIdQuery(
            DialogId.Create(connection.UserId, PeerType.User, peerUserId).Value));
        var isNewChat = dialog == null ||
                        new DateTimeOffset(dialog.CreationTime).ToUnixTimeSeconds() >= connection.Date;

        return BusinessRecipientsMatcher.Matches(connection.Recipients, peerUserId, isContact, isNewChat,
            connection.ExcludeUserIds);
    }

    public string NewConnectionId()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');
    }

    private static FilterDefinition<BsonDocument> PausedFilter(long userId, long peerId) =>
        Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("UserId", userId),
            Builders<BsonDocument>.Filter.Eq("PeerId", peerId));

    /// <summary>Tolerates the shapes older code wrote: null lists, rights with a TL discriminator.</summary>
    internal static BusinessConnection Read(BsonDocument document)
    {
        var recipients = document.GetValue("Recipients", BsonNull.Value) as BsonDocument ?? [];
        var rights = document.GetValue("Rights", BsonNull.Value) as BsonDocument ?? [];
        bool B(BsonDocument d, string name) => d.TryGetValue(name, out var v) && v.IsBoolean && v.AsBoolean;
        List<long> L(BsonDocument d, string name) => d.TryGetValue(name, out var v) && v.IsBsonArray
            ? v.AsBsonArray.Where(p => p.IsNumeric).Select(p => p.ToInt64()).ToList()
            : [];

        var date = document.TryGetValue("Date", out var d) && d.IsNumeric
            ? d.ToInt32()
            : document.TryGetValue("CreatedAt", out var created) && created.IsValidDateTime
                ? (int)new DateTimeOffset(created.ToUniversalTime()).ToUnixTimeSeconds()
                : 0;

        return new BusinessConnection(
            document["UserId"].ToInt64(),
            document["BotId"].ToInt64(),
            document.GetValue("ConnectionId", string.Empty).AsString,
            new BusinessRecipientsItem(B(recipients, "ExistingChats"), B(recipients, "NewChats"),
                B(recipients, "Contacts"), B(recipients, "NonContacts"), B(recipients, "ExcludeSelected"),
                L(recipients, "Users")),
            L(recipients, "ExcludeUsers"),
            new BusinessBotRightsItem(B(rights, "Reply"), B(rights, "ReadMessages"), B(rights, "DeleteSentMessages"),
                B(rights, "DeleteReceivedMessages"), B(rights, "EditName"), B(rights, "EditBio"),
                B(rights, "EditProfilePhoto"), B(rights, "EditUsername"), B(rights, "ViewGifts"),
                B(rights, "SellGifts"), B(rights, "ChangeGiftSettings"), B(rights, "TransferAndUpgradeGifts"),
                B(rights, "TransferStars"), B(rights, "ManageStories")),
            date);
    }
}
