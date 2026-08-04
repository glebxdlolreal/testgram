namespace MyTelegram.ReadModel.ReadModelLocators;

public class PollAnswerVoterReadModelLocator : IPollAnswerVoterReadModelLocator, ITransientDependency
{
    public IEnumerable<string> GetReadModelIds(IDomainEvent domainEvent)
    {
        var aggregateEvent = domainEvent.GetAggregateEvent();
        switch (aggregateEvent)
        {
            // Keyed by (voter, option) so multiple-choice picks each get their own document.
            case VoteAnswerCreatedEvent voteAnswerCreatedEvent:
                yield return
                    $"{domainEvent.GetIdentity().Value}_{voteAnswerCreatedEvent.VoterPeerId}_{voteAnswerCreatedEvent.Option}";
                break;
            case VoteAnswerDeletedEvent voteAnswerDeletedEvent:
                yield return
                    $"{domainEvent.GetIdentity().Value}_{voteAnswerDeletedEvent.VoterPeerId}_{voteAnswerDeletedEvent.Option}";
                break;
            // An answer removed from an open poll takes every vote cast for it with it.
            case PollAnswerRemovedEvent pollAnswerRemovedEvent:
                foreach (var voterPeerId in pollAnswerRemovedEvent.AllVoterPeerIds)
                {
                    yield return
                        $"{domainEvent.GetIdentity().Value}_{voterPeerId}_{pollAnswerRemovedEvent.Answer.Option}";
                }

                break;
        }
    }
}
