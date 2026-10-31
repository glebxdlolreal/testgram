using EventFlow.Queries;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MyTelegram.Queries;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Services.Services.IdGenerator;
using MyTelegram.Messenger.Tests.Stats;

namespace MyTelegram.Messenger.Tests.IdGenerator;

/// <summary>
/// A message id must never be handed out twice. The generator seeds itself from what is stored after
/// a restart; when the newest message had been deleted, the message read model alone no longer held
/// its id, the id was reissued and the next send failed with <c>AggregateIsNewSpecification</c>.
/// </summary>
public class MessageIdSeedTests
{
    private const long OwnerId = 2010001;

    [Theory]
    [InlineData(32290, 32294, 32295)] // the newest message (32294) was deleted
    [InlineData(32294, 32294, 32295)]
    [InlineData(32294, 0, 32295)]     // no pts record
    public async Task The_first_id_after_a_restart_follows_the_highest_ever_issued(int storedMax, int ptsMax, long expected)
    {
        var queries = new Mock<IQueryProcessor>();
        queries.Setup(q => q.ProcessAsync(It.IsAny<GetMaxMessageIdByPeerIdQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(storedMax);
        var pts = new Mock<IPtsReadModel>();
        pts.SetupGet(p => p.MaxMessageId).Returns(ptsMax);
        queries.Setup(q => q.ProcessAsync(It.IsAny<GetPtsByPeerIdQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ptsMax > 0 ? pts.Object : null);

        var blockSize = new Mock<IHiLoStateBlockSizeHelper>();
        blockSize.Setup(b => b.GetBlockSize(It.IsAny<IdType>())).Returns(1);
        var high = new Mock<IHiLoHighValueGenerator>();
        var next = 0L;
        high.Setup(h => h.GetNewHighValueAsync(It.IsAny<IdType>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref next) + Math.Max(storedMax, ptsMax));
        var generator = new global::MyTelegram.Messenger.Services.Impl.IdGenerator(
            new HiLoValueGeneratorCache(blockSize.Object),
            new HiLoValueGeneratorFactory(NullLogger<DefaultHiLoValueGenerator>.Instance, high.Object),
            queries.Object, blockSize.Object, NullLogger<global::MyTelegram.Messenger.Services.Impl.IdGenerator>.Instance);

        (await generator.NextLongIdAsync(IdType.MessageId, OwnerId)).ShouldBe(expected);
    }
}

/// <summary>The shared counter behind message ids, as the command and query servers both use it.</summary>
public class MessageIdAllocatorTests
{
    [RequiresMongoDbFact]
    public async Task Two_processes_never_hand_out_the_same_id_and_never_go_below_what_is_stored()
    {
        using var mongo = MyTelegram.Messenger.Tests.Stats.EmbeddedMongoServer.Start();
        var commandServer = new global::MyTelegram.Messenger.Services.Impl.MongoMessageIdAllocator(mongo.Database);
        var queryServer = new global::MyTelegram.Messenger.Services.Impl.MongoMessageIdAllocator(mongo.Database);

        var ids = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await commandServer.NextAsync(2010001, 1, () => Task.FromResult(32294L)));
            // The query server last saw an older maximum (a deleted message): it must not rewind.
            ids.Add(await queryServer.NextAsync(2010001, 1, () => Task.FromResult(32290L)));
        }

        ids.ShouldBe(Enumerable.Range(32295, 10).Select(i => (long)i));
        (await commandServer.NextAsync(2010001, 3, () => Task.FromResult(0L))).ShouldBe(32305);
        (await queryServer.NextAsync(2010001, 1, () => Task.FromResult(0L))).ShouldBe(32308);
    }
}
