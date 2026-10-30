using EventFlow.Queries;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using MyTelegram.Messenger.Services.Pts;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.Services.Services.IdGenerator;

namespace MyTelegram.Messenger.Tests.IdGenerator;

public class DurableChannelPtsAllocatorTests
{
    private const long Channel = MyTelegramConsts.ChannelInitId + 51;

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1000)]
    [InlineData(2, 2000)]
    [InlineData(2147483, 2147483000)]
    public void Bootstrap_reserves_the_entire_hilo_block(long high, long rawMaximum)
    {
        new ChannelPtsLegacySeed(Channel, high, 0).HighWatermark()
            .ShouldBe(checked((int)(rawMaximum + MyTelegramConsts.PtsInitId)));
    }

    [Fact]
    public void Bootstrap_honors_observed_maximum_and_rejects_overflow()
    {
        new ChannelPtsLegacySeed(Channel, 1, 3007).HighWatermark().ShouldBe(3007);
        Should.Throw<OverflowException>(() => new ChannelPtsLegacySeed(Channel, long.MaxValue, 0).HighWatermark());
        Should.Throw<OverflowException>(() => new ChannelPtsLegacySeed(Channel, 2147484, 0).HighWatermark());
        new ChannelPtsLegacySeed(Channel, 0, int.MaxValue).HighWatermark().ShouldBe(int.MaxValue);
    }

    [RequiresMongoDbFact]
    public async Task No_fence_means_no_allocations_or_automatic_bootstrap()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var sut = new DurableChannelPtsAllocator(mongo.Database);
        await Should.ThrowAsync<InvalidOperationException>(() => sut.ReserveAsync(Channel, "missing-fence"));
        (await mongo.Database.GetCollection<BsonDocument>(DurableChannelPtsAllocator.CounterCollection)
            .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty)).ShouldBe(0);
    }

    [RequiresMongoDbFact]
    public async Task Every_fault_boundary_recovers_from_persisted_state_with_a_fresh_allocator()
    {
        using var mongo = EmbeddedMongoServer.Start();
        foreach (var point in new[] { "before-reserve", "after-reserve", "before-journal", "after-journal", "before-clear", "after-clear" })
        {
            var db = mongo.Client.GetDatabase("pts_fault_" + Guid.NewGuid().ToString("N"));
            await new DurableChannelPtsAllocator(db).BootstrapQuiescedAsync(
                [new(Channel, 2, 1001)], "test-only complete quiesced inventory");
            var fired = false;
            var failing = new DurableChannelPtsAllocator(db, checkpoint =>
            {
                if (!fired && checkpoint == point)
                {
                    fired = true;
                    throw new SimulatedProcessFailure();
                }
                return Task.CompletedTask;
            });
            await Should.ThrowAsync<SimulatedProcessFailure>(() => failing.ReserveAsync(Channel, "stable", 3));
            fired.ShouldBeTrue();
            var restarted = new DurableChannelPtsAllocator(db);
            var pts = await restarted.ReserveAsync(Channel, "stable", 3);
            pts.ShouldBe(2003 + MyTelegramConsts.PtsInitId);
            (await new DurableChannelPtsAllocator(db).ReserveAsync(Channel, "stable", 3)).ShouldBe(pts);
            (await restarted.ReserveAsync(Channel, "next")).ShouldBe(pts + 1);
            var counter = await db.GetCollection<BsonDocument>(DurableChannelPtsAllocator.CounterCollection)
                .Find(new BsonDocument("_id", Channel)).SingleAsync();
            counter.Contains("Pending").ShouldBeFalse();
            (await db.GetCollection<BsonDocument>(DurableChannelPtsAllocator.JournalCollection)
                .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty)).ShouldBe(2);
        }
    }

    [RequiresMongoDbFact]
    public async Task Independent_writers_share_one_scalar_and_repeated_operation_is_allocated_once()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await new DurableChannelPtsAllocator(mongo.Database).BootstrapQuiescedAsync([], "empty isolated database");
        var tasks = Enumerable.Range(0, 30).Select(i => new DurableChannelPtsAllocator(mongo.Database)
            .ReserveAsync(Channel, i % 2 == 0 ? "shared" : "unique-" + i)).ToArray();
        var results = await Task.WhenAll(tasks);
        results.Where((_, i) => i % 2 == 0).Distinct().Count().ShouldBe(1);
        results.Distinct().Order().ToArray().ShouldBe(
            Enumerable.Range(MyTelegramConsts.PtsInitId + 1, 16).ToArray());
        await Should.ThrowAsync<InvalidOperationException>(() => new DurableChannelPtsAllocator(mongo.Database)
            .ReserveAsync(Channel, "shared", 2));
    }

    [RequiresMongoDbFact]
    public async Task Overflow_does_not_reserve_and_lost_response_does_not_allocate_again()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var sut = new DurableChannelPtsAllocator(mongo.Database);
        await sut.BootstrapQuiescedAsync([new(Channel, 0, int.MaxValue - 1)], "boundary inventory");
        (await sut.ReserveAsync(Channel, "last")).ShouldBe(int.MaxValue);
        (await new DurableChannelPtsAllocator(mongo.Database).ReserveAsync(Channel, "last")).ShouldBe(int.MaxValue);
        await Should.ThrowAsync<OverflowException>(() => sut.ReserveAsync(Channel, "overflow"));
        await Should.ThrowAsync<InvalidOperationException>(() => sut.BootstrapQuiescedAsync([], "must not reset"));
    }

    [Fact]
    public async Task General_generator_routes_both_channel_entry_points_but_leaves_user_pts_in_hilo()
    {
        var allocator = new Mock<IChannelPtsAllocator>();
        var operations = new List<string>();
        allocator.Setup(x => x.ReserveAsync(Channel, It.IsAny<string>(), 2, It.IsAny<CancellationToken>()))
            .Callback<long, string, int, CancellationToken>((_, op, _, _) => operations.Add(op))
            .ReturnsAsync(999);
        var helper = new HiLoStateBlockSizeHelper();
        var high = new Mock<IHiLoHighValueGenerator>();
        high.Setup(x => x.GetNewHighValueAsync(IdType.Pts, 2010001, It.IsAny<CancellationToken>())).ReturnsAsync(1);
        var generator = new global::MyTelegram.Messenger.Services.Impl.IdGenerator(
            new HiLoValueGeneratorCache(helper),
            new HiLoValueGeneratorFactory(NullLogger<DefaultHiLoValueGenerator>.Instance, high.Object),
            Mock.Of<IQueryProcessor>(), helper,
            NullLogger<global::MyTelegram.Messenger.Services.Impl.IdGenerator>.Instance, allocator.Object);
        (await generator.NextIdAsync(IdType.Pts, Channel, 2)).ShouldBe(999);
        (await generator.NextLongIdAsync(IdType.Pts, Channel, 2)).ShouldBe(999);
        operations.Distinct().Count().ShouldBe(2);
        (await generator.NextIdAsync(IdType.Pts, 2010001)).ShouldBe(MyTelegramConsts.PtsInitId + 1);
        allocator.Verify(x => x.ReserveAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [RequiresMongoDbFact]
    public async Task Discovery_includes_orphan_sequences_and_missing_high_channels_and_bootstrap_retries()
    {
        using var mongo = EmbeddedMongoServer.Start();
        // Isolated fixture setup only; production discovery never writes EventFlow collections.
        await mongo.Database.GetCollection<BsonDocument>("eventflow.counter").InsertManyAsync(
        [
            // "seq" is the field EventFlow 1.2.3 actually writes; "Seq" is the spelling an older
            // build left behind. Discovery must read both, which BSON's case-sensitive lookup makes
            // easy to get wrong.
            new() { { "_id", $"Pts-{Channel}" }, { "seq", 2 } },
            new() { { "_id", $"Pts-{Channel + 1}" }, { "Seq", 0 } }
        ]);
        await mongo.Database.GetCollection<BsonDocument>("eventflow-channelreadmodel").InsertOneAsync(
            new() { { "_id", "fixture" }, { "ChannelId", Channel + 2 }, { "Pts", 9 } });
        var sut = new DurableChannelPtsAllocator(mongo.Database);
        var discovered = await sut.DiscoverLegacyInventoryAsync();
        discovered.Count.ShouldBe(3);
        discovered.Single(x => x.ChannelId == Channel).HighWatermark().ShouldBe(2001);
        discovered.Single(x => x.ChannelId == Channel + 1).HighWatermark().ShouldBe(1);
        discovered.Single(x => x.ChannelId == Channel + 2).HighWatermark().ShouldBe(9);
        await Should.ThrowAsync<InvalidOperationException>(() => sut.BootstrapQuiescedAsync(
            discovered.Where(x => x.ChannelId != Channel).ToArray(), "incomplete"));
        foreach (var mismatchedHigh in new[] { 1L, 3L })
        {
            var mismatched = discovered.Select(x => x.ChannelId == Channel
                ? x with { PersistedHigh = mismatchedHigh } : x).ToArray();
            await Should.ThrowAsync<InvalidOperationException>(() => sut.BootstrapQuiescedAsync(mismatched, "mismatched high"));
        }
        var understated = discovered.Select(x => x.ChannelId == Channel + 2
            ? x with { ObservedPts = 8 } : x).ToArray();
        await Should.ThrowAsync<InvalidOperationException>(() => sut.BootstrapQuiescedAsync(understated, "understated observed Pts"));
        await Should.ThrowAsync<InvalidOperationException>(() => sut.ReserveAsync(Channel, "not-ready"));
        var failing = new DurableChannelPtsAllocator(mongo.Database, point => point == "bootstrap-after-seed"
            ? Task.FromException(new SimulatedProcessFailure()) : Task.CompletedTask);
        await Should.ThrowAsync<SimulatedProcessFailure>(() => failing.BootstrapQuiescedAsync(discovered, "partial"));
        await Should.ThrowAsync<InvalidOperationException>(() => sut.ReserveAsync(Channel, "still-not-ready"));
        await new DurableChannelPtsAllocator(mongo.Database).BootstrapQuiescedAsync(discovered, "complete retry");
        (await sut.ReserveAsync(Channel, "first")).ShouldBe(2002);
    }

    private sealed class SimulatedProcessFailure : Exception;
}
