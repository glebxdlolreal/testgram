using System.Collections.Concurrent;

namespace MyTelegram.Domain.Aggregates.Poll;

public class PollState : AggregateState<PollAggregate, PollId, PollState>, IApply<PollCreatedEvent>,
    IApply<VoteSucceededEvent>,
    IApply<VoteAnswerCreatedEvent>,
    IApply<VoteAnswerDeletedEvent>,
    IApply<PollAnswerAddedEvent>,
    IApply<PollAnswerRemovedEvent>,
    IApply<PollClosedEvent>
{
    public long PollId { get; private set; }
    public long CreatorUid { get; private set; }
    public bool Closed { get; private set; }
    public bool PublicVoters { get; private set; }
    public bool MultipleChoice { get; private set; }
    public bool Quiz { get; private set; }
    public bool OpenAnswers { get; private set; }
    public bool RevotingDisabled { get; private set; }
    public bool ShuffleAnswers { get; private set; }
    public bool HideResultsUntilClose { get; private set; }
    public string Question { get; private set; } = default!;
    public string Solution { get; private set; } = default!;
    public byte[] SolutionEntities { get; private set; } = default!;
    public int CreationTime { get; private set; }
    public int? CloseDate { get; private set; }
    public int? ClosePeriod { get; private set; }
    public Peer ToPeer { get; private set; } = default!;

    public ConcurrentDictionary<string, List<long>> OptionsToVoterUsers { get; } = new();
    public List<string> Options { get; private set; } = new();
    public IReadOnlyCollection<string>? CorrectAnswers { get; private set; }
    public IReadOnlyCollection<PollAnswer> Answers { get; private set; } = default!;
    public IReadOnlyCollection<PollAnswerVoter> AnswerVoters { get; private set; } = new List<PollAnswerVoter>();
    public HashSet<long> VotedPeerIds { get; private set; } = new();
    private readonly ConcurrentDictionary<string, HashSet<long>> _optionToVoterPeers = new();
    public void Apply(PollCreatedEvent aggregateEvent)
    {
        PollId = aggregateEvent.PollId;
        Options = aggregateEvent.Answers.Select(p => p.Option).ToList();
        Answers = aggregateEvent.Answers;
        CorrectAnswers = aggregateEvent.CorrectAnswers;
        ToPeer = aggregateEvent.ToPeer;
        Quiz = aggregateEvent.Quiz;
        MultipleChoice = aggregateEvent.MultipleChoice;
        PublicVoters = aggregateEvent.PublicVoters;
        Question = aggregateEvent.Question;
        CreatorUid = aggregateEvent.CreatorUserId;
        ClosePeriod = aggregateEvent.ClosePeriod;
        CloseDate = aggregateEvent.CloseDate;
        OpenAnswers = aggregateEvent.OpenAnswers;
        RevotingDisabled = aggregateEvent.RevotingDisabled;
        ShuffleAnswers = aggregateEvent.ShuffleAnswers;
        HideResultsUntilClose = aggregateEvent.HideResultsUntilClose;

        var answerVoters = new List<PollAnswerVoter>();
        foreach (var answer in Answers)
        {
            var correct = CorrectAnswers?.Contains(answer.Option) ?? false;
            var voter = new PollAnswerVoter(correct, answer.Option, 0);
            answerVoters.Add(voter);
        }
        AnswerVoters = answerVoters;
    }

    public void Apply(VoteSucceededEvent aggregateEvent)
    {
        VotedPeerIds.Add(aggregateEvent.VoteUserPeerId);

        AnswerVoters = aggregateEvent.AnswerVoters;

        // Item 23: drop the voter from any options they're retracting so a follow-up
        // re-vote can correctly look up "what did they previously pick". Without this,
        // _optionToVoterPeers accumulates every historical pick and over-decrements
        // AnswerVoters on the next re-vote.
        if (aggregateEvent.RetractVoteOptions is { Count: > 0 } retracted)
        {
            foreach (var option in retracted)
            {
                if (_optionToVoterPeers.TryGetValue(option, out var voterPeers))
                {
                    voterPeers.Remove(aggregateEvent.VoteUserPeerId);
                }
            }
        }

        foreach (var option in aggregateEvent.Options)
        {
            if (!_optionToVoterPeers.TryGetValue(option, out var voterPeers))
            {
                voterPeers = new HashSet<long>();
                _optionToVoterPeers.TryAdd(option, voterPeers);
            }
            voterPeers.Add(aggregateEvent.VoteUserPeerId);
        }
    }

    public void Apply(VoteAnswerCreatedEvent aggregateEvent)
    {
        //throw new NotImplementedException();
    }

    public List<string> GetVoteOptionsByUserId(long userId)
    {
        var options = new List<string>();
        foreach (var kv in _optionToVoterPeers)
        {
            if (kv.Value.Contains(userId))
            {
                options.Add(kv.Key);
            }
        }

        return options;
    }

    public List<long> GetVoterPeerIdsByOption(string option)
    {
        return _optionToVoterPeers.TryGetValue(option, out var voterPeers)
            ? voterPeers.ToList()
            : [];
    }

    public void Apply(VoteAnswerDeletedEvent aggregateEvent)
    {
        // A single option is being retracted. The voter only stops counting as a poll
        // voter once none of their picks remain — otherwise a multiple-choice voter who
        // drops one of two options would be treated as having never voted.
        if (_optionToVoterPeers.TryGetValue(aggregateEvent.Option, out var voterPeers))
        {
            voterPeers.Remove(aggregateEvent.VoterPeerId);
        }

        if (GetVoteOptionsByUserId(aggregateEvent.VoterPeerId).Count == 0)
        {
            VotedPeerIds.Remove(aggregateEvent.VoterPeerId);
        }
    }

    public void Apply(PollAnswerAddedEvent aggregateEvent)
    {
        Answers = [.. Answers, aggregateEvent.Answer];
        Options.Add(aggregateEvent.Answer.Option);
        AnswerVoters = [.. AnswerVoters, new PollAnswerVoter(false, aggregateEvent.Answer.Option, 0)];
    }

    public void Apply(PollAnswerRemovedEvent aggregateEvent)
    {
        var option = aggregateEvent.Answer.Option;
        Answers = [.. Answers.Where(p => p.Option != option)];
        Options.Remove(option);
        AnswerVoters = [.. AnswerVoters.Where(p => p.Option != option)];
        _optionToVoterPeers.TryRemove(option, out _);

        // Voters whose only pick was the removed option are no longer voters at all.
        foreach (var voterPeerId in aggregateEvent.VoterPeerIds)
        {
            if (GetVoteOptionsByUserId(voterPeerId).Count == 0)
            {
                VotedPeerIds.Remove(voterPeerId);
            }
        }
    }

    public void Apply(PollClosedEvent aggregateEvent)
    {
        Closed = true;
        CloseDate = aggregateEvent.CloseDate;
    }
}
