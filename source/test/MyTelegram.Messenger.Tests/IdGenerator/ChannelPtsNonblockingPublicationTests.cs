using MyTelegram.Messenger.Services.Pts;
using MyTelegram.Messenger.Tests.Stats;

namespace MyTelegram.Messenger.Tests.IdGenerator;

public sealed class ChannelPtsNonblockingPublicationTests
{
    [RequiresMongoDbFact]
    public async Task Foreign_completion_yields_without_releasing_owner_barrier()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var allocator = new DurableChannelPtsAllocator(mongo.Database);
        await allocator.BootstrapQuiescedAsync([], "isolated test");
        var channel = MyTelegramConsts.ChannelInitId + 555;
        var first = await allocator.TryReserveForPublicationAsync(channel, "owner");
        Assert.NotNull(first);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Null(await allocator.TryReserveForPublicationAsync(channel, "queued", cancellationToken: timeout.Token));
        Assert.NotNull(await allocator.GetReadablePtsCeilingAsync(channel));
        Assert.Equal(first, await allocator.TryReserveForPublicationAsync(channel, "owner"));
        await allocator.MarkPublishedAsync(channel, "owner", first.Value);
        Assert.Null(await allocator.GetReadablePtsCeilingAsync(channel));
        Assert.Equal(first.Value + 1, await allocator.TryReserveForPublicationAsync(channel, "queued"));
    }
}
