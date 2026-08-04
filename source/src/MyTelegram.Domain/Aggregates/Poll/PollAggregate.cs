namespace MyTelegram.Domain.Aggregates.Poll;

[EnableAutoGeneration]
public class PollAggregate : AggregateRoot<PollAggregate, PollId>
{
    private readonly PollState _state = new();

    public PollAggregate(PollId id) : base(id)
    {
        Register(_state);
    }

    public void Vote(RequestInfo requestInfo, long voteUserPeerId, IReadOnlyCollection<string> options)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        if (_state.Closed)
        {
            RpcErrors.RpcErrors400.MessagePollClosed.ThrowRpcError();
        }

        if (!_state.MultipleChoice)
        {
            if (options.Count > 1)
            {
                RpcErrors.RpcErrors400.OptionInvalid.ThrowRpcError();
            }
        }

        if (_state.VotedPeerIds.Contains(voteUserPeerId))
        {
            // Quizzes never allow changing an answer. RevotingDisabled extends the same
            // rule to regular polls when the creator asked for it.
            if (_state.Quiz || _state.RevotingDisabled)
            {
                RpcErrors.RpcErrors400.RevoteNotAllowed.ThrowRpcError();
            }
        }


        foreach (var option in options)
        {
            if (!_state.Options.Contains(option))
            {
                RpcErrors.RpcErrors400.OptionInvalid.ThrowRpcError();
            }
        }

        var answerVoters = _state.AnswerVoters;

        // Only quiz==false can retract vote
        List<string>? retractVoteOptions = null;
        if (options.Count == 0 && !_state.Quiz)
        {
            retractVoteOptions = _state.GetVoteOptionsByUserId(voteUserPeerId);
            foreach (var pollAnswerVoter in answerVoters)
            {
                if (retractVoteOptions.Contains(pollAnswerVoter.Option))
                {
                    pollAnswerVoter.DecrementVoters();
                }
            }
        }
        else
        {
            // Item 23: when a user re-votes in a non-quiz poll, decrement their previous
            // option voter counts first so the totals reflect the new selection instead
            // of double-counting. Without this, every revote inflates AnswerVoters
            // forever and the broadcast updateMessagePoll shows wrong tallies.
            if (!_state.Quiz && _state.VotedPeerIds.Contains(voteUserPeerId))
            {
                retractVoteOptions = _state.GetVoteOptionsByUserId(voteUserPeerId);
                foreach (var pollAnswerVoter in answerVoters)
                {
                    if (retractVoteOptions.Contains(pollAnswerVoter.Option))
                    {
                        pollAnswerVoter.DecrementVoters();
                    }
                }
            }

            foreach (var answer in answerVoters)
            {
                if (options.Contains(answer.Option))
                {
                    answer.IncrementVoters();
                }
            }
        }

        Emit(new VoteSucceededEvent(
            requestInfo,
            _state.PollId,
            voteUserPeerId,
            options,
            _state.Answers,
            _state.CorrectAnswers,
            answerVoters,
            _state.ToPeer,
            retractVoteOptions
        ));
    }

    public void ClosePoll(int closeDate)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);

        // Closing an already closed poll is a no-op: clients re-send the "stop poll"
        // edit on retry, and the auto-close background service may race with a manual
        // stop. Emitting a second PollClosedEvent would overwrite CloseDate and push a
        // redundant updateMessagePoll to every member.
        if (_state.Closed)
        {
            return;
        }

        Emit(new PollClosedEvent(_state.ToPeer, _state.PollId, closeDate));
    }

    public void CreatePoll(Peer toPeer,
        long pollId,
        bool multipleChoice,
        bool quiz,
        bool publicVoters,
        string question,
        List<PollAnswer> answers,
        IReadOnlyCollection<string>? correctAnswers,
        string? solution,
        //byte[]? solutionEntities,
        //byte[]? questionEntities
        IList<IMessageEntity>? solutionEntities,
        IList<IMessageEntity>? questionEntities,
        long creatorUserId,
        int? closePeriod = null,
        int? closeDate = null,
        bool openAnswers = false,
        bool revotingDisabled = false,
        bool shuffleAnswers = false,
        bool hideResultsUntilClose = false
        )
    {
        Specs.AggregateIsNew.ThrowDomainErrorIfNotSatisfied(this);
        if (answers.Count > MyTelegramConsts.MaxVoteOptions)
        {
            RpcErrors.RpcErrors400.OptionsTooMuch.ThrowRpcError();
        }

        if (closePeriod is < MyTelegramConsts.MinPollClosePeriod or > MyTelegramConsts.MaxPollClosePeriod)
        {
            RpcErrors.RpcErrors400.PollOptionInvalid.ThrowRpcError();
        }

        Emit(new PollCreatedEvent(toPeer,
            pollId,
            multipleChoice,
            quiz,
            publicVoters,
            question,
            answers,
            correctAnswers,
            solution,
            solutionEntities,
            questionEntities,
            creatorUserId,
            closePeriod,
            closeDate,
            openAnswers,
            revotingDisabled,
            shuffleAnswers,
            hideResultsUntilClose
            ));
    }

    public void CreateVoteAnswer(long pollId,
        long voterPeerId,
        string option,
        bool correct)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        var date = DateTime.UtcNow.ToTimestamp();
        Emit(new VoteAnswerCreatedEvent(pollId, voterPeerId, option, correct, date));
    }

    public void DeleteVoteAnswer(long pollId,
        long voterPeerId,
        string option)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        Emit(new VoteAnswerDeletedEvent(pollId, voterPeerId, option));
    }

    /// <summary>
    /// Appends a new answer to an open poll (<c>open_answers</c>), on behalf of a member.
    /// </summary>
    public void AddAnswer(RequestInfo requestInfo, long addedByPeerId, PollAnswer answer, int date)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);

        if (_state.Closed)
        {
            RpcErrors.RpcErrors400.MessagePollClosed.ThrowRpcError();
        }

        if (!_state.OpenAnswers)
        {
            RpcErrors.RpcErrors400.PollAnswerInvalid.ThrowRpcError();
        }

        if (string.IsNullOrWhiteSpace(answer.Text))
        {
            RpcErrors.RpcErrors400.PollAnswerInvalid.ThrowRpcError();
        }

        if (_state.Answers.Count >= MyTelegramConsts.MaxVoteOptions)
        {
            RpcErrors.RpcErrors400.OptionsTooMuch.ThrowRpcError();
        }

        // The option id is server-assigned, so a duplicate can only be detected by text.
        if (_state.Answers.Any(p => string.Equals(p.Text, answer.Text, StringComparison.Ordinal)))
        {
            RpcErrors.RpcErrors400.PollOptionDuplicate.ThrowRpcError();
        }

        Emit(new PollAnswerAddedEvent(requestInfo, _state.PollId, _state.ToPeer, addedByPeerId, answer, date));
    }

    /// <summary>
    /// Removes an answer from an open poll (<c>open_answers</c>).
    /// </summary>
    public void DeleteAnswer(RequestInfo requestInfo, long requestedByPeerId, string option)
    {
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);

        if (_state.Closed)
        {
            RpcErrors.RpcErrors400.MessagePollClosed.ThrowRpcError();
        }

        if (!_state.OpenAnswers)
        {
            RpcErrors.RpcErrors400.PollAnswerInvalid.ThrowRpcError();
        }

        var answer = _state.Answers.FirstOrDefault(p => p.Option == option);
        if (answer == null)
        {
            RpcErrors.RpcErrors400.OptionInvalid.ThrowRpcError();
        }

        // Only whoever contributed the answer, or the poll creator, may remove it.
        // Channel admins are authorized by the handler before the command is published.
        if (answer!.AddedByPeerId != null
            && answer.AddedByPeerId != requestedByPeerId
            && _state.CreatorUid != requestedByPeerId)
        {
            RpcErrors.RpcErrors400.PollAnswerInvalid.ThrowRpcError();
        }

        var allVoterPeerIds = _state.GetVoterPeerIdsByOption(option);

        // Only voters whose *sole* pick was this option stop being voters of the poll.
        // A multiple-choice voter who also picked something else stays counted, so the
        // read model can subtract exactly this list from TotalVoters, while the locator
        // uses allVoterPeerIds to cascade-delete every vote document for the option.
        var voterPeerIds = allVoterPeerIds
            .Where(voterPeerId => _state.GetVoteOptionsByUserId(voterPeerId).Count == 1)
            .ToList();

        Emit(new PollAnswerRemovedEvent(requestInfo, _state.PollId, _state.ToPeer, requestedByPeerId, answer, voterPeerIds, allVoterPeerIds));
    }
}