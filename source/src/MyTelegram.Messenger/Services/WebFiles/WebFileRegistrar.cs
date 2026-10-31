using System.Collections.Concurrent;
using Google.Protobuf;
using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.GrpcService;
using MyTelegram.Messenger.Services.VideoProcessing;

namespace MyTelegram.Messenger.Services.WebFiles;

/// <summary>
/// Makes a URL readable through <c>upload.getWebFile</c>.
///
/// <para>That method belongs to the file server, and it answers only for a web file it can look up:
/// <c>GetWebFileHandler</c> turns the URL into a file id with <c>WebFileHelper.GenerateFileId</c>, queries
/// <c>GetWebFileByFileIdQuery</c>, and throws <c>WEBDOCUMENT_INVALID</c> when nothing comes back. Its own
/// gRPC <c>SaveWebFile</c> is supposed to create that record, and it does download and store the body —
/// but creating the record fails inside its image:</para>
///
/// <code>
/// System.InvalidOperationException: Reflection-based serialization has been disabled for this application
///    at MyTelegram.FileServer.Services.WebFileDownloader.DownloadAsync
/// </code>
///
/// <para>The file server is a native-AOT build with no source, and the reflection its <c>WebFile</c>
/// aggregate needs was trimmed out of it, so no argument to <c>SaveWebFile</c> produces the record —
/// <c>eventflow.events</c> never gets a <c>WebFileAggregate</c> entry. This class therefore does the half
/// that binary cannot: it asks the file server to fetch and store the body, then writes the read model
/// row its query reads. That is a deliberate exception to the rule against writing <c>eventflow-*</c>
/// directly — the aggregate that owns this read model lives in a closed binary whose write path is
/// broken, so there is no other way for a proxied <c>webDocument</c> to be readable at all.</para>
///
/// <para>For an image the file server fails one step earlier still: <c>DownloadPhotoAsync</c> stores the
/// body AES-CTR encrypted and then dies serializing <c>WebFileCreatedEvent</c> (missing AOT metadata), so
/// the key it encrypted with is never written to <c>eventflow-filereadmodel</c> and <c>upload.getWebFile</c>
/// hands out the ciphertext as it is. Such a body is replaced with the plain one, which the file server
/// serves unchanged when it has no key — the same way every server-created body is stored.</para>
///
/// <para>Registration is remembered, per process and in Mongo, because the same preview URL comes back on
/// every keystroke of a GIF search and each fresh registration is a download on the file server.</para>
/// </summary>
public interface IWebFileRegistrar
{
    /// <summary>
    /// Registers <paramref name="url"/> unless it already is, and reports whether it may now be handed
    /// out as a proxied web document.
    /// </summary>
    /// <param name="size">
    /// Byte size of the body as the origin reports it. It becomes <c>upload.webFile.size</c>, which is how
    /// a client knows when it has read the whole file.
    /// </param>
    Task<bool> EnsureRegisteredAsync(long userId, string? url, string mimeType, int size,
        TVector<IDocumentAttribute>? attributes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether this process has already registered <paramref name="url"/>. Answered without waiting,
    /// because the conversion that needs to know is synchronous.
    /// </summary>
    bool IsRegistered(string? url);
}

/// <inheritdoc />
public class WebFileRegistrar(
    IMongoDatabase mongoDatabase,
    IWebFileFetcher fetcher,
    IStoredFileStorage storedFileStorage,
    IOptionsMonitor<MyTelegramMessengerServerOptions> options,
    ILogger<WebFileRegistrar> logger)
    : IWebFileRegistrar, ISingletonDependency
{
    public const string CollectionName = "web_file_registrations";

    /// <summary>
    /// Where the file server's <c>GetWebFileByFileIdQuery</c> reads from — EventFlow names a read model
    /// collection <c>eventflow-{type}</c>, and the query is a plain <c>FileId</c> match.
    /// </summary>
    public const string ReadModelCollectionName = "eventflow-webfilereadmodel";

    /// <summary>Where the file server keeps the AES key of a body it stored encrypted.</summary>
    private const string FileReadModelCollectionName = "eventflow-filereadmodel";

    private static readonly ConcurrentDictionary<string, bool> Registered = new(StringComparer.Ordinal);

    public bool IsRegistered(string? url)
    {
        return !string.IsNullOrEmpty(url) && Registered.ContainsKey(url);
    }

    public async Task<bool> EnsureRegisteredAsync(long userId, string? url, string mimeType, int size,
        TVector<IDocumentAttribute>? attributes, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(url) || !fetcher.IsAllowed(url))
        {
            return false;
        }

        if (Registered.ContainsKey(url))
        {
            return true;
        }

        if (await WasRegisteredBeforeAsync(url, cancellationToken) is { } registeredFileId)
        {
            await EnsureReadableBodyAsync(registeredFileId, url, mimeType, cancellationToken);
            Registered[url] = true;

            return true;
        }

        try
        {
            var client = GrpcClientFactory.CreateMediaServiceClient(
                options.CurrentValue.FileServerGrpcServiceUrl);

            // The file id is derived from the URL, so this answers the same value however often it is
            // called; a repeat call also trips the file server's own "aggregate is not new" guard, which
            // it logs and swallows, and the body it already stored stays valid.
            var response = await client.SaveWebFileAsync(new SaveWebFileRequest
            {
                Url = url,
                UserId = userId,
                IsPhoto = false,
                MimeType = mimeType,
                Attributes = ByteString.CopyFrom((attributes ?? new TVector<IDocumentAttribute>()).ToBytes())
            }, cancellationToken: cancellationToken).ResponseAsync;

            if (response.FileId == 0)
            {
                logger.LogWarning("The file server stored no web file for a proxied URL");

                return false;
            }

            await WriteReadModelAsync(response.FileId, url, mimeType, size, cancellationToken);
            await EnsureReadableBodyAsync(response.FileId, url, mimeType, cancellationToken);

            Registered[url] = true;
            await RememberAsync(url, mimeType, response.FileId, cancellationToken);

            return true;
        }
        catch (Exception ex)
        {
            // A URL that could not be registered simply goes out as webDocumentNoProxy, which is worse
            // for the clients that cannot fetch it themselves but better than media that cannot be read
            // at all.
            logger.LogWarning(ex, "A web file could not be registered with the file server");

            return false;
        }
    }

    /// <summary>
    /// The row the file server's own aggregate would have written. Upserted on <c>FileId</c> rather than
    /// on the document id, because the file id is what its query matches and what makes the row usable.
    /// </summary>
    private async Task WriteReadModelAsync(long fileId, string url, string mimeType, int size,
        CancellationToken cancellationToken)
    {
        await mongoDatabase.GetCollection<BsonDocument>(ReadModelCollectionName).ReplaceOneAsync(
            Builders<BsonDocument>.Filter.Eq("FileId", fileId),
            new BsonDocument
            {
                ["_id"] = $"webfile-{fileId}",
                ["FileId"] = fileId,
                ["Url"] = url,
                ["MimeType"] = mimeType,
                ["Size"] = size,
                ["Version"] = 1L
            },
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);
    }

    /// <summary>The file id an earlier registration of <paramref name="url"/> got, or <c>null</c> for none.</summary>
    private async Task<long?> WasRegisteredBeforeAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var existing = await Collection()
                .Find(Builders<BsonDocument>.Filter.Eq("_id", CacheId(url)))
                .FirstOrDefaultAsync(cancellationToken);

            return existing == null ? null : existing.TryGetValue("FileId", out var fileId) && fileId.IsNumeric ? fileId.ToInt64() : 0;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "The web file registration cache could not be read");

            return null;
        }
    }

    /// <summary>
    /// Replaces an image body the file server stored encrypted without keeping its key (see the class
    /// summary) with the plain body. A body that has its key is left alone: the file server decrypts it.
    /// </summary>
    private async Task EnsureReadableBodyAsync(long fileId, string url, string mimeType,
        CancellationToken cancellationToken)
    {
        if (fileId == 0 || !mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string? path = null;
        try
        {
            var keyed = await mongoDatabase.GetCollection<BsonDocument>(FileReadModelCollectionName)
                .Find(Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("FileId", fileId),
                    Builders<BsonDocument>.Filter.Exists("Key")))
                .AnyAsync(cancellationToken);
            if (keyed)
            {
                return;
            }

            var body = await fetcher.GetAsync(url, mimeType, cancellationToken);
            if (body == null)
            {
                logger.LogWarning("The web file {Url} could not be fetched to store it readable", url);
                return;
            }

            path = Path.GetTempFileName();
            await File.WriteAllBytesAsync(path, body.Bytes, cancellationToken);
            await storedFileStorage.UploadFileAsync(fileId, path, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The web file {Url} could not be stored readable", url);
        }
        finally
        {
            if (path != null)
            {
                File.Delete(path);
            }
        }
    }

    private async Task RememberAsync(string url, string mimeType, long fileId,
        CancellationToken cancellationToken)
    {
        try
        {
            await Collection().ReplaceOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", CacheId(url)),
                new BsonDocument
                {
                    ["_id"] = CacheId(url),
                    ["Url"] = url,
                    ["MimeType"] = mimeType,
                    ["FileId"] = fileId,
                    ["Date"] = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                },
                new ReplaceOptions { IsUpsert = true },
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "The web file registration cache could not be written");
        }
    }

    private IMongoCollection<BsonDocument> Collection()
    {
        return mongoDatabase.GetCollection<BsonDocument>(CollectionName);
    }

    private static string CacheId(string url)
    {
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(url)));
    }
}
