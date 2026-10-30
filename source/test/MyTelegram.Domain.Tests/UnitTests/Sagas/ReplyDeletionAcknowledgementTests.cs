namespace MyTelegram.Domain.Tests.UnitTests.Sagas;

public class ReplyDeletionAcknowledgementTests : TestsFor<DeleteReplyMessagesSaga>
{
    private readonly Mock<IIdGenerator> _ids;

    public ReplyDeletionAcknowledgementTests()
    {
        Fixture.Customize<DeleteReplyMessagesSagaId>(c => c.FromFactory(() => new DeleteReplyMessagesSagaId("deletereplymessagessaga-" + Guid.NewGuid())));
        _ids = InjectMock<IIdGenerator>();
        _ids.Setup(x => x.NextIdAsync(IdType.Pts, 100, 1, It.IsAny<CancellationToken>())).ReturnsAsync(50);
    }

    [Fact]
    public async Task All_acknowledged_replies_do_not_emit_deletion_completion_or_increment_pts()
    {
        Start([1]);
        await Acknowledge(1);
        Sut.UncommittedEvents.ShouldNotContain(e => e.AggregateEvent is DeleteReplyMessagesCompletedSagaEvent);
        _ids.Verify(x => x.NextIdAsync(It.IsAny<IdType>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Mixed_reply_batch_completes_with_only_actual_deletions()
    {
        Start([1, 2]);
        await Acknowledge(1);
        await Acknowledge(1);
        await Sut.HandleAsync(ADomainEvent<MessageAggregate, MessageId, ChannelMessageDeletedEvent>(
            new ChannelMessageDeletedEvent(A<RequestInfo>(), 100, 2, false, null, null, null), MessageId.Create(100, 2), 1),
            Moq.Mock.Of<ISagaContext>(), CancellationToken.None);
        var completed = Sut.UncommittedEvents.Last().AggregateEvent.ShouldBeOfType<DeleteReplyMessagesCompletedSagaEvent>();
        completed.MessageIds.ShouldBe([2]);
        completed.PtsCount.ShouldBe(1);
        completed.Pts.ShouldBe(50);
    }

    private void Start(List<int> ids) => Sut.ApplyEvents([
        ADomainEvent<DeleteReplyMessagesSaga, DeleteReplyMessagesSagaId, DeleteReplyMessagesSagaStartedSagaEvent>(
            new DeleteReplyMessagesSagaStartedSagaEvent(A<RequestInfo>(), 100, ids, 0), 1)
    ]);

    private Task Acknowledge(int id) => Sut.HandleAsync(
        ADomainEvent<MessageAggregate, MessageId, ChannelMessageDeletionAcknowledgedEvent>(
            new ChannelMessageDeletionAcknowledgedEvent(A<RequestInfo>(), 100, id, Guid.NewGuid()), MessageId.Create(100, id), 1),
        Moq.Mock.Of<ISagaContext>(), CancellationToken.None);
}
