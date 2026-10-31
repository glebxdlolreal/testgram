using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Payments;

/// <summary>
/// A settled bot payment, addressed the way <c>payments.getPaymentReceipt</c> addresses it: by the
/// service message the checkout generated.
/// </summary>
/// <remarks>
/// See https://corefork.telegram.org/api/payments#5-checkout
/// </remarks>
public sealed class PaymentReceiptDocument
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    /// <summary>Owner of the receipt service message this record is addressed by.</summary>
    public long OwnerPeerId { get; set; }

    /// <summary>Receipt service message id inside <see cref="OwnerPeerId"/>'s id space.</summary>
    public int MsgId { get; set; }

    public long BotId { get; set; }
    public long BuyerUserId { get; set; }
    public int Date { get; set; }

    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>Serialized <see cref="IWebDocument"/>, null when the invoice had no photo.</summary>
    public byte[]? Photo { get; set; }

    /// <summary>Serialized <see cref="IInvoice"/> as it stood when the payment went through.</summary>
    public byte[] Invoice { get; set; } = [];

    public string Currency { get; set; } = BotInvoiceHelper.StarsCurrency;
    public long TotalAmount { get; set; }

    /// <summary>Ledger transaction id, also the <c>paymentCharge.provider_charge_id</c>.</summary>
    public string TransactionId { get; set; } = string.Empty;

    /// <summary>Serialized <see cref="IPaymentRequestedInfo"/> the buyer submitted, when any.</summary>
    public byte[]? Info { get; set; }

    public string? ShippingOptionId { get; set; }
    public string? InvoiceSlug { get; set; }

    /// <summary>The card a money payment was made with, as <c>paymentReceipt.credentials_title</c> shows it.</summary>
    public string? CredentialsTitle { get; set; }
}

public static class PaymentReceiptHelper
{
    public const string CollectionName = "payment-receipts";

    public static string MakeId(long ownerPeerId, int msgId) => $"{ownerPeerId}-{msgId}";

    public static async Task SaveAsync(IMongoDatabase db, PaymentReceiptDocument document)
    {
        await db.GetCollection<PaymentReceiptDocument>(CollectionName).ReplaceOneAsync(
            x => x.Id == document.Id,
            document,
            new ReplaceOptions { IsUpsert = true });
    }

    public static async Task<PaymentReceiptDocument?> FindAsync(IMongoDatabase db, long ownerPeerId, int msgId)
    {
        return await db.GetCollection<PaymentReceiptDocument>(CollectionName)
            .Find(x => x.Id == MakeId(ownerPeerId, msgId))
            .FirstOrDefaultAsync();
    }

    public static IInvoice ReadInvoice(PaymentReceiptDocument document)
    {
        if (document.Invoice is { Length: > 0 })
        {
            var buffer = new ReadOnlyMemory<byte>(document.Invoice);
            return buffer.Read<IInvoice>();
        }

        return new TInvoice
        {
            Currency = document.Currency,
            Prices = new TVector<ILabeledPrice>(new TLabeledPrice
            {
                Label = document.Title,
                Amount = document.TotalAmount
            })
        };
    }

    public static IWebDocument? ReadPhoto(PaymentReceiptDocument document)
    {
        if (document.Photo is not { Length: > 0 })
        {
            return null;
        }

        var buffer = new ReadOnlyMemory<byte>(document.Photo);
        return buffer.Read<IWebDocument>();
    }
}
