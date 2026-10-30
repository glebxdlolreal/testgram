namespace MyTelegram.Domain.Tests.UnitTests.Sagas;

public class ChannelDeletionAcknowledgementTests : TestsFor<DeleteChannelMessagesSaga>
{
    private readonly Mock<IIdGenerator> _ids;

    public ChannelDeletionAcknowledgementTests()
    {
        Fixture.Customize<DeleteChannelMessagesSagaId>(c => c.FromFactory(() => new DeleteChannelMessagesSagaId("deletechannelmessagessaga-" + Guid.NewGuid())));
        _ids = InjectMock<IIdGenerator>();
        _ids.Setup(x => x.NextIdAsync(IdType.Pts, 100, 1, It.IsAny<CancellationToken>())).ReturnsAsync(50);
    }

    [Fact]
    public async Task All_acknowledged_completes_without_pts_or_deletion_completion()
    {
        Start([1]);
        await Acknowledge(1);
        _ids.Verify(x => x.NextIdAsync(It.IsAny<IdType>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        Sut.UncommittedEvents.ShouldNotContain(e => e.AggregateEvent is DeleteChannelMessagesCompletedSagaEvent);
        Sut.UncommittedEvents.Last().AggregateEvent.ShouldBeOfType<DeleteChannelMessagesAlreadyDeletedSagaEvent>();
    }

    [Fact]
    public async Task Mixed_batch_only_counts_actual_deletion_and_duplicate_ack_does_not_finish_early()
    {
        Start([1, 2]);
        await Acknowledge(1);
        await Acknowledge(1);
        Sut.UncommittedEvents.ShouldNotContain(e => e.AggregateEvent is DeleteChannelMessagesCompletedSagaEvent);
        await Sut.HandleAsync(ADomainEvent<MessageAggregate, MessageId, ChannelMessageDeletedEvent>(
            new ChannelMessageDeletedEvent(A<RequestInfo>(), 100, 2, false, null, null, null), MessageId.Create(100, 2), 1),
            Moq.Mock.Of<ISagaContext>(), CancellationToken.None);
        var completion = Sut.UncommittedEvents.Last().AggregateEvent.ShouldBeOfType<DeleteChannelMessagesCompletedSagaEvent>();
        completion.MessageIds.ShouldBe([2]);
        completion.PtsCount.ShouldBe(1);
        completion.Pts.ShouldBe(50);
        _ids.Verify(x => x.NextIdAsync(IdType.Pts, 100, 1, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task History_all_acknowledged_uses_ack_completion_not_empty_history_deletion()
    {
        Start([1], true);
        await Acknowledge(1);
        var completion = Sut.UncommittedEvents.Last().AggregateEvent.ShouldBeOfType<DeleteChannelMessagesAlreadyDeletedSagaEvent>();
        completion.IsDeleteHistory.ShouldBeTrue();
        Sut.UncommittedEvents.ShouldNotContain(e => e.AggregateEvent is DeleteChannelHistoryCompletedSagaEvent);
    }

    private void Start(List<int> ids, bool history = false)
    {
        Sut.ApplyEvents([
            ADomainEvent<DeleteChannelMessagesSaga, DeleteChannelMessagesSagaId, DeleteChannelMessagesSagaStartedSagaEvent>(
                new DeleteChannelMessagesSagaStartedSagaEvent(A<RequestInfo>(), 100, ids, 0, null, history, null, null, null), 1)
        ]);
    }

    private Task Acknowledge(int id) => Sut.HandleAsync(
        ADomainEvent<MessageAggregate, MessageId, ChannelMessageDeletionAcknowledgedEvent>(
            new ChannelMessageDeletionAcknowledgedEvent(A<RequestInfo>(), 100, id, Guid.NewGuid()), MessageId.Create(100, id), 1),
        Moq.Mock.Of<ISagaContext>(), CancellationToken.None);
}
