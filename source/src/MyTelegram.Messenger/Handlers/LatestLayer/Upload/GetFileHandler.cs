using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Passport;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Messenger.Services.SecretChat;

namespace MyTelegram.Messenger.Messenger.Handlers.LatestLayer.Upload;

/// <summary>
/// Returns content of a whole file or its part.
/// See https://core.telegram.org/method/upload.getFile
/// </summary>
internal sealed class GetFileHandler : RpcResultObjectHandler<MyTelegram.Schema.Upload.RequestGetFile, MyTelegram.Schema.Upload.IFile>
{
    private readonly IMongoDatabase _database;
    private readonly ILogger<GetFileHandler> _logger;
    private readonly IHlsGroupCallStreamService _hlsGroupCallStreamService;
    private readonly IEncryptedFileStore _encryptedFileStore;
    private readonly IPassportFileStore _passportFileStore;
    private readonly IAccessHashHelper2 _accessHashHelper;
    private readonly IFileReferenceHelper _fileReferenceHelper;

    public GetFileHandler(
        IMongoDatabase database,
        ILogger<GetFileHandler> logger,
        IHlsGroupCallStreamService hlsGroupCallStreamService,
        IEncryptedFileStore encryptedFileStore,
        IPassportFileStore passportFileStore,
        IAccessHashHelper2 accessHashHelper,
        IFileReferenceHelper fileReferenceHelper)
    {
        _database = database;
        _logger = logger;
        _hlsGroupCallStreamService = hlsGroupCallStreamService;
        _encryptedFileStore = encryptedFileStore;
        _passportFileStore = passportFileStore;
        _accessHashHelper = accessHashHelper;
        _fileReferenceHelper = fileReferenceHelper;
    }

    protected override async Task<MyTelegram.Schema.Upload.IFile> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Upload.RequestGetFile obj)
    {
        // Validate input
        if (obj.Limit <= 0 || obj.Limit > 1024 * 1024) // Max 1MB per request
        {
            RpcErrors.RpcErrors400.LimitInvalid.ThrowRpcError();
        }

        if (obj.Offset < 0)
        {
            RpcErrors.RpcErrors400.OffsetInvalid.ThrowRpcError();
        }

        if (obj.Location is TInputGroupCallStream groupCallStream)
        {
            return await HandleGroupCallStreamAsync(input, groupCallStream, obj.Limit);
        }

        // Encrypted-file download for secret chats. Branch BEFORE the file_parts lookup below,
        // which filters by the caller's UserId and would reject the recipient. access_hash is the
        // capability token (as on Telegram proper) — no membership join.
        if (obj.Location is TInputEncryptedFileLocation encryptedFileLocation)
        {
            // Reads only the requested window, not the whole blob.
            var loaded = await _encryptedFileStore.LoadRangeAsync(encryptedFileLocation.Id,
                encryptedFileLocation.AccessHash,
                obj.Offset,
                obj.Limit);
            if (loaded == null)
            {
                RpcErrors.RpcErrors400.FileIdInvalid.ThrowRpcError();
            }

            return new MyTelegram.Schema.Upload.TFile
            {
                Type = new MyTelegram.Schema.Storage.TFilePartial(),
                Mtime = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Bytes = loaded!.Value.Bytes
            };
        }

        // Telegram Passport download. The bot the form was submitted to reads the very same blob, so
        // ownership is not the gate here — the session-derived access hash is, exactly as for
        // secret-chat files. https://corefork.telegram.org/api/passport#securefile
        if (obj.Location is TInputSecureFileLocation secureFileLocation)
        {
            await _accessHashHelper.CheckAccessHashAsync(input, secureFileLocation.Id,
                secureFileLocation.AccessHash, AccessHashType.Document);

            var loaded = await _passportFileStore.LoadRangeAsync(secureFileLocation.Id, obj.Offset, obj.Limit);
            if (loaded == null)
            {
                RpcErrors.RpcErrors400.FileIdInvalid.ThrowRpcError();
            }

            return new MyTelegram.Schema.Upload.TFile
            {
                Type = new MyTelegram.Schema.Storage.TFilePartial(),
                Mtime = loaded!.Value.Document.Date,
                Bytes = loaded.Value.Bytes
            };
        }

        // Extract file ID from location
        long fileId = obj.Location switch
        {
            TInputDocumentFileLocation doc => doc.Id,
            TInputPhotoFileLocation photo => photo.Id,
            TInputFileLocation file => file.VolumeId, // Legacy
            TInputPeerPhotoFileLocation peerPhoto => peerPhoto.PhotoId,
            _ => 0
        };

        if (fileId == 0)
        {
            RpcErrors.RpcErrors400.LocationInvalid.ThrowRpcError();
        }

        // The two location types that carry a file reference. upload.getFile is the one download method
        // whose documented errors include FILE_REFERENCE_EMPTY / _EXPIRED / _INVALID, and answering them
        // is what starts the client's repair loop; a request that reaches this handler with a bad
        // reference was routed here by FileDownloadLaneRouter precisely so it can be refused.
        // See https://corefork.telegram.org/api/file-references
        switch (obj.Location)
        {
            case TInputDocumentFileLocation documentLocation:
                _fileReferenceHelper.Check(documentLocation.FileReference.Span, AccessHashType.Document,
                    documentLocation.Id);
                break;
            case TInputPhotoFileLocation photoLocation:
                _fileReferenceHelper.Check(photoLocation.FileReference.Span, AccessHashType.Photo,
                    photoLocation.Id);
                break;
        }

        // Try to get file from uploaded parts first (for recently uploaded files)
        var partsCollection = _database.GetCollection<BsonDocument>("file_parts");
        var partsFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("UserId", input.UserId),
            Builders<BsonDocument>.Filter.Eq("FileId", fileId)
        );
        // Read the part sizes only. Loading the Bytes of every part just to copy a window out of
        // the assembled blob meant a 1-byte ranged request on a 2 GB upload still materialised the
        // whole file (twice, via the intermediate List<byte>), so concurrent getFile calls could
        // exhaust server memory.
        var partIndex = await partsCollection.Find(partsFilter)
            .Sort(Builders<BsonDocument>.Sort.Ascending("FilePart"))
            .Project(Builders<BsonDocument>.Projection.Include("FilePart").Include("Size"))
            .ToListAsync();

        if (partIndex.Count > 0)
        {
            // Part sizes are not fixed on the saveFilePart path, so the byte offset of each part is
            // derived from the running total rather than assumed.
            var windowStart = obj.Offset;
            var windowEnd = obj.Offset + obj.Limit;
            var covering = new List<(int FilePart, long Start)>();
            long cursor = 0;
            foreach (var entry in partIndex)
            {
                var size = entry.TryGetValue("Size", out var sizeValue) ? sizeValue.ToInt64() : 0;
                var partStart = cursor;
                cursor += size;

                if (partStart < windowEnd && cursor > windowStart)
                {
                    covering.Add((entry["FilePart"].ToInt32(), partStart));
                }

                if (partStart >= windowEnd)
                {
                    break;
                }
            }

            var totalSize = cursor;
            var start = Math.Min(windowStart, totalSize);
            var length = (int)Math.Min(obj.Limit, totalSize - start);
            var resultBytes = new byte[Math.Max(0, length)];

            if (covering.Count > 0 && resultBytes.Length > 0)
            {
                var coveringParts = await partsCollection
                    .Find(partsFilter & Builders<BsonDocument>.Filter.In("FilePart", covering.Select(p => p.FilePart)))
                    .ToListAsync();
                var startByPart = covering.ToDictionary(p => p.FilePart, p => p.Start);

                foreach (var part in coveringParts)
                {
                    if (!startByPart.TryGetValue(part["FilePart"].ToInt32(), out var partStart))
                    {
                        continue;
                    }

                    var partBytes = part["Bytes"].AsByteArray;
                    // Overlap of [partStart, partStart+len) with the requested window, in part-local
                    // coordinates.
                    var copyFrom = (int)Math.Max(0, start - partStart);
                    var copyTo = (int)Math.Min(partBytes.Length, windowEnd - partStart);
                    if (copyTo <= copyFrom)
                    {
                        continue;
                    }

                    var destination = (int)(partStart + copyFrom - start);
                    Array.Copy(partBytes, copyFrom, resultBytes, destination, copyTo - copyFrom);
                }
            }

            _logger.LogDebug("Retrieved file from parts: FileId={FileId}, Offset={Offset}, Limit={Limit}, Returned={Length}",
                fileId, obj.Offset, obj.Limit, resultBytes.Length);

            return new MyTelegram.Schema.Upload.TFile
            {
                Type = new MyTelegram.Schema.Storage.TFilePartial(), // Partial file type
                Mtime = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Bytes = resultBytes
            };
        }

        // File not found in parts, check if it's a stored document/photo
        // Stored files should be served by the FileServer, not the messenger server
        var documentsCollection = _database.GetCollection<BsonDocument>("eventflow-documentreadmodel");
        var docFilter = Builders<BsonDocument>.Filter.Eq("DocumentId", fileId);
        var document = await documentsCollection.Find(docFilter).FirstOrDefaultAsync();

        if (document == null)
        {
            // Check if it's a photo
            var photosCollection = _database.GetCollection<BsonDocument>("eventflow-photoreadmodel");
            var photoFilter = Builders<BsonDocument>.Filter.Eq("PhotoId", fileId);
            var photo = await photosCollection.Find(photoFilter).FirstOrDefaultAsync();

            if (photo == null)
            {
                RpcErrors.RpcErrors400.FileIdInvalid.ThrowRpcError();
            }
        }

        // Stored files are served by the FileServer; messenger cannot serve them directly
        _logger.LogWarning("GetFile called for stored file {FileId} - returning FileIdInvalid (should be served by FileServer)", fileId);
        RpcErrors.RpcErrors400.FileIdInvalid.ThrowRpcError();

        // Unreachable
        throw new InvalidOperationException();
    }

    private async Task<MyTelegram.Schema.Upload.IFile> HandleGroupCallStreamAsync(
        IRequestInput input,
        TInputGroupCallStream location,
        int limit)
    {
        if (location.Call is not TInputGroupCall inputGroupCall)
        {
            RpcErrors.RpcErrors400.LocationInvalid.ThrowRpcError();
            return null!;
        }

        var groupCalls = _database.GetCollection<GroupCallDocument>("group_calls");
        var groupCall = await groupCalls.Find(GroupCallStateHelper.Filter(inputGroupCall, input, _accessHashHelper)).FirstOrDefaultAsync();
        if (groupCall == null || !groupCall.Active)
        {
            RpcErrors.RpcErrors400.LocationInvalid.ThrowRpcError();
            return null!;
        }

        if (!GroupCallStateHelper.IsJoinedByUser(groupCall, input.UserId) && groupCall.CreatorId != input.UserId)
        {
            RpcErrors.RpcErrors400.GroupcallJoinMissing.ThrowRpcError();
            return null!;
        }

        _logger.LogInformation(
            "Handling upload.getFile inputGroupCallStream: ReqMsgId={ReqMsgId}, CallId={CallId}, TimeMs={TimeMs}, Scale={Scale}, VideoChannel={VideoChannel}, VideoQuality={VideoQuality}",
            input.ReqMsgId,
            groupCall.CallId,
            location.TimeMs,
            location.Scale,
            location.VideoChannel,
            location.VideoQuality);

        var bytes = await _hlsGroupCallStreamService.ReadPartAsync(groupCall, location);
        if (bytes.Length > limit)
        {
            bytes = bytes.Take(limit).ToArray();
        }

        _logger.LogInformation(
            "Returning upload.getFile inputGroupCallStream: ReqMsgId={ReqMsgId}, CallId={CallId}, Bytes={Bytes}",
            input.ReqMsgId,
            groupCall.CallId,
            bytes.Length);

        return new MyTelegram.Schema.Upload.TFile
        {
            Type = new MyTelegram.Schema.Storage.TFilePartial(),
            Mtime = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Bytes = bytes
        };
    }
}
