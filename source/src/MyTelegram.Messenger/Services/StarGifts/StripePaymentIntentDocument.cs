using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace MyTelegram.Messenger.Services.StarGifts;

public class StripePaymentIntentDocument
{
    [BsonId]
    public ObjectId Id { get; set; }
    public long FormId { get; set; }
    public string PaymentIntentId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public long UserId { get; set; }
    public long RecipientUserId { get; set; }  // 0 = topup for self, >0 = gift to another user
    public long Stars { get; set; }
    public DateTime CreatedAt { get; set; }
    public byte[]? GiveawayPurpose { get; set; }  // Serialized TInputStorePaymentStarsGiveaway or TInputStorePaymentPremiumGiveaway

    // Premium purchases. What was quoted is kept here so the settlement grants exactly what was paid
    // for, rather than whatever the client restates when it submits the form.

    /// <summary>One of the <c>PremiumCardCheckout.Kind*</c> values; null for Stars top-ups and gifts.</summary>
    public string? PremiumKind { get; set; }
    public int PremiumMonths { get; set; }

    /// <summary>How many subscriptions were paid for: recipients of a gift, or giveaway winners.</summary>
    public int PremiumQuantity { get; set; }
    public List<long>? PremiumRecipientUserIds { get; set; }
    public long Amount { get; set; }
    public string? Currency { get; set; }

    /// <summary>For an @PremiumBot invoice: the stored bot invoice the form was issued for.</summary>
    public string? BotInvoiceId { get; set; }
}
