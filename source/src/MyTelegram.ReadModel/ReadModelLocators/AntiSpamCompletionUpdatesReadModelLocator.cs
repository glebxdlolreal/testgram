using MyTelegram.Domain.Aggregates.AntiSpamCompletion;

namespace MyTelegram.ReadModel.ReadModelLocators;

public interface IAntiSpamCompletionUpdatesReadModelLocator : IReadModelLocator
{
}

/// <summary>
/// Projects only antispam completions into the updates read model, where getChannelDifference finds the
/// updateDeleteChannelMessages. Those events are committed by the command server, which has no other
/// UpdatesReadModel projection; ordinary UpdatesAggregate events yield nothing here.
/// </summary>
public class AntiSpamCompletionUpdatesReadModelLocator : IAntiSpamCompletionUpdatesReadModelLocator, ITransientDependency
{
    public IEnumerable<string> GetReadModelIds(IDomainEvent domainEvent)
    {
        if (domainEvent.GetAggregateEvent() is AntiSpamCompletionCreatedEvent)
        {
            yield return domainEvent.GetIdentity().Value;
        }
    }
}
