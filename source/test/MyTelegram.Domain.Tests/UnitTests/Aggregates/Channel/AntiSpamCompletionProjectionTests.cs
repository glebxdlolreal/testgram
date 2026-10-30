using EventFlow.ReadStores;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MyTelegram.Domain.Aggregates.AntiSpamCompletion;
using MyTelegram.ReadModel.Impl;
using MyTelegram.ReadModel.ReadModelLocators;

namespace MyTelegram.Domain.Tests.UnitTests.Aggregates.Channel;

public class AntiSpamCompletionProjectionTests : TestsFor<AntiSpamCompletionAggregate>
{
    private const long Channel = MyTelegramConsts.ChannelInitId + 1;
    private static readonly string Operation = $"antispamcompletion-{Channel}-10-11111111111111111111111111111111";
    private static readonly string OtherOperation = $"antispamcompletion-{Channel}-9-22222222222222222222222222222222";

    public AntiSpamCompletionProjectionTests()
    {
        Fixture.Customize<AntiSpamCompletionId>(x => x.FromFactory(() => new AntiSpamCompletionId(Operation)));
    }

    private IDomainEvent<AntiSpamCompletionAggregate, AntiSpamCompletionId, T> Wrap<T>(T value, int version = 1)
        where T : class, IAggregateEvent<AntiSpamCompletionAggregate, AntiSpamCompletionId> =>
        (IDomainEvent<AntiSpamCompletionAggregate, AntiSpamCompletionId, T>)
        ADomainEvent<AntiSpamCompletionAggregate, AntiSpamCompletionId, T>(value, version);

    private static ChannelReadModel WithTop(int top) => BsonSerializer.Deserialize<ChannelReadModel>(
        new BsonDocument { { "ChannelId", Channel }, { "TopMessageId", top } });

    [Fact]
    public async Task Created_routes_to_exact_message_and_marks_it_for_deletion_on_every_delivery()
    {
        var created = Wrap(new AntiSpamCompletionCreatedEvent(Operation, Channel, 10, 20, 30, 40, 9));
        new MessageIdLocator().GetReadModelIds(created).ShouldBe([MessageId.Create(Channel, 10).Value]);
        var context = new Mock<IReadModelContext>();
        var message = new MessageReadModel();
        await message.ApplyAsync(context.Object, created, CancellationToken.None);
        await message.ApplyAsync(context.Object, created, CancellationToken.None);
        context.Verify(x => x.MarkForDeletion(), Times.Exactly(2));
    }

    [Fact]
    public async Task Replayed_creation_never_restores_immutable_top_candidate()
    {
        var channel = WithTop(10);
        var created = Wrap(new AntiSpamCompletionCreatedEvent(Operation, Channel, 10, 20, 30, 40, 9));
        await channel.ApplyAsync(null!, created, CancellationToken.None);
        channel.TopMessageId.ShouldBe(10);
        await channel.ApplyAsync(null!, Wrap(new AntiSpamCompletionTopRepairedEvent(Operation, Channel, 10, 8), 2), CancellationToken.None);
        await channel.ApplyAsync(null!, created, CancellationToken.None);
        channel.TopMessageId.ShouldBe(8);
    }

    [Theory]
    [InlineData(10, 9)]
    [InlineData(11, 11)]
    [InlineData(8, 8)]
    public async Task Repair_is_compare_and_swap_and_routes_only_to_its_channel(int currentTop, int expectedResult)
    {
        var channel = WithTop(currentTop);
        var repair = Wrap(new AntiSpamCompletionTopRepairedEvent(Operation, Channel, 10, 9));
        new ChannelReadModelLocator().GetReadModelIds(repair).ShouldBe([ChannelId.Create(Channel).Value]);
        new MessageIdLocator().GetReadModelIds(repair).ShouldBeEmpty();
        await channel.ApplyAsync(null!, repair, CancellationToken.None);
        await channel.ApplyAsync(null!, repair, CancellationToken.None);
        channel.TopMessageId.ShouldBe(expectedResult);
    }

    [Fact]
    public async Task Out_of_order_deletions_converge_with_fresh_repair_instead_of_replaying_creation()
    {
        var channel = WithTop(10);
        var deleteTen = Wrap(new AntiSpamCompletionCreatedEvent(Operation, Channel, 10, 20, 30, 40, 9));
        var deleteNine = Wrap(new AntiSpamCompletionCreatedEvent(OtherOperation, Channel, 9, 21, 31, 40, 8));
        await channel.ApplyAsync(null!, deleteNine, CancellationToken.None);
        await channel.ApplyAsync(null!, deleteTen, CancellationToken.None);
        // Both removals have projected before live history is queried: 8, not 9, is now live.
        await channel.ApplyAsync(null!, Wrap(new AntiSpamCompletionTopRepairedEvent(Operation, Channel, 10, 8), 2), CancellationToken.None);
        await channel.ApplyAsync(null!, deleteTen, CancellationToken.None);
        await channel.ApplyAsync(null!, Wrap(new AntiSpamCompletionTopRepairedEvent(OtherOperation, Channel, 9, 8), 2), CancellationToken.None);
        channel.TopMessageId.ShouldBe(8);
    }

    [Fact]
    public async Task Fresh_retry_can_repair_candidate_deleted_after_first_query_and_clear_empty_history()
    {
        var channel = WithTop(10);
        await channel.ApplyAsync(null!, Wrap(new AntiSpamCompletionTopRepairedEvent(Operation, Channel, 10, 9)), CancellationToken.None);
        // Authoritative post-projection validation finds 9 absent and requests a new repair.
        await channel.ApplyAsync(null!, Wrap(new AntiSpamCompletionTopRepairedEvent(Operation, Channel, 9, 0), 2), CancellationToken.None);
        channel.TopMessageId.ShouldBe(0);
    }

    [Fact]
    public void Aggregate_repair_is_resumable_and_preserves_immutable_completion()
    {
        Sut.CreateAntiSpamCompletion(Operation, Channel, 10, 20, 30, 40, 9);
        var created = Sut.Completion;
        Sut.RepairAntiSpamCompletionTop(10, 8);
        Sut.RepairAntiSpamCompletionTop(8, 7);
        Sut.RepairAntiSpamCompletionTop(8, 7);
        var repair = Sut.UncommittedEvents.Last().AggregateEvent.ShouldBeOfType<AntiSpamCompletionTopRepairedEvent>();
        repair.OperationId.ShouldBe(Operation);
        repair.ChannelId.ShouldBe(Channel);
        repair.ExpectedTopMessageId.ShouldBe(8);
        repair.NewTopMessageId.ShouldBe(7);
        Sut.Completion.ShouldBeSameAs(created);
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(0, 9)]
    public void Completion_rejects_channel_or_message_mismatching_operation_identity(int channelOffset, int messageId)
    {
        Assert.Throws<InvalidOperationException>(() =>
            Sut.CreateAntiSpamCompletion(Operation, Channel + channelOffset, messageId, 20, 30, 40, 8));
        Sut.Completion.ShouldBeNull();
        Sut.UncommittedEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Repair_requires_created_completion_and_cannot_restore_its_deleted_message()
    {
        Assert.Throws<InvalidOperationException>(() => Sut.RepairAntiSpamCompletionTop(10, 9));
        Sut.CreateAntiSpamCompletion(Operation, Channel, 10, 20, 30, 40, 9);
        Assert.Throws<InvalidOperationException>(() => Sut.RepairAntiSpamCompletionTop(9, 10));
        Assert.Throws<InvalidOperationException>(() => Sut.RepairAntiSpamCompletionTop(-1, 0));
    }
}
