namespace MyTelegram.ReadModel.Impl;

public class PollReadModel : IPollReadModel,
    IAmReadModelFor<PollAggregate, PollId, PollCreatedEvent>,
    IAmReadModelFor<PollAggregate, PollId, VoteSucceededEvent>,
    IAmReadModelFor<PollAggregate, PollId, PollAnswerAddedEvent>,
    IAmReadModelFor<PollAggregate, PollId, PollAnswerRemovedEvent>,
    IAmReadModelFor<PollAggregate, PollId, PollClosedEvent>
{
    public IReadOnlyCollection<PollAnswer> Answers { get; private set; } = default!;
    public IReadOnlyCollection<PollAnswerVoter>? AnswerVoters { get; private set; }
    public IList<IMessageEntity>? SolutionEntities2 { get; private set; }
    public bool Closed { get; private set; }
    public int? CloseDate { get; private set; }
    public int? ClosePeriod { get; private set; }
    public IReadOnlyCollection<string>? CorrectAnswers { get; private set; }
    public virtual string Id { get; private set; } = null!;
    public bool MultipleChoice { get; private set; }
    public long PollId { get; private set; }
    public bool PublicVoters { get; private set; }
    public string Question { get; private set; } = default!;
    public byte[]? QuestionEntities { get; private set; }
    public IList<IMessageEntity>? QuestionEntities2 { get; private set; }
    public bool Quiz { get; private set; }
    public string? Solution { get; private set; }
    public byte[]? SolutionEntities { get; private set; }
    public long ToPeerId { get; private set; }
    public int TotalVoters { get; private set; }
    public bool OpenAnswers { get; private set; }
    public bool RevotingDisabled { get; private set; }
    public bool ShuffleAnswers { get; private set; }
    public bool HideResultsUntilClose { get; private set; }
    public virtual long? Version { get; set; }

    public long? CreatorUserId { get; private set; }
    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<PollAggregate, PollId, PollCreatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        Id = domainEvent.AggregateIdentity.Value;
        ToPeerId = domainEvent.AggregateEvent.ToPeer.PeerId;
        PollId = domainEvent.AggregateEvent.PollId;
        MultipleChoice = domainEvent.AggregateEvent.MultipleChoice;
        Quiz = domainEvent.AggregateEvent.Quiz;
        PublicVoters = domainEvent.AggregateEvent.PublicVoters;
        Answers = domainEvent.AggregateEvent.Answers;
        Question = domainEvent.AggregateEvent.Question;
        CorrectAnswers = domainEvent.AggregateEvent.CorrectAnswers;
        Solution = domainEvent.AggregateEvent.Solution;
        SolutionEntities2 = domainEvent.AggregateEvent.SolutionEntities;
        QuestionEntities2 = domainEvent.AggregateEvent.QuestionEntities;
        CreatorUserId = domainEvent.AggregateEvent.CreatorUserId;
        ClosePeriod = domainEvent.AggregateEvent.ClosePeriod;
        CloseDate = domainEvent.AggregateEvent.CloseDate;
        OpenAnswers = domainEvent.AggregateEvent.OpenAnswers;
        RevotingDisabled = domainEvent.AggregateEvent.RevotingDisabled;
        ShuffleAnswers = domainEvent.AggregateEvent.ShuffleAnswers;
        HideResultsUntilClose = domainEvent.AggregateEvent.HideResultsUntilClose;

        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<PollAggregate, PollId, VoteSucceededEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        AnswerVoters = domainEvent.AggregateEvent.AnswerVoters;
        // Item 23: keep TotalVoters in sync with reality. A fresh vote bumps it, a
        // retract drops it, and a re-vote (RetractVoteOptions set together with new
        // Options) is a swap so the total is unchanged. Without this check, switching
        // a non-quiz vote inflated TotalVoters once per click.
        var retractedSomething = domainEvent.AggregateEvent.RetractVoteOptions is { Count: > 0 };
        var votedSomething = domainEvent.AggregateEvent.Options.Count > 0;
        if (votedSomething && !retractedSomething)
        {
            TotalVoters++;
        }
        else if (!votedSomething && retractedSomething)
        {
            TotalVoters--;
        }

        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<PollAggregate, PollId, PollAnswerAddedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        Answers = [.. Answers, domainEvent.AggregateEvent.Answer];
        AnswerVoters =
        [
            .. AnswerVoters ?? [],
            new PollAnswerVoter(false, domainEvent.AggregateEvent.Answer.Option, 0)
        ];

        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<PollAggregate, PollId, PollAnswerRemovedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var option = domainEvent.AggregateEvent.Answer.Option;
        Answers = [.. Answers.Where(p => p.Option != option)];
        AnswerVoters = AnswerVoters == null
            ? null
            : [.. AnswerVoters.Where(p => p.Option != option)];

        // Voters who had only picked the removed option stop being voters, so the total
        // has to shrink by exactly that many people. VoterPeerIds carries everyone who
        // had picked it; the aggregate already knows which of them had other picks too.
        var orphanedVoters = domainEvent.AggregateEvent.VoterPeerIds.Count;
        if (orphanedVoters > 0)
        {
            TotalVoters = Math.Max(0, TotalVoters - orphanedVoters);
        }

        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<PollAggregate, PollId, PollClosedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        Closed = true;
        CloseDate = domainEvent.AggregateEvent.CloseDate;

        return Task.CompletedTask;
    }
}
