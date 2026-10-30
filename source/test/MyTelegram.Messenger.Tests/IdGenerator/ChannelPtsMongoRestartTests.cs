using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Pts;
using MyTelegram.Messenger.Tests.Stats;

namespace MyTelegram.Messenger.Tests.IdGenerator;

public class ChannelPtsMongoRestartTests
{
    [RequiresMongoDbFact]
    public async Task Journaled_reservations_survive_real_mongod_process_restarts_at_each_boundary()
    {
        using var mongo = new RestartableMongo();
        const long channel = MyTelegramConsts.ChannelInitId + 71;
        foreach (var boundary in new[] { "before-reserve", "after-reserve", "before-journal", "after-journal", "before-clear", "after-clear" })
        {
            var databaseName = "pts_" + Guid.NewGuid().ToString("N");
            var db = mongo.Database(databaseName);
            await new DurableChannelPtsAllocator(db).BootstrapQuiescedAsync([new(channel, 1, 7)], "isolated quiesced inventory");
            var failing = new DurableChannelPtsAllocator(db, point =>
                point == boundary ? Task.FromException(new IOException("process failed")) : Task.CompletedTask);
            await Should.ThrowAsync<IOException>(() => failing.ReserveAsync(channel, "persist", 2));
            mongo.Restart();
            var recovered = new DurableChannelPtsAllocator(mongo.Database(databaseName));
            (await recovered.ReserveAsync(channel, "persist", 2)).ShouldBe(1003);
            await Should.ThrowAsync<InvalidOperationException>(() => recovered.ReserveAsync(channel, "persist", 3));
            (await recovered.ReserveAsync(channel, "next")).ShouldBe(1004);
            (await recovered.ReserveAsync(channel, "persist", 2)).ShouldBe(1003);
        }
    }

    [RequiresMongoDbFact]
    public async Task Publication_barrier_survives_reservation_and_acknowledgement_crashes()
    {
        using var mongo = new RestartableMongo();
        const long channel = MyTelegramConsts.ChannelInitId + 72;
        foreach (var boundary in new[] { "after-reserve", "after-journal", "after-clear", "before-publication-ack", "after-publication-ack" })
        {
            var name = "publication_" + Guid.NewGuid().ToString("N");
            var db = mongo.Database(name);
            await new DurableChannelPtsAllocator(db).BootstrapQuiescedAsync([new(channel, 1, 7)], "isolated inventory");
            var failing = new DurableChannelPtsAllocator(db, point => point == boundary
                ? Task.FromException(new IOException("process failed")) : Task.CompletedTask);
            if (boundary.Contains("ack"))
            {
                (await failing.ReserveForPublicationAsync(channel, "native", 2)).ShouldBe(1003);
                await Should.ThrowAsync<IOException>(() => failing.MarkPublishedAsync(channel, "native", 1003));
            }
            else
                await Should.ThrowAsync<IOException>(() => failing.ReserveForPublicationAsync(channel, "native", 2));
            mongo.Restart();
            var recovered = new DurableChannelPtsAllocator(mongo.Database(name));
            (await recovered.ReserveForPublicationAsync(channel, "native", 2)).ShouldBe(1003);
            await Should.ThrowAsync<InvalidOperationException>(() => recovered.ReserveAsync(channel, "native", 2));
            await Should.ThrowAsync<InvalidOperationException>(() => recovered.ReserveForPublicationAsync(channel, "native", 3));
            await Should.ThrowAsync<InvalidOperationException>(() => recovered.MarkPublishedAsync(channel, "native", 1004));
            if (boundary != "after-publication-ack")
            {
                (await recovered.GetReadablePtsCeilingAsync(channel)).ShouldBe(1001);
                using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                await Should.ThrowAsync<OperationCanceledException>(() => recovered.ReserveAsync(channel, "blocked", cancellationToken: cancel.Token));
                (await recovered.GetReadablePtsCeilingAsync(channel)).ShouldBe(1001);
            }
            await recovered.MarkPublishedAsync(channel, "native", 1003);
            await recovered.MarkPublishedAsync(channel, "native", 1003);
            (await recovered.GetReadablePtsCeilingAsync(channel)).ShouldBeNull();
            (await recovered.ReserveForPublicationAsync(channel, "next-native")).ShouldBe(1004);
            await recovered.MarkPublishedAsync(channel, "native", 1003);
            (await recovered.GetReadablePtsCeilingAsync(channel)).ShouldBe(1003);
            var waiting = recovered.ReserveAsync(channel, "ordinary");
            await Task.Delay(150);
            waiting.IsCompleted.ShouldBeFalse();
            await recovered.MarkPublishedAsync(channel, "next-native", 1004);
            (await waiting.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBe(1005);
        }
    }

    private sealed class RestartableMongo : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "channel_pts_restart_" + Guid.NewGuid().ToString("N"));
        private readonly int _port;
        private Process? _process;
        private MongoClient _client = null!;

        public RestartableMongo()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            _port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            Directory.CreateDirectory(_directory);
            try { Start(); }
            catch { Dispose(); throw; }
        }

        public IMongoDatabase Database(string name) => _client.GetDatabase(name);

        public void Restart()
        {
            Stop();
            Start();
        }

        private void Start()
        {
            var info = new ProcessStartInfo(EmbeddedMongoServer.LocateMongod()!)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var arg in new[] { "--dbpath", _directory, "--port", _port.ToString(), "--bind_ip", "127.0.0.1", "--wiredTigerCacheSizeGB", "0.25" })
                info.ArgumentList.Add(arg);
            _process = Process.Start(info) ?? throw new IOException("mongod did not start");
            _process.OutputDataReceived += (_, _) => { };
            _process.ErrorDataReceived += (_, _) => { };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            _client = new MongoClient($"mongodb://127.0.0.1:{_port}/?directConnection=true&serverSelectionTimeoutMS=1000");
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (true)
            {
                if (_process.HasExited) throw new IOException("mongod exited during restart");
                try { _client.GetDatabase("admin").RunCommand<BsonDocument>(new BsonDocument("ping", 1)); return; }
                catch (MongoException) when (DateTime.UtcNow < deadline) { Thread.Sleep(100); }
            }
        }

        private void Stop()
        {
            if (_process == null) return;
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                if (!_process.WaitForExit(10000)) throw new IOException("mongod did not exit");
            }
            _process.Dispose();
            _process = null;
        }

        public void Dispose()
        {
            Stop();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
