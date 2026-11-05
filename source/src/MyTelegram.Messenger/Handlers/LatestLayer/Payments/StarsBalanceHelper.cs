using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

public static class StarsBalanceHelper
{
    public static async Task<long> GetBalanceAsync(IMongoDatabase db, long userId)
    {
        var doc = await db.GetCollection<StarsBalanceDocument>("star-balances")
            .Find(x => x.UserId == userId).FirstOrDefaultAsync();
        return doc?.Balance ?? 0;
    }

    public static async Task<long> AddBalanceAsync(IMongoDatabase db, long userId, long delta)
    {
        var col = db.GetCollection<StarsBalanceDocument>("star-balances");
        var result = await col.FindOneAndUpdateAsync(
            x => x.UserId == userId,
            Builders<StarsBalanceDocument>.Update.Inc(x => x.Balance, delta),
            new FindOneAndUpdateOptions<StarsBalanceDocument> { ReturnDocument = ReturnDocument.After, IsUpsert = true }
        );
        return result?.Balance ?? delta;
    }

    /// <summary>
    /// Atomically debits <paramref name="amount"/> only when the stored balance already covers it.
    /// The balance check and the decrement happen in a single conditional update, so two concurrent
    /// requests cannot both pass a prior read-then-write check and drive the balance negative (which
    /// would mint stars). Returns false when the balance is insufficient or the account has no row;
    /// the caller must then abort without crediting the counterparty.
    /// </summary>
    public static async Task<bool> TryDebitAsync(IMongoDatabase db, long userId, long amount)
    {
        if (amount <= 0)
        {
            return false;
        }

        var col = db.GetCollection<StarsBalanceDocument>("star-balances");
        var result = await col.FindOneAndUpdateAsync(
            x => x.UserId == userId && x.Balance >= amount,
            Builders<StarsBalanceDocument>.Update.Inc(x => x.Balance, -amount),
            new FindOneAndUpdateOptions<StarsBalanceDocument> { ReturnDocument = ReturnDocument.After }
        );
        return result != null;
    }

    /// <summary>Appends a row to the Stars ledger and returns its transaction id.</summary>
    /// <remarks>
    /// The id doubles as the <c>paymentCharge.provider_charge_id</c> a bot quotes back to
    /// <c>payments.refundStarsCharge</c>, so the charge and the ledger row always refer to the
    /// same record. See https://corefork.telegram.org/api/payments#6-refunds
    /// </remarks>
    public static async Task<string> AddTransactionAsync(IMongoDatabase db, long userId, long amount, bool gift = false,
        long? peerUserId = null, long? peerChannelId = null, string? title = null, bool stargiftUpgrade = false,
        bool stargiftAuctionBid = false, bool offer = false, string? stargiftSlug = null, int? premiumGiftMonths = null,
        int? starrefCommissionPermille = null, long? starrefPeerUserId = null, long? starrefPeerChannelId = null, long? starrefAmount = null,
        // Layer-223 flags wired into the transaction ledger so reaction sales,
        // resale flows, paid messages, drop-original-details, etc. surface the
        // correct boolean / int columns in starsTransaction on the client.
        bool pending = false, bool failed = false, bool refund = false, bool reaction = false,
        bool businessTransfer = false, bool stargiftResale = false, bool postsSearch = false,
        bool stargiftPrepaidUpgrade = false, bool stargiftDropOriginalDetails = false,
        bool phonegroupMessage = false, int? paidMessages = null, int? msgId = null, int? floodskipNumber = null,
        int? transactionDate = null, string? transactionUrl = null, string? description = null,
        TVector<IMessageMedia>? extendedMedia = null)
    {
        var transactionId = Guid.NewGuid().ToString("N");
        await db.GetCollection<StarsTransactionDocument>("star-transactions").InsertOneAsync(new StarsTransactionDocument
        {
            Id = transactionId,
            TransactionId = transactionId,
            UserId = userId,
            Amount = amount,
            Date = DateTime.UtcNow.ToTimestamp(),
            Gift = gift,
            Refund = refund,
            PeerUserId = peerUserId,
            PeerChannelId = peerChannelId,
            Title = title,
            Description = description,
            StargiftUpgrade = stargiftUpgrade,
            StargiftAuctionBid = stargiftAuctionBid,
            Offer = offer,
            StargiftSlug = stargiftSlug,
            PremiumGiftMonths = premiumGiftMonths,
            StarrefCommissionPermille = starrefCommissionPermille,
            StarrefPeerUserId = starrefPeerUserId,
            StarrefPeerChannelId = starrefPeerChannelId,
            StarrefAmount = starrefAmount,
            Pending = pending,
            Failed = failed,
            Reaction = reaction,
            BusinessTransfer = businessTransfer,
            StargiftResale = stargiftResale,
            PostsSearch = postsSearch,
            StargiftPrepaidUpgrade = stargiftPrepaidUpgrade,
            StargiftDropOriginalDetails = stargiftDropOriginalDetails,
            PhonegroupMessage = phonegroupMessage,
            PaidMessages = paidMessages,
            FloodskipNumber = floodskipNumber,
            MsgId = msgId,
            TransactionDate = transactionDate,
            TransactionUrl = transactionUrl,
            ExtendedMedia = extendedMedia?.Select(x => x.ToBytes()).ToList(),
        });

        return transactionId;
    }

    public static TStarsTransaction BsonToTl(BsonDocument doc)
    {
        var transactionId = doc.Contains("TransactionId") && !doc["TransactionId"].IsBsonNull
            ? doc["TransactionId"].AsString
            : doc["_id"].AsString;

        var amount = doc.Contains("Amount") ? doc["Amount"].AsInt64 : 0L;
        var date = doc.Contains("Date") ? doc["Date"].AsInt32 : 0;
        var gift = doc.Contains("Gift") && doc["Gift"].AsBoolean;
        var refund = doc.Contains("Refund") && doc["Refund"].AsBoolean;
        var title = doc.Contains("Title") && !doc["Title"].IsBsonNull ? doc["Title"].AsString : null;
        var stargiftUpgrade = doc.Contains("StargiftUpgrade") && doc["StargiftUpgrade"].AsBoolean;
        var stargiftAuctionBid = doc.Contains("StargiftAuctionBid") && doc["StargiftAuctionBid"].AsBoolean;
        var offer = doc.Contains("Offer") && doc["Offer"].AsBoolean;
        var premiumGiftMonths = doc.Contains("PremiumGiftMonths") && !doc["PremiumGiftMonths"].IsBsonNull
            ? (int?)doc["PremiumGiftMonths"].AsInt32
            : null;

        long? peerUserId = doc.Contains("PeerUserId") && !doc["PeerUserId"].IsBsonNull
            ? doc["PeerUserId"].AsInt64
            : null;
        long? peerChannelId = doc.Contains("PeerChannelId") && !doc["PeerChannelId"].IsBsonNull
            ? doc["PeerChannelId"].AsInt64
            : null;

        IStarsTransactionPeer peer = peerUserId.HasValue
            ? new TStarsTransactionPeer { Peer = new TPeerUser { UserId = peerUserId.Value } }
            : peerChannelId.HasValue
                ? new TStarsTransactionPeer { Peer = new TPeerChannel { ChannelId = peerChannelId.Value } }
                : new TStarsTransactionPeerUnsupported();

        // Affiliate info
        int? starrefCommissionPermille = doc.Contains("StarrefCommissionPermille") && !doc["StarrefCommissionPermille"].IsBsonNull
            ? (int?)doc["StarrefCommissionPermille"].AsInt32
            : null;
        long? starrefPeerUserId = doc.Contains("StarrefPeerUserId") && !doc["StarrefPeerUserId"].IsBsonNull
            ? doc["StarrefPeerUserId"].AsInt64
            : null;
        long? starrefPeerChannelId = doc.Contains("StarrefPeerChannelId") && !doc["StarrefPeerChannelId"].IsBsonNull
            ? doc["StarrefPeerChannelId"].AsInt64
            : null;
        long? starrefAmount = doc.Contains("StarrefAmount") && !doc["StarrefAmount"].IsBsonNull
            ? doc["StarrefAmount"].AsInt64
            : null;

        IPeer? starrefPeer = starrefPeerUserId.HasValue
            ? new TPeerUser { UserId = starrefPeerUserId.Value }
            : starrefPeerChannelId.HasValue
                ? new TPeerChannel { ChannelId = starrefPeerChannelId.Value }
                : null;

        // Layer-223 flag accessors. Older documents are missing these fields,
        // so we default-treat them as false / null which keeps the result
        // bit-compatible with previously persisted transactions.
        bool BoolField(string name) => doc.Contains(name) && doc[name].AsBoolean;
        int? IntField(string name) => doc.Contains(name) && !doc[name].IsBsonNull ? (int?)doc[name].AsInt32 : null;
        string? StrField(string name) => doc.Contains(name) && !doc[name].IsBsonNull ? doc[name].AsString : null;

        return new TStarsTransaction
        {
            Id = transactionId,
            Amount = new TStarsAmount { Amount = amount },
            Date = date,
            Peer = peer,
            Gift = gift,
            Refund = refund,
            Title = title,
            Description = StrField("Description"),
            StargiftUpgrade = stargiftUpgrade,
            StargiftAuctionBid = stargiftAuctionBid,
            Offer = offer,
            PremiumGiftMonths = premiumGiftMonths,
            StarrefCommissionPermille = starrefCommissionPermille,
            StarrefPeer = starrefPeer,
            StarrefAmount = starrefAmount.HasValue ? new TStarsAmount { Amount = starrefAmount.Value } : null,
            Pending = BoolField("Pending"),
            Failed = BoolField("Failed"),
            Reaction = BoolField("Reaction"),
            BusinessTransfer = BoolField("BusinessTransfer"),
            StargiftResale = BoolField("StargiftResale"),
            PostsSearch = BoolField("PostsSearch"),
            StargiftPrepaidUpgrade = BoolField("StargiftPrepaidUpgrade"),
            StargiftDropOriginalDetails = BoolField("StargiftDropOriginalDetails"),
            PhonegroupMessage = BoolField("PhonegroupMessage"),
            PaidMessages = IntField("PaidMessages"),
            FloodskipNumber = IntField("FloodskipNumber"),
            MsgId = IntField("MsgId"),
            ExtendedMedia = doc.Contains("ExtendedMedia") && doc["ExtendedMedia"].IsBsonArray
                ? new TVector<IMessageMedia>(doc["ExtendedMedia"].AsBsonArray.Select(x =>
                {
                    var memory = new ReadOnlyMemory<byte>(x.AsByteArray);
                    return memory.Read<IMessageMedia>();
                }).ToList())
                : null,
            TransactionDate = IntField("TransactionDate"),
            TransactionUrl = StrField("TransactionUrl"),
        };
    }

    /// <summary>
    /// Hydrates the embedded <c>Stargift</c> field of each transaction by
    /// looking up the unique gift document referenced by <c>StargiftSlug</c>.
    /// One batched query covers every gift transaction in the page.
    /// </summary>
    public static async Task HydrateGiftsAsync(IMongoDatabase db, IList<TStarsTransaction> transactions, IList<string?> slugs)
    {
        var distinct = slugs.Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).Distinct().ToList();
        var uniqueCol = db.GetCollection<UniqueStarGiftDocument>("unique-star-gifts");
        var docs = distinct.Count == 0
            ? []
            : await uniqueCol
                .Find(Builders<UniqueStarGiftDocument>.Filter.In(d => d.Slug, distinct))
                .ToListAsync();
        var giftMap = docs.ToDictionary(d => d.Slug, d => (MyTelegram.Schema.IStarGift)UniqueStarGiftHelper.ToTl(d));
        for (int i = 0; i < transactions.Count; i++)
        {
            var slug = slugs[i];
            if (!string.IsNullOrEmpty(slug) && giftMap.TryGetValue(slug!, out var gift))
            {
                transactions[i].Stargift = gift;
            }
            else
            {
                transactions[i].StargiftUpgrade = false;
                transactions[i].StargiftResale = false;
                transactions[i].StargiftPrepaidUpgrade = false;
                transactions[i].StargiftDropOriginalDetails = false;
                transactions[i].StargiftAuctionBid = false;
                transactions[i].Offer = false;
            }
        }
    }

    public static TStarsTransaction ToTl(StarsTransactionDocument doc)
    {
        IStarsTransactionPeer peer = doc.PeerUserId.HasValue
            ? new TStarsTransactionPeer { Peer = new TPeerUser { UserId = doc.PeerUserId.Value } }
            : doc.PeerChannelId.HasValue
                ? new TStarsTransactionPeer { Peer = new TPeerChannel { ChannelId = doc.PeerChannelId.Value } }
                : new TStarsTransactionPeerUnsupported();

        IPeer? starrefPeer = doc.StarrefPeerUserId.HasValue
            ? new TPeerUser { UserId = doc.StarrefPeerUserId.Value }
            : doc.StarrefPeerChannelId.HasValue
                ? new TPeerChannel { ChannelId = doc.StarrefPeerChannelId.Value }
                : null;

        return new TStarsTransaction
        {
            Id = doc.TransactionId,
            Amount = new TStarsAmount { Amount = doc.Amount }, // signed: negative = outgoing
            Date = doc.Date,
            Peer = peer,
            Gift = doc.Gift,
            Refund = doc.Refund,
            Title = doc.Title,
            Description = doc.Description,
            StargiftUpgrade = doc.StargiftUpgrade,
            StargiftAuctionBid = doc.StargiftAuctionBid,
            Offer = doc.Offer,
            PremiumGiftMonths = doc.PremiumGiftMonths,
            StarrefCommissionPermille = doc.StarrefCommissionPermille,
            StarrefPeer = starrefPeer,
            StarrefAmount = doc.StarrefAmount.HasValue ? new TStarsAmount { Amount = doc.StarrefAmount.Value } : null,
            Pending = doc.Pending,
            Failed = doc.Failed,
            Reaction = doc.Reaction,
            BusinessTransfer = doc.BusinessTransfer,
            StargiftResale = doc.StargiftResale,
            PostsSearch = doc.PostsSearch,
            StargiftPrepaidUpgrade = doc.StargiftPrepaidUpgrade,
            StargiftDropOriginalDetails = doc.StargiftDropOriginalDetails,
            PhonegroupMessage = doc.PhonegroupMessage,
            PaidMessages = doc.PaidMessages,
            FloodskipNumber = doc.FloodskipNumber,
            MsgId = doc.MsgId,
            ExtendedMedia = doc.ExtendedMedia?.Count > 0
                ? new TVector<IMessageMedia>(doc.ExtendedMedia.Select(x =>
                {
                    var memory = new ReadOnlyMemory<byte>(x);
                    return memory.Read<IMessageMedia>();
                }).ToList())
                : null,
            TransactionDate = doc.TransactionDate,
            TransactionUrl = doc.TransactionUrl,
        };
    }
}
