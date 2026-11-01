using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Business;

/// <summary>A <a href="https://corefork.telegram.org/api/business#business-chat-links">business chat link</a>.</summary>
public record BusinessChatLinkItem(
    long UserId,
    string Slug,
    string? Title,
    string Message,
    TVector<IMessageEntity>? Entities,
    int Views);

/// <summary>
/// Business chat links, keyed by slug (unique). Slugs are random and checked for collisions.
/// </summary>
public interface IBusinessChatLinkStore
{
    Task<List<BusinessChatLinkItem>> GetByUserAsync(long userId);
    Task<BusinessChatLinkItem?> GetAsync(string slug);
    Task<long> CountAsync(long userId);
    Task<BusinessChatLinkItem> CreateAsync(long userId, string? title, string message, TVector<IMessageEntity>? entities);
    Task<BusinessChatLinkItem?> UpdateAsync(long userId, string slug, string? title, string message, TVector<IMessageEntity>? entities);
    Task<bool> DeleteAsync(long userId, string slug);

    /// <summary>Counts a view; returns the link, or null when it does not exist.</summary>
    Task<BusinessChatLinkItem?> ResolveAsync(string slug, bool countView);

    /// <summary>The slug out of a full <c>https://t.me/m/slug</c> link or a bare slug.</summary>
    string ParseSlug(string? slugOrLink);
}

public sealed class BusinessChatLinkStore(IMongoDatabase mongoDatabase) : IBusinessChatLinkStore, ITransientDependency
{
    public const string CollectionName = "businesschatlinks";
    private const string SlugAlphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    private IMongoCollection<BsonDocument> Collection => mongoDatabase.GetCollection<BsonDocument>(CollectionName);

    public async Task<List<BusinessChatLinkItem>> GetByUserAsync(long userId)
    {
        var documents = await Collection.Find(Builders<BsonDocument>.Filter.Eq("UserId", userId))
            .Sort(Builders<BsonDocument>.Sort.Ascending("CreatedAt"))
            .ToListAsync();
        return documents.Select(Read).ToList();
    }

    public async Task<BusinessChatLinkItem?> GetAsync(string slug)
    {
        var document = await Collection.Find(Builders<BsonDocument>.Filter.Eq("Slug", slug)).FirstOrDefaultAsync();
        return document == null ? null : Read(document);
    }

    public Task<long> CountAsync(long userId) =>
        Collection.CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("UserId", userId));

    public async Task<BusinessChatLinkItem> CreateAsync(long userId, string? title, string message,
        TVector<IMessageEntity>? entities)
    {
        for (var attempt = 0; ; attempt++)
        {
            var slug = RandomNumberGenerator.GetString(SlugAlphabet, 11);
            try
            {
                await Collection.InsertOneAsync(new BsonDocument
                {
                    ["UserId"] = userId,
                    ["Slug"] = slug,
                    ["Title"] = title == null ? BsonNull.Value : title,
                    ["Message"] = message,
                    ["Entities"] = ToBson(entities),
                    ["Views"] = 0,
                    ["CreatedAt"] = DateTime.UtcNow
                });
                return new BusinessChatLinkItem(userId, slug, title, message, entities, 0);
            }
            catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey &&
                                                 attempt < 5)
            {
                // Slug taken: draw another one.
            }
        }
    }

    public async Task<BusinessChatLinkItem?> UpdateAsync(long userId, string slug, string? title, string message,
        TVector<IMessageEntity>? entities)
    {
        var document = await Collection.FindOneAndUpdateAsync(OwnedFilter(userId, slug),
            Builders<BsonDocument>.Update
                .Set("Title", title == null ? (BsonValue)BsonNull.Value : title)
                .Set("Message", message)
                .Set("Entities", ToBson(entities)),
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After });
        return document == null ? null : Read(document);
    }

    public async Task<bool> DeleteAsync(long userId, string slug) =>
        (await Collection.DeleteOneAsync(OwnedFilter(userId, slug))).DeletedCount > 0;

    public async Task<BusinessChatLinkItem?> ResolveAsync(string slug, bool countView)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("Slug", slug);
        var document = countView
            ? await Collection.FindOneAndUpdateAsync(filter, Builders<BsonDocument>.Update.Inc("Views", 1),
                new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After })
            : await Collection.Find(filter).FirstOrDefaultAsync();
        return document == null ? null : Read(document);
    }

    public string ParseSlug(string? slugOrLink)
    {
        var value = slugOrLink?.Trim() ?? string.Empty;
        var index = value.LastIndexOf("/m/", StringComparison.Ordinal);
        if (index >= 0)
        {
            value = value[(index + 3)..];
        }

        return value.Trim('/');
    }

    private static FilterDefinition<BsonDocument> OwnedFilter(long userId, string slug) =>
        Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("UserId", userId),
            Builders<BsonDocument>.Filter.Eq("Slug", slug));

    private static BsonValue ToBson(TVector<IMessageEntity>? entities)
    {
        if (entities == null || entities.Count == 0)
        {
            return BsonNull.Value;
        }

        // Kept as TL bytes: entity constructors carry optional fields a BSON class map would have to know.
        var writer = new System.Buffers.ArrayBufferWriter<byte>();
        entities.Serialize(writer);
        return new BsonBinaryData(writer.WrittenSpan.ToArray());
    }

    private static TVector<IMessageEntity>? FromBson(BsonValue? value)
    {
        if (value is not { IsBsonBinaryData: true })
        {
            return null;
        }

        // TVector.Serialize writes the vector constructor id first; the vector reader starts at the count.
        ReadOnlyMemory<byte> buffer = value.AsBsonBinaryData.Bytes;
        buffer = buffer[4..];
        return buffer.ReadVector<IMessageEntity>();
    }

    private static BusinessChatLinkItem Read(BsonDocument document)
    {
        var title = document.GetValue("Title", BsonNull.Value);
        var message = document.GetValue("Message", BsonNull.Value);
        var views = document.GetValue("Views", 0);
        return new BusinessChatLinkItem(
            document["UserId"].ToInt64(),
            document["Slug"].AsString,
            title.IsString && title.AsString.Length > 0 ? title.AsString : null,
            message.IsString ? message.AsString : string.Empty,
            FromBson(document.GetValue("Entities", BsonNull.Value)),
            views.IsNumeric ? views.ToInt32() : 0);
    }
}
