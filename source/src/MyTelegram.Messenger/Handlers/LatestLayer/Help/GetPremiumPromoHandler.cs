using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Options;
using MyTelegram.Messenger.Services.Payments;
using MyTelegram.Messenger.Services.Premium;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Help;
/// <summary>
/// Get Telegram Premium promotion information
/// <para><c>See <a href="https://corefork.telegram.org/method/help.getPremiumPromo"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
///
/// <para>Shaped like real Telegram's answer (measured 2026-10-31): <c>users</c> carries the caller, so
/// clients learn their own <c>premium</c> flag from it, and each period option's <c>bot_url</c> is an
/// @PremiumBot start link (<c>https://t.me/PremiumBot?start=12</c>), longest term first. Prices come
/// from <see cref="PremiumOptions"/>, in the currency of the caller's phone number (<see cref="IPurchasePricing"/>). An account Premium is off sale for gets no period options.</para>
/// </remarks>
internal sealed class GetPremiumPromoHandler(
    IMongoDatabase mongoDatabase,
    IFileReferenceHelper fileReferenceHelper,
    ILayeredService<IPremiumPromoConverter> layeredPremiumPromoService,
    IUserConverterService userConverterService,
    IOptions<PremiumOptions> premiumOptions,
    IPremiumPurchaseGate premiumPurchaseGate,
    IPurchasePricing purchasePricing) : RpcResultObjectHandler<MyTelegram.Schema.Help.RequestGetPremiumPromo, MyTelegram.Schema.Help.IPremiumPromo>
{
    protected override async Task<MyTelegram.Schema.Help.IPremiumPromo> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Help.RequestGetPremiumPromo obj)
    {
        var promo = (TPremiumPromo)layeredPremiumPromoService.GetConverter(input.Layer).ToPremiumPromo();
        var videos = await LoadPromoVideosAsync();
        promo.VideoSections = new TVector<string>(videos.Select(x => x.Section).ToList());
        promo.Videos = new TVector<IDocument>(videos.Select(x => x.Document).ToList());

        var self = await userConverterService.GetUserAsync(input, input.UserId, skipPrivacy: true, layer: input.Layer);
        self.Self = true;
        promo.Users = new TVector<IUser>(self);

        promo.PeriodOptions = await premiumPurchaseGate.IsBlockedAsync(input.UserId)
            ? new TVector<IPremiumSubscriptionOption>()
            : BuildPeriodOptions(premiumOptions.Value, await purchasePricing.ForUserAsync(input.UserId));

        return promo;
    }

    private static TVector<IPremiumSubscriptionOption> BuildPeriodOptions(PremiumOptions premium, PurchasePrices prices)
    {
        var options = premium.GetPeriods()
            .Where(p => p.Subscription && p.Amount > 0)
            .OrderByDescending(p => p.Months)
            .Select(p => (IPremiumSubscriptionOption)new TPremiumSubscriptionOption
            {
                Months = p.Months,
                Currency = prices.Currency,
                Amount = prices.Price(p.Amount),
                BotUrl = PremiumBotService.BotUrlFor(p.Months),
                StoreProduct = StoreProductFor(p.Months)
            })
            .ToList();

        return new TVector<IPremiumSubscriptionOption>(options);
    }

    /// <summary>The store product ids real Telegram quotes for its subscription terms.</summary>
    private static string StoreProductFor(int months) => months switch
    {
        1 => "org.telegram.telegramPremium.monthly",
        12 => "org.telegram.telegramPremium.annual",
        24 => "org.telegram.telegramPremium.biannual",
        _ => $"org.telegram.telegramPremium.months{months}"
    };

    private async Task<List<(string Section, IDocument Document)>> LoadPromoVideosAsync()
    {
        var promoCol = mongoDatabase.GetCollection<BsonDocument>("premium_promo_media");
        var docCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");

        var promoDocs = await promoCol.Find(FilterDefinition<BsonDocument>.Empty)
            .Sort(Builders<BsonDocument>.Sort.Ascending("Order").Ascending("Section"))
            .ToListAsync();
        if (promoDocs.Count == 0)
        {
            return [];
        }

        var documentIds = promoDocs
            .Where(x => x.Contains("DocumentId") && !x["DocumentId"].IsBsonNull)
            .Select(x => GetInt64(x["DocumentId"]))
            .Distinct()
            .ToList();
        if (documentIds.Count == 0)
        {
            return [];
        }

        var docDocs = await docCol.Find(Builders<BsonDocument>.Filter.In("DocumentId", documentIds.Select(x => (BsonValue)new BsonInt64(x))))
            .ToListAsync();
        var docMap = docDocs.ToDictionary(x => GetInt64(x["DocumentId"]));
        var result = new List<(string Section, IDocument Document)>();

        foreach (var promoDoc in promoDocs)
        {
            if (!promoDoc.Contains("Section") || !promoDoc.Contains("DocumentId"))
            {
                continue;
            }

            var documentId = GetInt64(promoDoc["DocumentId"]);
            if (!docMap.TryGetValue(documentId, out var doc))
            {
                continue;
            }

            result.Add((promoDoc["Section"].AsString, BuildDocument(doc)));
        }

        return result;
    }

    private IDocument BuildDocument(BsonDocument d)
    {
        var documentId = GetInt64(d["DocumentId"]);

        TVector<IDocumentAttribute> attributes = [];
        if (d.Contains("Attributes2") && !d["Attributes2"].IsBsonNull)
        {
            try
            {
                attributes = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<TVector<IDocumentAttribute>>(d["Attributes2"].ToJson());
            }
            catch
            {
                attributes = [];
            }
        }

        return new TDocument
        {
            Id = documentId,
            AccessHash = GetInt64(d["AccessHash"]),
            FileReference = fileReferenceHelper.Create(AccessHashType.Document, documentId),
            Date = d.Contains("Date") ? GetInt32(d["Date"]) : 0,
            MimeType = d.Contains("MimeType") ? d["MimeType"].AsString : "application/octet-stream",
            Size = d.Contains("Size") ? GetInt64(d["Size"]) : 0,
            Thumbs = ReadStrippedThumbs(d),
            VideoThumbs = new TVector<IVideoSize>(),
            // dc_id=0 points the client at a non-existent datacenter and makes it spam
            // help.getConfig; fall back to the media DC like the other document builders.
            DcId = d.Contains("DcId") && GetInt32(d["DcId"]) > 0 ? GetInt32(d["DcId"]) : MyTelegramConsts.MediaDcId,
            Attributes = attributes
        };
    }

    /// <summary>
    /// The inline preview real Telegram attaches to each promo video (a photoStrippedSize), shown
    /// while the video itself downloads.
    /// </summary>
    private static TVector<IPhotoSize> ReadStrippedThumbs(BsonDocument d)
    {
        var thumbs = new TVector<IPhotoSize>();
        if (d.TryGetValue("Thumbs", out var value) && value is BsonArray array)
        {
            foreach (var thumb in array.OfType<BsonDocument>())
            {
                if (thumb.TryGetValue("StrippedThumb", out var bytes) && bytes is BsonBinaryData { Bytes.Length: > 0 } binary)
                {
                    thumbs.Add(new TPhotoStrippedSize { Type = "i", Bytes = binary.Bytes });
                }
            }
        }

        return thumbs;
    }

    private static long GetInt64(BsonValue v)
    {
        return v.BsonType switch
        {
            BsonType.Int64 => v.AsInt64,
            BsonType.Int32 => v.AsInt32,
            BsonType.Double => (long)v.AsDouble,
            _ => throw new InvalidCastException($"Cannot convert {v.BsonType} to Int64")
        };
    }

    private static int GetInt32(BsonValue v)
    {
        return v.BsonType switch
        {
            BsonType.Int32 => v.AsInt32,
            BsonType.Int64 => (int)v.AsInt64,
            BsonType.Double => (int)v.AsDouble,
            _ => throw new InvalidCastException($"Cannot convert {v.BsonType} to Int32")
        };
    }
}