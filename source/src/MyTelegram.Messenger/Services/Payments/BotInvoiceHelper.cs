using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Payments;

/// <summary>
/// Server side copy of a bot invoice.
/// </summary>
/// <remarks>
/// <para>
/// <c>messageMediaInvoice</c> only carries what the client is allowed to see: title, description,
/// photo, currency and the total. The fields the payment flow actually runs on — the bot API
/// <c>payload</c>, the provider token, and the <c>invoice</c> flags such as <c>flexible</c> or
/// <c>name_requested</c> — are deliberately absent from it, so they have to be kept here instead.
/// Without them <c>updateBotShippingQuery</c> can never fire and the bot receives a pre-checkout
/// query it cannot match to an order.
/// </para>
/// <para>See https://corefork.telegram.org/api/payments </para>
/// </remarks>
public sealed class BotInvoiceDocument
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    /// <summary>Invoice deep link slug, always generated so any invoice can be exported later.</summary>
    public string Slug { get; set; } = string.Empty;

    public long BotId { get; set; }

    /// <summary>Owner of the bot's own copy of the invoice message, 0 for link only invoices.</summary>
    public long OwnerPeerId { get; set; }

    public long ToPeerId { get; set; }

    /// <summary>Message id inside <see cref="OwnerPeerId"/>'s id space, 0 for link only invoices.</summary>
    public int MsgId { get; set; }

    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>Serialized <see cref="IWebDocument"/>, null when the bot supplied no photo.</summary>
    public byte[]? Photo { get; set; }

    public byte[] Payload { get; set; } = [];
    public string? Provider { get; set; }
    public string? ProviderData { get; set; }

    /// <summary>Serialized <see cref="IInvoice"/>: keeps every flag and the full price breakdown.</summary>
    public byte[] Invoice { get; set; } = [];

    public string Currency { get; set; } = BotInvoiceHelper.StarsCurrency;
    public long TotalAmount { get; set; }
    public string? StartParam { get; set; }
    public int Date { get; set; }
}

public static class BotInvoiceHelper
{
    public const string CollectionName = "bot-invoices";
    public const string StarsCurrency = "XTR";

    private const string SlugAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    private const int SlugLength = 22;

    public static string MakeMessageId(long ownerPeerId, int msgId) => $"bot-invoice-{ownerPeerId}-{msgId}";

    public static string MakeSlugId(string slug) => $"bot-invoice-slug-{slug}";

    public static string GenerateSlug()
    {
        return string.Create(SlugLength, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = SlugAlphabet[Random.Shared.Next(SlugAlphabet.Length)];
            }
        });
    }

    public static long GetTotalAmount(IInvoice? invoice)
    {
        return invoice?.Prices?.Sum(p => p.Amount) ?? 0;
    }

    /// <summary>
    /// The bot supplies an <c>inputWebDocument</c>, which has no access hash because the server never
    /// stores the body. It is handed back as a <c>webDocumentNoProxy</c> so clients fetch the bot's URL
    /// directly instead of asking this server for a file it does not have.
    /// </summary>
    public static IWebDocument? ToWebDocument(IInputWebDocument? photo)
    {
        if (photo is not TInputWebDocument webDocument || string.IsNullOrEmpty(webDocument.Url))
        {
            return null;
        }

        return new TWebDocumentNoProxy
        {
            Url = webDocument.Url,
            Size = webDocument.Size,
            MimeType = webDocument.MimeType,
            Attributes = webDocument.Attributes ?? new TVector<IDocumentAttribute>()
        };
    }

    public static IInvoice ReadInvoice(BotInvoiceDocument document)
    {
        if (document.Invoice is { Length: > 0 })
        {
            var buffer = new ReadOnlyMemory<byte>(document.Invoice);
            return buffer.Read<IInvoice>();
        }

        // Only reachable for records written before the invoice blob existed.
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

    public static IWebDocument? ReadPhoto(BotInvoiceDocument document)
    {
        if (document.Photo is not { Length: > 0 })
        {
            return null;
        }

        var buffer = new ReadOnlyMemory<byte>(document.Photo);
        return buffer.Read<IWebDocument>();
    }

    public static BotInvoiceDocument Create(
        TInputMediaInvoice media,
        long botId,
        long ownerPeerId,
        long toPeerId,
        int msgId)
    {
        var slug = GenerateSlug();
        var invoice = media.Invoice;

        return new BotInvoiceDocument
        {
            Id = msgId == 0 ? MakeSlugId(slug) : MakeMessageId(ownerPeerId, msgId),
            Slug = slug,
            BotId = botId,
            OwnerPeerId = ownerPeerId,
            ToPeerId = toPeerId,
            MsgId = msgId,
            Title = media.Title ?? string.Empty,
            Description = media.Description ?? string.Empty,
            Photo = ToWebDocument(media.Photo)?.ToBytes(),
            Payload = media.Payload.ToArray(),
            Provider = media.Provider,
            ProviderData = (media.ProviderData as TDataJSON)?.Data,
            Invoice = invoice?.ToBytes() ?? [],
            Currency = invoice?.Currency ?? StarsCurrency,
            TotalAmount = GetTotalAmount(invoice),
            StartParam = media.StartParam,
            Date = DateTime.UtcNow.ToTimestamp()
        };
    }

    /// <summary>
    /// "For invoice messages, the message will also have a replyInlineMarkup keyboard attached to it.
    /// The first button of the keyboard will always be a keyboardButtonBuy button."
    /// https://corefork.telegram.org/api/payments#2-order-information
    /// </summary>
    /// <remarks>Prepends a keyboardButtonBuy row to whatever keyboard the bot supplied.</remarks>
    public static IReplyMarkup WithBuyButton(IReplyMarkup? replyMarkup)
    {
        var existingRows = replyMarkup is TReplyInlineMarkup { Rows.Count: > 0 } inlineMarkup
            ? inlineMarkup.Rows.ToList()
            : [];

        // A bot may have placed the Pay button itself; the rule is that it comes first, not that
        // there are two of them.
        var existingBuyRow = existingRows.FindIndex(row => row.Buttons?.Any(b => b is TKeyboardButtonBuy) == true);
        if (existingBuyRow >= 0)
        {
            var buyRow = existingRows[existingBuyRow];
            existingRows.RemoveAt(existingBuyRow);
            existingRows.Insert(0, buyRow);
        }
        else
        {
            existingRows.Insert(0, new TKeyboardButtonRow
            {
                Buttons = new TVector<IKeyboardButton>(new TKeyboardButtonBuy { Text = "Pay" })
            });
        }

        return new TReplyInlineMarkup { Rows = new TVector<IKeyboardButtonRow>(existingRows) };
    }

    public static async Task SaveAsync(IMongoDatabase db, BotInvoiceDocument document)
    {
        var collection = db.GetCollection<BotInvoiceDocument>(CollectionName);
        await collection.ReplaceOneAsync(
            x => x.Id == document.Id,
            document,
            new ReplaceOptions { IsUpsert = true });

        // A message invoice is addressable both ways: by the message it was sent in and by its slug,
        // so that a link exported for it later resolves to the very same record.
        if (document.MsgId != 0)
        {
            var alias = CloneAs(document, MakeSlugId(document.Slug));
            await collection.ReplaceOneAsync(
                x => x.Id == alias.Id,
                alias,
                new ReplaceOptions { IsUpsert = true });
        }
    }

    private static BotInvoiceDocument CloneAs(BotInvoiceDocument source, string id)
    {
        return new BotInvoiceDocument
        {
            Id = id,
            Slug = source.Slug,
            BotId = source.BotId,
            OwnerPeerId = source.OwnerPeerId,
            ToPeerId = source.ToPeerId,
            MsgId = source.MsgId,
            Title = source.Title,
            Description = source.Description,
            Photo = source.Photo,
            Payload = source.Payload,
            Provider = source.Provider,
            ProviderData = source.ProviderData,
            Invoice = source.Invoice,
            Currency = source.Currency,
            TotalAmount = source.TotalAmount,
            StartParam = source.StartParam,
            Date = source.Date
        };
    }

    public static async Task<BotInvoiceDocument?> FindBySlugAsync(IMongoDatabase db, string? slug)
    {
        if (string.IsNullOrEmpty(slug))
        {
            return null;
        }

        return await db.GetCollection<BotInvoiceDocument>(CollectionName)
            .Find(x => x.Id == MakeSlugId(slug))
            .FirstOrDefaultAsync();
    }

    public static async Task<BotInvoiceDocument?> FindByMessageAsync(IMongoDatabase db, long ownerPeerId, int msgId)
    {
        return await db.GetCollection<BotInvoiceDocument>(CollectionName)
            .Find(x => x.Id == MakeMessageId(ownerPeerId, msgId))
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Resolves the invoice behind an <c>inputInvoiceMessage</c>.
    /// </summary>
    /// <remarks>
    /// The record is keyed by the bot's own copy of the message, but the caller quotes the message id
    /// from <em>their</em> copy. The recipient's read model carries <c>SenderPeerId</c> /
    /// <c>SenderMessageId</c> pointing back at the sender's copy — the same bridge
    /// <c>GetReplyToMsgIdListQueryHandler</c> uses — so the lookup goes through those.
    /// </remarks>
    public static async Task<BotInvoiceDocument?> ResolveMessageAsync(
        IMongoDatabase db,
        IPeerHelper peerHelper,
        IRequestInput input,
        TInputInvoiceMessage invoiceMessage)
    {
        var peer = peerHelper.GetPeer(invoiceMessage.Peer, input.UserId);
        if (peer == null)
        {
            return null;
        }

        var ownerPeerId = peer.PeerType == PeerType.Channel ? peer.PeerId : input.UserId;

        // The caller is the bot itself (or the invoice was posted to a channel): its own copy is the
        // one the record is keyed by.
        var direct = await FindByMessageAsync(db, ownerPeerId, invoiceMessage.MsgId);
        if (direct != null)
        {
            return direct;
        }

        var messageDoc = await db.GetCollection<BsonDocument>("eventflow-messagereadmodel")
            .Find(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("MessageId", invoiceMessage.MsgId),
                Builders<BsonDocument>.Filter.Eq("OwnerPeerId", ownerPeerId)))
            .FirstOrDefaultAsync();

        if (messageDoc == null)
        {
            return null;
        }

        var senderPeerId = GetInt64(messageDoc, "SenderPeerId");
        var senderMessageId = GetInt32(messageDoc, "SenderMessageId");
        if (senderPeerId == null || senderMessageId == null)
        {
            return null;
        }

        return await FindByMessageAsync(db, senderPeerId.Value, senderMessageId.Value);
    }

    public static async Task<BotInvoiceDocument?> ResolveAsync(
        IMongoDatabase db,
        IPeerHelper peerHelper,
        IRequestInput input,
        IInputInvoice invoice)
    {
        return invoice switch
        {
            TInputInvoiceMessage invoiceMessage => await ResolveMessageAsync(db, peerHelper, input, invoiceMessage),
            TInputInvoiceSlug invoiceSlug => await FindBySlugAsync(db, invoiceSlug.Slug),
            _ => null
        };
    }

    private static long? GetInt64(BsonDocument doc, string name)
    {
        if (!doc.TryGetValue(name, out var value) || value.IsBsonNull)
        {
            return null;
        }

        return value.BsonType switch
        {
            BsonType.Int64 => value.AsInt64,
            BsonType.Int32 => value.AsInt32,
            _ => null
        };
    }

    private static int? GetInt32(BsonDocument doc, string name)
    {
        if (!doc.TryGetValue(name, out var value) || value.IsBsonNull)
        {
            return null;
        }

        return value.BsonType switch
        {
            BsonType.Int32 => value.AsInt32,
            BsonType.Int64 => checked((int)value.AsInt64),
            _ => null
        };
    }
}
