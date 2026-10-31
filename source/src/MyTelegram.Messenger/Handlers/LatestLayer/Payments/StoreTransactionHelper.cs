using MongoDB.Bson;
using MongoDB.Driver;
using System.Security.Cryptography;
using System.Text;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

/// <summary>
/// The half of an app store purchase that is the same whether the receipt came from the App Store or
/// from Play: turning a verified purchase into Premium or into a Stars balance.
/// </summary>
/// <remarks>
/// See https://corefork.telegram.org/method/payments.assignAppStoreTransaction and
/// https://corefork.telegram.org/method/payments.assignPlayMarketTransaction
/// </remarks>
public static class StoreTransactionHelper
{
    /// <summary>Receipts already redeemed, keyed by platform and receipt digest.</summary>
    public const string RedeemedReceiptCollectionName = "store-transactions";

    /// <summary>Running total credited per account through the unverified path.</summary>
    public const string TopupQuotaCollectionName = "store-topup-quota";

    public static async Task CreditStarsAsync(
        IMongoDatabase mongoDatabase,
        IObjectMessageSender objectMessageSender,
        long userId,
        long stars,
        string title)
    {
        await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, userId, stars);
        await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, userId, stars, title: title);
        await BalancePushHelper.PushStarsBalanceAsync(objectMessageSender, mongoDatabase, userId);
    }

    /// <summary>
    /// Lets a purchase through that the server was unable to confirm was ever paid for, or refuses it.
    /// </summary>
    /// <remarks>
    /// Refuses outright unless <see cref="PaymentsConfig.AllowUnverifiedTopup"/> is set, because at
    /// this point the amount rests on nothing but the caller's word: left open, this is an unlimited
    /// Stars tap for anyone holding an auth key. Even when it is set, the receipt is burned on first
    /// use and the payer's lifetime total is held under
    /// <see cref="PaymentsConfig.UnverifiedTopupLimit"/>.
    /// </remarks>
    /// <param name="platform">Where the unverifiable purchase came from: <c>appstore</c>, <c>playmarket</c>, <c>stripe</c>.</param>
    /// <param name="receipt">Whatever identifies this one purchase; two calls quoting the same bytes are the same purchase.</param>
    /// <param name="payerUserId">The account the ceiling is charged against — the buyer, who is not always who ends up with the Stars.</param>
    public static async Task AuthorizeUnverifiedTopupAsync(
        IMongoDatabase mongoDatabase,
        PaymentsConfig config,
        string platform,
        ReadOnlyMemory<byte> receipt,
        long payerUserId,
        long stars)
    {
        if (!config.AllowUnverifiedTopup)
        {
            RpcErrors.RpcErrors400.PaymentProviderInvalid.ThrowRpcError();
        }

        if (stars < 0)
        {
            RpcErrors.RpcErrors400.InputPurposeInvalid.ThrowRpcError();
        }

        await ReserveTopupQuotaAsync(mongoDatabase, payerUserId, stars, config.UnverifiedTopupLimit);

        if (!await TryRedeemReceiptAsync(mongoDatabase, platform, receipt, payerUserId, stars))
        {
            await ReleaseTopupQuotaAsync(mongoDatabase, payerUserId, stars);
            RpcErrors.RpcErrors400.PaymentProviderInvalid.ThrowRpcError();
        }
    }

    public static Task AuthorizeUnverifiedTopupAsync(
        IMongoDatabase mongoDatabase,
        PaymentsConfig config,
        string platform,
        string receipt,
        long payerUserId,
        long stars)
    {
        return AuthorizeUnverifiedTopupAsync(mongoDatabase, config, platform, Encoding.UTF8.GetBytes(receipt),
            payerUserId, stars);
    }

    /// <summary>
    /// Credits a store top-up whose receipt the server has no way of checking with Apple or Google.
    /// </summary>
    public static async Task CreditUnverifiedTopupAsync(
        IMongoDatabase mongoDatabase,
        IObjectMessageSender objectMessageSender,
        PaymentsConfig config,
        string platform,
        ReadOnlyMemory<byte> receipt,
        long userId,
        long stars,
        string title)
    {
        // A store purpose asking for nothing is malformed rather than free, but the closed door comes
        // first: with the path off, the answer must not depend on what the caller asked for.
        if (config.AllowUnverifiedTopup && stars <= 0)
        {
            RpcErrors.RpcErrors400.InputPurposeInvalid.ThrowRpcError();
        }

        await AuthorizeUnverifiedTopupAsync(mongoDatabase, config, platform, receipt, userId, stars);

        await CreditStarsAsync(mongoDatabase, objectMessageSender, userId, stars, title);
    }

    /// <summary>
    /// Records a receipt as spent, returning false if it had already been spent.
    /// </summary>
    /// <remarks>
    /// The key deliberately leaves the account out: one receipt is one purchase, so a receipt already
    /// redeemed by someone else has to be refused rather than credited a second time.
    /// </remarks>
    public static async Task<bool> TryRedeemReceiptAsync(
        IMongoDatabase mongoDatabase,
        string platform,
        ReadOnlyMemory<byte> receipt,
        long userId,
        long stars)
    {
        var digest = Convert.ToHexString(SHA256.HashData(receipt.Span));

        try
        {
            await mongoDatabase.GetCollection<BsonDocument>(RedeemedReceiptCollectionName).InsertOneAsync(
                new BsonDocument
                {
                    ["_id"] = $"{platform}:{digest}",
                    ["Platform"] = platform,
                    ["UserId"] = userId,
                    ["Stars"] = stars,
                    ["Date"] = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                });
            return true;
        }
        catch (MongoWriteException exception)
            when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public static Task CreditUnverifiedTopupAsync(
        IMongoDatabase mongoDatabase,
        IObjectMessageSender objectMessageSender,
        PaymentsConfig config,
        string platform,
        string receipt,
        long userId,
        long stars,
        string title)
    {
        return CreditUnverifiedTopupAsync(mongoDatabase, objectMessageSender, config, platform,
            Encoding.UTF8.GetBytes(receipt), userId, stars, title);
    }

    private static async Task ReserveTopupQuotaAsync(
        IMongoDatabase mongoDatabase,
        long userId,
        long stars,
        long limit)
    {
        var collection = mongoDatabase.GetCollection<BsonDocument>(TopupQuotaCollectionName);
        var filter = Builders<BsonDocument>.Filter.Eq("_id", userId);

        var quota = await collection.FindOneAndUpdateAsync(filter,
            Builders<BsonDocument>.Update.Inc("Credited", stars),
            new FindOneAndUpdateOptions<BsonDocument>
            {
                IsUpsert = true,
                ReturnDocument = ReturnDocument.After
            });

        if (ReadCredited(quota) <= limit)
        {
            return;
        }

        // Take the reservation back. Two callers that overshoot at once each undo their own, so the
        // pair fails closed instead of one of them slipping past the ceiling.
        await ReleaseTopupQuotaAsync(mongoDatabase, userId, stars);
        RpcErrors.RpcErrors400.PaymentProviderInvalid.ThrowRpcError();
    }

    private static Task ReleaseTopupQuotaAsync(IMongoDatabase mongoDatabase, long userId, long stars)
    {
        return mongoDatabase.GetCollection<BsonDocument>(TopupQuotaCollectionName).UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", userId),
            Builders<BsonDocument>.Update.Inc("Credited", -stars));
    }

    private static long ReadCredited(BsonDocument? quota)
    {
        return quota?.GetValue("Credited", BsonNull.Value) switch
        {
            { BsonType: BsonType.Int64 } value => value.AsInt64,
            { BsonType: BsonType.Int32 } value => value.AsInt32,
            { BsonType: BsonType.Double } value => (long)value.AsDouble,
            _ => 0
        };
    }

    public static TUpdates EmptyUpdates() => new()
    {
        Updates = new TVector<IUpdate>(),
        Users = new TVector<IUser>(),
        Chats = new TVector<IChat>(),
        Date = DateTime.UtcNow.ToTimestamp(),
        Seq = 0
    };
}
