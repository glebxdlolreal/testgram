namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;

/// <summary>
/// Get available message reactions
/// See https://core.telegram.org/method/messages.getAvailableReactions
/// </summary>
internal sealed class GetAvailableReactionsHandler : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetAvailableReactions, MyTelegram.Schema.Messages.IAvailableReactions>
{
    private readonly IMongoDatabase _database;
    private readonly IAccessHashHelper2 _accessHashHelper;

    public GetAvailableReactionsHandler(IMongoDatabase database, IAccessHashHelper2 accessHashHelper)
    {
        _database = database;
        _accessHashHelper = accessHashHelper;
    }

    protected override async Task<MyTelegram.Schema.Messages.IAvailableReactions> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Messages.RequestGetAvailableReactions obj)
    {
        // Load reactions from MongoDB
        var collection = _database.GetCollection<BsonDocument>("reactions");
        var filter = Builders<BsonDocument>.Filter.Empty;
        var sort = Builders<BsonDocument>.Sort.Ascending("Order");
        var reactionDocs = await collection.Find(filter).Sort(sort).ToListAsync();

        if (reactionDocs.Count == 0)
        {
            // Return empty if no reactions in database
            return new TAvailableReactions
            {
                Reactions = new TVector<IAvailableReaction>(),
                Hash = 0
            };
        }

        // Convert MongoDB documents to TAvailableReaction objects
        var reactions = new List<IAvailableReaction>();
        foreach (var doc in reactionDocs)
        {
            var reaction = new TAvailableReaction
            {
                Reaction = doc["Reaction"].AsString,
                Title = doc["Title"].AsString,
                Inactive = doc.Contains("Inactive") && doc["Inactive"].AsBoolean,
                Premium = doc.Contains("Premium") && doc["Premium"].AsBoolean,
                StaticIcon = ConvertToDocument(input, doc["StaticIcon"].AsBsonDocument),
                AppearAnimation = ConvertToDocument(input, doc["AppearAnimation"].AsBsonDocument),
                SelectAnimation = ConvertToDocument(input, doc["SelectAnimation"].AsBsonDocument),
                ActivateAnimation = ConvertToDocument(input, doc["ActivateAnimation"].AsBsonDocument),
                EffectAnimation = ConvertToDocument(input, doc["EffectAnimation"].AsBsonDocument),
                AroundAnimation = doc.Contains("AroundAnimation") && !doc["AroundAnimation"].IsBsonNull
                    ? ConvertToDocument(input, doc["AroundAnimation"].AsBsonDocument)
                    : new TDocumentEmpty(),
                CenterIcon = doc.Contains("CenterIcon") && !doc["CenterIcon"].IsBsonNull
                    ? ConvertToDocument(input, doc["CenterIcon"].AsBsonDocument)
                    : new TDocumentEmpty()
            };
            reactions.Add(reaction);
        }

        // Calculate hash. Must be reproducible across processes, so string.GetHashCode() (randomized
        // per process in .NET) cannot be used here.
        var hash = TelegramHashHelper.GetInt32Hash(
            reactions.Select(r => TelegramHashHelper.GetStringNumber(((TAvailableReaction)r).Reaction)));

        // Check if client has same hash
        if (obj.Hash != 0 && obj.Hash == hash)
            return new TAvailableReactionsNotModified();

        return new TAvailableReactions
        {
            Reactions = new TVector<IAvailableReaction>(reactions),
            Hash = hash
        };
    }

    private TDocument ConvertToDocument(IRequestInput input, BsonDocument doc)
    {
        var documentId = GetInt64(doc["Id"]);
        var mimeType = doc["MimeType"].AsString;
        return new TDocument
        {
            Id = documentId,
            AccessHash = _accessHashHelper.GenerateAccessHash(input.UserId, input.AccessHashKeyId, documentId, AccessHashType.Document),
            FileReference = GetByteArray(doc["FileReference"]),
            Date = GetInt32(doc["Date"]),
            MimeType = mimeType,
            Size = GetInt64(doc["Size"]),
            Thumbs = ReadThumbs(doc),
            VideoThumbs = new TVector<IVideoSize>(),
            DcId = GetInt32(doc["DcId"]),
            Attributes = BuildAttributes(mimeType)
        };
    }

    private static TVector<IDocumentAttribute> BuildAttributes(string mimeType)
    {
        if (mimeType == "application/x-tgsticker")
        {
            return
            [
                new TDocumentAttributeImageSize { W = 512, H = 512 },
                new TDocumentAttributeFilename { FileName = "AnimatedSticker.tgs" }
            ];
        }

        return [];
    }

    private static TVector<IPhotoSize> ReadThumbs(BsonDocument document)
    {
        var result = new TVector<IPhotoSize>();
        if (!document.TryGetValue("Thumbs", out var thumbsValue) || !thumbsValue.IsBsonArray)
        {
            return result;
        }

        foreach (var value in thumbsValue.AsBsonArray.Where(value => value.IsBsonDocument))
        {
            var thumb = value.AsBsonDocument;
            var type = thumb.GetValue("_t", "").AsString;
            var thumbType = thumb.GetValue("Type", "").AsString;

            switch (type)
            {
                case nameof(TPhotoSize):
                    result.Add(new TPhotoSize
                    {
                        Type = thumbType,
                        W = GetInt32(thumb["W"]),
                        H = GetInt32(thumb["H"]),
                        Size = GetInt32(thumb["Size"]),
                    });
                    break;
                case nameof(TPhotoCachedSize):
                    result.Add(new TPhotoCachedSize
                    {
                        Type = thumbType,
                        W = GetInt32(thumb["W"]),
                        H = GetInt32(thumb["H"]),
                        Bytes = GetByteArray(thumb["Bytes"]),
                    });
                    break;
                case nameof(TPhotoSizeProgressive):
                    result.Add(new TPhotoSizeProgressive
                    {
                        Type = thumbType,
                        W = GetInt32(thumb["W"]),
                        H = GetInt32(thumb["H"]),
                        Sizes = new TVector<int>(thumb["Sizes"].AsBsonArray.Select(GetInt32)),
                    });
                    break;
                case nameof(TPhotoStrippedSize):
                    result.Add(new TPhotoStrippedSize { Type = thumbType, Bytes = GetByteArray(thumb["Bytes"]) });
                    break;
                case nameof(TPhotoPathSize):
                    result.Add(new TPhotoPathSize { Type = thumbType, Bytes = GetByteArray(thumb["Bytes"]) });
                    break;
                case nameof(TPhotoSizeEmpty):
                    result.Add(new TPhotoSizeEmpty { Type = thumbType });
                    break;
            }
        }

        return result;
    }

    private static byte[] GetByteArray(BsonValue value)
    {
        return value.BsonType switch
        {
            BsonType.Binary => value.AsBsonBinaryData.Bytes,
            BsonType.Array => value.AsBsonArray.Select(p => (byte)p.ToInt32()).ToArray(),
            _ => []
        };
    }

    private static long GetInt64(BsonValue value)
    {
        return value.BsonType switch
        {
            BsonType.Int64 => value.AsInt64,
            BsonType.Int32 => value.AsInt32,
            BsonType.Double => (long)value.AsDouble,
            _ => value.ToInt64()
        };
    }

    private static int GetInt32(BsonValue value)
    {
        return value.BsonType switch
        {
            BsonType.Int32 => value.AsInt32,
            BsonType.Int64 => checked((int)value.AsInt64),
            BsonType.Double => (int)value.AsDouble,
            _ => value.ToInt32()
        };
    }
}
