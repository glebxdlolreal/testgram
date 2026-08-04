using MyTelegram.Domain.Aggregates.Poll;

namespace MyTelegram.Messenger.QueryServer.DomainEventHandlers;

/// <summary>
/// Broadcasts <c>updateMessagePoll</c> when a poll is closed, so every member's client stops
/// accepting votes and reveals the final results. Fires for both a manual stop (an edit with
/// <c>poll.closed = true</c>) and an automatic close at the poll's deadline.
/// </summary>
public class PollClosedDomainEventHandler(
    IObjectMessageSender objectMessageSender,
    ICommandBus commandBus,
    IIdGenerator idGenerator,
    IAckCacheService ackCacheService,
    IQueryProcessor queryProcessor,
    IPollConverterService pollConverterService)
    :
        DomainEventHandlerBase(objectMessageSender, commandBus, idGenerator, ackCacheService),
        ISubscribeSynchronousTo<PollAggregate, PollId, PollClosedEvent>
{
    public async Task HandleAsync(IDomainEvent<PollAggregate, PollId, PollClosedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var pollReadModel = await queryProcessor
            .ProcessAsync(new GetPollQuery(domainEvent.AggregateEvent.PollId), cancellationToken);
        if (pollReadModel == null)
        {
            return;
        }

        var toPeer = domainEvent.AggregateEvent.ToPeer;
        var msgId = await queryProcessor.ProcessAsync(
            new GetMessageIdByPollIdQuery(toPeer.PeerId, pollReadModel.PollId), cancellationToken);

        // The full poll is included (not just results) because the closed flag lives on the
        // poll itself. Results stay min: this copy goes to every member alike, so it carries
        // no per-user chosen state.
        var updates = pollConverterService.ToPollUpdates(pollReadModel, [], peer: toPeer, msgId: msgId,
            includePoll: true);

        await PushMessageToPeerAsync(toPeer, updates);
    }
}
