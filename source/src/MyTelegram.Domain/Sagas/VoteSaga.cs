namespace MyTelegram.Domain.Sagas;

public class VoteSaga : MyInMemoryAggregateSaga<VoteSaga, VoteSagaId, VoteSagaLocator>,
    ISagaIsStartedBy<PollAggregate, PollId, VoteSucceededEvent>
{
    private readonly VoteState _state = new();

    public VoteSaga(VoteSagaId id,
        IEventStore eventStore) : base(id, eventStore)
    {
        Register(_state);
    }

    public Task HandleAsync(IDomainEvent<PollAggregate, PollId, VoteSucceededEvent> domainEvent,
        ISagaContext sagaContext,
        CancellationToken cancellationToken)
    {
        var options = domainEvent.AggregateEvent.Options;
        foreach (var option in options)
        {
            var correct = domainEvent.AggregateEvent.CorrectAnswers?.Contains(option);
            var command = new CreateVoteAnswerCommand(domainEvent.AggregateIdentity,
                domainEvent.AggregateEvent.PollId,
                domainEvent.AggregateEvent.VoteUserPeerId,
                option,
                correct ?? false);
            Publish(command);
        }

        // Each voter+option pair is its own read model document, so every retracted
        // option needs its own delete command carrying that option. This covers both a
        // full retraction (Options empty) and a re-vote, where the previous picks that
        // aren't part of the new selection must stop showing up in messages.getPollVotes.
        // Options still selected are skipped: their document is simply rewritten above.
        // PollState only drops the voter from VotedPeerIds once no picks remain, so a
        // re-vote keeps them counted as a voter.
        foreach (var retractedOption in domainEvent.AggregateEvent.RetractVoteOptions ?? [])
        {
            if (options.Contains(retractedOption))
            {
                continue;
            }

            var command = new DeleteVoteAnswerCommand(
                domainEvent.AggregateIdentity,
                domainEvent.AggregateEvent.PollId,
                domainEvent.AggregateEvent.VoteUserPeerId,
                retractedOption);
            Publish(command);
        }

        Emit(new VoteSagaCompletedSagaEvent(domainEvent.AggregateEvent.RequestInfo,
            domainEvent.AggregateEvent.PollId,
            domainEvent.AggregateEvent.VoteUserPeerId,
            domainEvent.AggregateEvent.Options,
            domainEvent.AggregateEvent.ToPeer));
        return Task.CompletedTask;
    }
}
