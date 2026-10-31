using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Fragment;

/// <summary>
/// One entry of the <c>fragment_collectibles</c> catalogue: a username or an anonymous number sold on
/// <a href="https://fragment.com">Fragment</a>, with what it fetched and, once assigned, who owns it.
/// There is no Fragment marketplace behind this server, so the administrator fills the catalogue
/// (<c>scripts/init-fragment-nft.sh</c>, <c>scripts/assign-nft-username.sh</c>).
/// </summary>
public sealed record FragmentCollectible(
    string Id,
    string Type,
    string? Username,
    string? Phone,
    int PurchaseDate,
    string Currency,
    long Amount,
    string CryptoCurrency,
    long CryptoAmount,
    string Url,
    PeerType? OwnerPeerType,
    long? OwnerPeerId,
    string? AssignState);

/// <summary>
/// Reads the <c>fragment_collectibles</c> catalogue. It is server configuration, not an EventFlow read
/// model; who actually owns a username is recorded on the peer through its aggregate.
/// </summary>
public interface IFragmentCollectibleCatalog
{
    public const string CollectionName = "fragment_collectibles";

    public const string AssignStatePending = "pending";
    public const string AssignStateApplied = "applied";
    public const string AssignStateFailed = "failed";
    public const string AssignStateRevokePending = "revoke_pending";
    public const string AssignStateRevoked = "revoked";

    Task<FragmentCollectible?> FindUsernameAsync(string username);

    Task<FragmentCollectible?> FindPhoneAsync(string phone);

    /// <summary>
    /// Whether <paramref name="username"/> is listed on Fragment and nobody holds it, which makes
    /// <c>checkUsername</c> / <c>updateUsername</c> answer <c>USERNAME_PURCHASE_AVAILABLE</c>.
    /// </summary>
    Task<bool> IsUsernameForSaleAsync(string username);
}

/// <inheritdoc />
public sealed class FragmentCollectibleCatalog(IMongoDatabase database) : IFragmentCollectibleCatalog, ITransientDependency
{
    public Task<FragmentCollectible?> FindUsernameAsync(string username)
    {
        return FindAsync(Builders<BsonDocument>.Filter.Eq("type", "username") &
                         Builders<BsonDocument>.Filter.Eq("username", username.ToLowerInvariant()));
    }

    public Task<FragmentCollectible?> FindPhoneAsync(string phone)
    {
        return FindAsync(Builders<BsonDocument>.Filter.Eq("type", "phone") &
                         Builders<BsonDocument>.Filter.Eq("phone", NormalizePhone(phone)));
    }

    public async Task<bool> IsUsernameForSaleAsync(string username)
    {
        var collectible = await FindUsernameAsync(username);
        return collectible != null && collectible.AssignState is not (IFragmentCollectibleCatalog.AssignStatePending
            or IFragmentCollectibleCatalog.AssignStateApplied or IFragmentCollectibleCatalog.AssignStateRevokePending);
    }

    /// <summary>
    /// Phone numbers are stored as bare digits, the way <c>user.phone</c> carries them; clients may
    /// send them with a leading <c>+</c> or spaces.
    /// </summary>
    public static string NormalizePhone(string phone)
    {
        return new string(phone.Where(char.IsAsciiDigit).ToArray());
    }

    private async Task<FragmentCollectible?> FindAsync(FilterDefinition<BsonDocument> filter)
    {
        var doc = await database.GetCollection<BsonDocument>(IFragmentCollectibleCatalog.CollectionName)
            .Find(filter)
            .FirstOrDefaultAsync();

        return doc == null ? null : Parse(doc);
    }

    /// <summary>
    /// Numbers may be stored as Int32, Int64 or Double depending on who wrote them (mongosh writes
    /// plain literals as Double), so every one of them is read leniently.
    /// </summary>
    internal static FragmentCollectible Parse(BsonDocument doc)
    {
        PeerType? ownerPeerType = GetString(doc, "owner_peer_type") switch
        {
            "user" => PeerType.User,
            "channel" => PeerType.Channel,
            _ => null
        };

        return new FragmentCollectible(
            doc.GetValue("_id", BsonString.Empty).ToString()!,
            GetString(doc, "type") ?? string.Empty,
            GetString(doc, "username"),
            GetString(doc, "phone"),
            (int)(GetInt64(doc, "purchase_date") ?? 0),
            GetString(doc, "currency") ?? string.Empty,
            GetInt64(doc, "amount") ?? 0,
            GetString(doc, "crypto_currency") ?? string.Empty,
            GetInt64(doc, "crypto_amount") ?? 0,
            GetString(doc, "url") ?? string.Empty,
            ownerPeerType,
            GetInt64(doc, "owner_peer_id"),
            GetString(doc, "assign_state"));
    }

    private static string? GetString(BsonDocument doc, string name)
    {
        return doc.TryGetValue(name, out var value) && value.IsString ? value.AsString : null;
    }

    private static long? GetInt64(BsonDocument doc, string name)
    {
        if (!doc.TryGetValue(name, out var value))
        {
            return null;
        }

        return value.BsonType switch
        {
            BsonType.Int32 => value.AsInt32,
            BsonType.Int64 => value.AsInt64,
            BsonType.Double => (long)value.AsDouble,
            BsonType.Decimal128 => (long)value.AsDecimal,
            _ => null
        };
    }
}
