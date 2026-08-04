using MyTelegram.Domain.Aggregates.Poll;

namespace MyTelegram.Messenger.QueryServer.DomainEventHandlers;

/// <summary>
/// Broadcasts <c>updateMessagePoll</c> when the answer list of an open poll changes, so clients
/// re-render the options. The poll object itself is included: the change is to the answers, not
/// just the tallies.
/// </summary>
public class PollAnswerChangedDomainEventHandler(
    IObjectMessageSender objectMessageSender,
    ICommandBus commandBus,
    IIdGenerator idGenerator,
    IAckCacheService ackCacheService,
    IQueryProcessor queryProcessor,
    IPollConverterService pollConverterService)
    :
        DomainEventHandlerBase(objectMessageSender, commandBus, idGenerator, ackCacheService),
        ISubscribeSynchronousTo<PollAggregate, PollId, PollAnswerAddedEvent>,
        ISubscribeSynchronousTo<PollAggregate, PollId, PollAnswerRemovedEvent>
{
    public Task HandleAsync(IDomainEvent<PollAggregate, PollId, PollAnswerAddedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        return PushPollUpdatesAsync(domainEvent.AggregateEvent.PollId, domainEvent.AggregateEvent.ToPeer,
            cancellationToken);
    }

    public Task HandleAsync(IDomainEvent<PollAggregate, PollId, PollAnswerRemovedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        return PushPollUpdatesAsync(domainEvent.AggregateEvent.PollId, domainEvent.AggregateEvent.ToPeer,
            cancellationToken);
    }

    private async Task PushPollUpdatesAsync(long pollId, Peer toPeer, CancellationToken cancellationToken)
    {
        var pollReadModel = await queryProcessor.ProcessAsync(new GetPollQuery(pollId), cancellationToken);
        if (pollReadModel == null)
        {
            return;
        }

        var msgId = await queryProcessor.ProcessAsync(
            new GetMessageIdByPollIdQuery(toPeer.PeerId, pollId), cancellationToken);

        var updates = pollConverterService.ToPollUpdates(pollReadModel, [], peer: toPeer, msgId: msgId,
            includePoll: true);

        await PushMessageToPeerAsync(toPeer, updates);
    }
}
