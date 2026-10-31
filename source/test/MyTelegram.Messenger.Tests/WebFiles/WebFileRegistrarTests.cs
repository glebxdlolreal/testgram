using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using MyTelegram.Messenger.Services.VideoProcessing;
using MyTelegram.Messenger.Services.WebFiles;
using MyTelegram.Messenger.Tests.Stats;

namespace MyTelegram.Messenger.Tests.WebFiles;

/// <summary>
/// Feature: registering a URL with the file server so a proxied <c>webDocument</c> can be read back.
///
/// <para>
/// <c>upload.getWebFile</c> is answered by the file server, and it only serves a web file it has
/// registered — an unknown URL comes back as <c>WEBDOCUMENT_INVALID</c>, which is an empty tile in the GIF
/// grid. Registration is a download on the file server, and clients re-query on every keystroke, so
/// "already registered" has to be remembered rather than re-discovered.
/// </para>
/// </summary>
public class WebFileRegistrarTests
{
    private const long UserId = 2_000_001;

    /// <summary>
    /// A host outside the proxy list is refused before any gRPC call: the file server would not fetch it
    /// either, and the result then goes out as <c>webDocumentNoProxy</c> so the client can try itself.
    /// </summary>
    [Fact]
    public async Task A_url_this_server_would_not_fetch_is_not_registered()
    {
        var registrar = Registrar(null!);

        (await registrar.EnsureRegisteredAsync(UserId, "https://evil.example/a.mp4", "video/mp4", 10, null))
            .ShouldBeFalse();
        (await registrar.EnsureRegisteredAsync(UserId, "http://media.tenor.com/a.mp4", "video/mp4", 10, null))
            .ShouldBeFalse();
        (await registrar.EnsureRegisteredAsync(UserId, null, "video/mp4", 10, null)).ShouldBeFalse();

        registrar.IsRegistered("https://evil.example/a.mp4").ShouldBeFalse();
    }

    /// <summary>
    /// A URL registered by an earlier run — or by another instance — is adopted from the shared cache
    /// instead of being sent to the file server again.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task A_url_registered_before_is_taken_from_the_cache()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var url = $"https://media.tenor.com/{Guid.NewGuid():N}/cat.mp4";

        await mongo.Database.GetCollection<BsonDocument>(WebFileRegistrar.CollectionName).InsertOneAsync(
            new BsonDocument
            {
                ["_id"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(url))),
                ["Url"] = url,
                ["MimeType"] = "video/mp4",
                ["FileId"] = 123L
            });

        var registrar = Registrar(mongo.Database);

        // No file server is reachable in a test, so a call to it would fail — reaching true proves the
        // cache was consulted first.
        (await registrar.EnsureRegisteredAsync(UserId, url, "video/mp4", 15136, null)).ShouldBeTrue();

        // And the answer is available synchronously afterwards, which is what the converter needs.
        registrar.IsRegistered(url).ShouldBeTrue();
    }

    /// <summary>
    /// With nothing cached and no file server to call, registration fails rather than throwing: a GIF
    /// search must still answer, with previews the client fetches itself.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task An_unreachable_file_server_is_reported_rather_than_thrown()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var url = $"https://media.tenor.com/{Guid.NewGuid():N}/dog.mp4";

        var registrar = Registrar(mongo.Database, "http://127.0.0.1:1");

        (await registrar.EnsureRegisteredAsync(UserId, url, "video/mp4", 15136, null)).ShouldBeFalse();
        registrar.IsRegistered(url).ShouldBeFalse();
    }

    /// <summary>
    /// The file server stores an image encrypted and then fails before writing the key, so it serves
    /// ciphertext; such a body is replaced with the plain one the first time the URL is seen.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task An_image_stored_without_its_key_is_stored_again_in_the_clear()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var url = $"https://telegram.org/file/{Guid.NewGuid():N}";
        await Remember(mongo.Database, url, "image/jpeg", 77);
        var storage = new Mock<IStoredFileStorage>();
        byte[]? stored = null;
        storage.Setup(s => s.UploadFileAsync(77, It.IsAny<string>(), It.IsAny<CancellationToken>(), null))
            .Callback<long, string, CancellationToken, string?>((_, path, _, _) => stored = File.ReadAllBytes(path))
            .Returns(Task.CompletedTask);

        (await Registrar(mongo.Database, fetcher: Fetcher([0xff, 0xd8, 0xff]), storage: storage.Object)
            .EnsureRegisteredAsync(UserId, url, "image/jpeg", 3, null)).ShouldBeTrue();

        stored.ShouldBe(new byte[] { 0xff, 0xd8, 0xff });
    }

    /// <summary>A body whose key the file server did keep is decrypted by it, so it is left alone.</summary>
    [RequiresMongoDbFact]
    public async Task A_body_with_its_key_and_a_video_are_left_alone()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var image = $"https://telegram.org/file/{Guid.NewGuid():N}";
        var video = $"https://media.tenor.com/{Guid.NewGuid():N}/cat.mp4";
        await Remember(mongo.Database, image, "image/jpeg", 78);
        await Remember(mongo.Database, video, "video/mp4", 79);
        await mongo.Database.GetCollection<BsonDocument>("eventflow-filereadmodel").InsertOneAsync(
            new BsonDocument { ["_id"] = "file-78", ["FileId"] = 78L, ["Key"] = new BsonBinaryData(new byte[32]) });
        var storage = new Mock<IStoredFileStorage>(MockBehavior.Strict);
        var registrar = Registrar(mongo.Database, fetcher: Fetcher([1]), storage: storage.Object);

        (await registrar.EnsureRegisteredAsync(UserId, image, "image/jpeg", 1, null)).ShouldBeTrue();
        (await registrar.EnsureRegisteredAsync(UserId, video, "video/mp4", 1, null)).ShouldBeTrue();
    }

    private static Task Remember(IMongoDatabase database, string url, string mimeType, long fileId) =>
        database.GetCollection<BsonDocument>(WebFileRegistrar.CollectionName).InsertOneAsync(new BsonDocument
        {
            ["_id"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(url))),
            ["Url"] = url,
            ["MimeType"] = mimeType,
            ["FileId"] = fileId
        });

    private static IWebFileFetcher Fetcher(byte[] body)
    {
        var fetcher = new Mock<IWebFileFetcher>();
        fetcher.Setup(f => f.IsAllowed(It.IsAny<string>())).Returns(true);
        fetcher.Setup(f => f.GetAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WebFileBody(body, "image/jpeg"));
        return fetcher.Object;
    }

    private static WebFileRegistrar Registrar(IMongoDatabase database,
        string fileServerUrl = "http://127.0.0.1:1", IWebFileFetcher? fetcher = null, IStoredFileStorage? storage = null)
    {
        var serverOptions = new MyTelegramMessengerServerOptions { FileServerGrpcServiceUrl = fileServerUrl };
        var options = new Mock<IOptionsMonitor<MyTelegramMessengerServerOptions>>(MockBehavior.Loose);
        options.SetupGet(p => p.CurrentValue).Returns(serverOptions);

        fetcher ??= new WebFileFetcher(null!, options.Object, NullLogger<WebFileFetcher>.Instance);

        return new WebFileRegistrar(database, fetcher, storage ?? new Mock<IStoredFileStorage>().Object, options.Object,
            NullLogger<WebFileRegistrar>.Instance);
    }
}
