using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace MyTelegram.Messenger.Services.StarGifts;

public class StarsBalanceDocument
{
    [BsonId]
    public ObjectId Id { get; set; }
    public long UserId { get; set; }
    public long Balance { get; set; }
}

public class StarsTransactionDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public string Id { get; set; } = string.Empty;
    public string TransactionId { get; set; } = string.Empty;
    public long UserId { get; set; }
    public long Amount { get; set; }   // positive = incoming, negative = outgoing
    public int Date { get; set; }
    public bool Gift { get; set; }

    /// <summary>This row *is* a refund, as opposed to having *been* refunded.</summary>
    public bool Refund { get; set; }

    /// <summary>
    /// When this charge was refunded, so payments.refundStarsCharge can answer
    /// CHARGE_ALREADY_REFUNDED instead of paying the same charge back twice.
    /// </summary>
    public int? RefundedAt { get; set; }
    // peer info
    public long? PeerUserId { get; set; }
    public long? PeerChannelId { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public bool StargiftUpgrade { get; set; }
    public bool StargiftAuctionBid { get; set; }
    public bool Offer { get; set; }
    public string? StargiftSlug { get; set; }
    public int? PremiumGiftMonths { get; set; }
    // affiliate info
    public int? StarrefCommissionPermille { get; set; }
    public long? StarrefPeerUserId { get; set; }
    public long? StarrefPeerChannelId { get; set; }
    public long? StarrefAmount { get; set; }
    // layer-223 starsTransaction flags that were previously not persisted
    public bool Pending { get; set; }
    public bool Failed { get; set; }
    public bool Reaction { get; set; }
    public bool BusinessTransfer { get; set; }
    public bool StargiftResale { get; set; }
    public bool PostsSearch { get; set; }
    public bool StargiftPrepaidUpgrade { get; set; }
    public bool StargiftDropOriginalDetails { get; set; }
    public bool PhonegroupMessage { get; set; }
    public int? PaidMessages { get; set; }

    /// <summary><c>starsTransaction.floodskip_number</c>: the paid broadcast messages this row bills.</summary>
    public int? FloodskipNumber { get; set; }
    public int? MsgId { get; set; }
    public int? TransactionDate { get; set; }
    public string? TransactionUrl { get; set; }
    public List<byte[]>? ExtendedMedia { get; set; }
}
