using MyTelegram.Domain.Aggregates.Poll;

namespace MyTelegram.Domain.Tests.UnitTests.Aggregates.Poll;

public class PollAggregateTests : TestsFor<PollAggregate>
{
    private const long CreatorUserId = 111;
    private const long VoterUserId = 222;

    public PollAggregateTests()
    {
        Fixture.Customize<PollId>(x => x.FromFactory(() => PollId.Create(1001)));
    }

    [Fact]
    public void Vote_On_Multiple_Choice_Poll_Records_Every_Option()
    {
        CreatePoll(multipleChoice: true);

        Sut.Vote(A<RequestInfo>(), VoterUserId, ["0", "1"]);

        var @event = Sut.UncommittedEvents.Single().AggregateEvent.ShouldBeOfType<VoteSucceededEvent>();
        @event.Options.ShouldBe(["0", "1"]);
        @event.AnswerVoters.Single(p => p.Option == "0").Voters.ShouldBe(1);
        @event.AnswerVoters.Single(p => p.Option == "1").Voters.ShouldBe(1);
    }

    [Fact]
    public void Vote_With_Several_Options_On_Single_Choice_Poll_Throws()
    {
        CreatePoll();

        var exception = Assert.Throws<RpcException>(() => Sut.Vote(A<RequestInfo>(), VoterUserId, ["0", "1"]));

        exception.Message.ShouldBe(RpcErrors.RpcErrors400.OptionInvalid.Message);
    }

    [Fact]
    public void Revote_Does_Not_Inflate_Tallies()
    {
        CreatePoll();
        Vote(VoterUserId, "0");

        Sut.Vote(A<RequestInfo>(), VoterUserId, ["1"]);

        var @event = Sut.UncommittedEvents.Last().AggregateEvent.ShouldBeOfType<VoteSucceededEvent>();
        @event.RetractVoteOptions.ShouldBe(["0"]);
        @event.AnswerVoters.Single(p => p.Option == "0").Voters.ShouldBe(0);
        @event.AnswerVoters.Single(p => p.Option == "1").Voters.ShouldBe(1);
    }

    [Fact]
    public void Revote_On_Poll_With_Revoting_Disabled_Throws()
    {
        CreatePoll(revotingDisabled: true);
        Vote(VoterUserId, "0");

        var exception = Assert.Throws<RpcException>(() => Sut.Vote(A<RequestInfo>(), VoterUserId, ["1"]));

        exception.Message.ShouldBe(RpcErrors.RpcErrors400.RevoteNotAllowed.Message);
    }

    [Fact]
    public void Revote_On_Quiz_Throws()
    {
        CreatePoll(quiz: true);
        Vote(VoterUserId, "0");

        var exception = Assert.Throws<RpcException>(() => Sut.Vote(A<RequestInfo>(), VoterUserId, ["1"]));

        exception.Message.ShouldBe(RpcErrors.RpcErrors400.RevoteNotAllowed.Message);
    }

    [Fact]
    public void Vote_On_Closed_Poll_Throws()
    {
        CreatePoll();
        ApplyEvent(new PollClosedEvent(APeer(), 1001, 12345));

        var exception = Assert.Throws<RpcException>(() => Sut.Vote(A<RequestInfo>(), VoterUserId, ["0"]));

        exception.Message.ShouldBe(RpcErrors.RpcErrors400.MessagePollClosed.Message);
    }

    [Fact]
    public void ClosePoll_Emits_PollClosedEvent()
    {
        CreatePoll();

        Sut.ClosePoll(9999);

        var @event = Sut.UncommittedEvents.Single().AggregateEvent.ShouldBeOfType<PollClosedEvent>();
        @event.CloseDate.ShouldBe(9999);
    }

    [Fact]
    public void ClosePoll_Is_Idempotent()
    {
        CreatePoll();
        ApplyEvent(new PollClosedEvent(APeer(), 1001, 9999));

        Sut.ClosePoll(10000);

        // A repeated stop must not overwrite CloseDate or re-broadcast the update.
        Sut.UncommittedEvents.ShouldBeEmpty();
    }

    [Fact]
    public void CreatePoll_With_Close_Period_Out_Of_Range_Throws()
    {
        var exception = Assert.Throws<RpcException>(() => Sut.CreatePoll(
            APeer(),
            1001,
            false,
            false,
            false,
            "question",
            [new PollAnswer("a", "0", null)],
            null,
            null,
            null,
            null,
            CreatorUserId,
            closePeriod: MyTelegramConsts.MaxPollClosePeriod + 1));

        exception.Message.ShouldBe(RpcErrors.RpcErrors400.PollOptionInvalid.Message);
    }

    [Fact]
    public void CreatePoll_Stores_Close_Deadline_And_Flags()
    {
        Sut.CreatePoll(
            APeer(),
            1001,
            false,
            false,
            true,
            "question",
            [new PollAnswer("a", "0", null)],
            null,
            null,
            null,
            null,
            CreatorUserId,
            closePeriod: 30,
            closeDate: 5030,
            openAnswers: true,
            revotingDisabled: true,
            shuffleAnswers: true,
            hideResultsUntilClose: true);

        var @event = Sut.UncommittedEvents.Single().AggregateEvent.ShouldBeOfType<PollCreatedEvent>();
        @event.ClosePeriod.ShouldBe(30);
        @event.CloseDate.ShouldBe(5030);
        @event.OpenAnswers.ShouldBeTrue();
        @event.RevotingDisabled.ShouldBeTrue();
        @event.ShuffleAnswers.ShouldBeTrue();
        @event.HideResultsUntilClose.ShouldBeTrue();
    }

    [Fact]
    public void AddAnswer_On_Poll_Without_Open_Answers_Throws()
    {
        CreatePoll();

        var exception = Assert.Throws<RpcException>(() => Sut.AddAnswer(
            A<RequestInfo>(), VoterUserId, new PollAnswer("new", "2", null, VoterUserId, 500), 500));

        exception.Message.ShouldBe(RpcErrors.RpcErrors400.PollAnswerInvalid.Message);
    }

    [Fact]
    public void AddAnswer_Duplicate_Text_Throws()
    {
        CreatePoll(openAnswers: true);

        var exception = Assert.Throws<RpcException>(() => Sut.AddAnswer(
            A<RequestInfo>(), VoterUserId, new PollAnswer("a", "2", null, VoterUserId, 500), 500));

        exception.Message.ShouldBe(RpcErrors.RpcErrors400.PollOptionDuplicate.Message);
    }

    [Fact]
    public void AddAnswer_Emits_PollAnswerAddedEvent()
    {
        CreatePoll(openAnswers: true);

        Sut.AddAnswer(A<RequestInfo>(), VoterUserId, new PollAnswer("new", "2", null, VoterUserId, 500), 500);

        var @event = Sut.UncommittedEvents.Single().AggregateEvent.ShouldBeOfType<PollAnswerAddedEvent>();
        @event.Answer.Option.ShouldBe("2");
        @event.AddedByPeerId.ShouldBe(VoterUserId);
    }

    [Fact]
    public void DeleteAnswer_Reports_Only_Voters_Left_Without_Any_Pick()
    {
        CreatePoll(openAnswers: true, multipleChoice: true);

        // 222 picked both options, 333 only the one being removed.
        Vote(VoterUserId, "0", "1");
        Vote(333, "1");

        Sut.DeleteAnswer(A<RequestInfo>(), CreatorUserId, "1");

        var @event = Sut.UncommittedEvents.Last().AggregateEvent.ShouldBeOfType<PollAnswerRemovedEvent>();
        @event.AllVoterPeerIds.OrderBy(p => p).ShouldBe([VoterUserId, 333]);
        @event.VoterPeerIds.ShouldBe([333L]);
    }

    [Fact]
    public void DeleteAnswer_By_Someone_Else_Throws()
    {
        CreatePoll(openAnswers: true);
        ApplyEvent(new PollAnswerAddedEvent(A<RequestInfo>(), 1001, APeer(), VoterUserId,
            new PollAnswer("contributed", "2", null, VoterUserId, 500), 500));

        var exception = Assert.Throws<RpcException>(() =>
            Sut.DeleteAnswer(A<RequestInfo>(), 444, "2"));

        exception.Message.ShouldBe(RpcErrors.RpcErrors400.PollAnswerInvalid.Message);
    }

    [Fact]
    public void DeleteAnswer_For_Unknown_Option_Throws()
    {
        CreatePoll(openAnswers: true);

        var exception = Assert.Throws<RpcException>(() =>
            Sut.DeleteAnswer(A<RequestInfo>(), CreatorUserId, "99"));

        exception.Message.ShouldBe(RpcErrors.RpcErrors400.OptionInvalid.Message);
    }

    private static Peer APeer()
    {
        return new Peer(PeerType.Chat, 500);
    }

    private void CreatePoll(
        bool multipleChoice = false,
        bool quiz = false,
        bool openAnswers = false,
        bool revotingDisabled = false)
    {
        ApplyEvent(new PollCreatedEvent(
            APeer(),
            1001,
            multipleChoice,
            quiz,
            false,
            "question",
            [new PollAnswer("a", "0", null), new PollAnswer("b", "1", null)],
            quiz ? ["0"] : null,
            null,
            null,
            null,
            CreatorUserId,
            null,
            null,
            openAnswers,
            revotingDisabled,
            false,
            false));
    }

    /// <summary>Casts a vote and folds the resulting event back into the aggregate state.</summary>
    private void Vote(long voterPeerId, params string[] options)
    {
        Sut.Vote(A<RequestInfo>(), voterPeerId, options);
        var @event = (VoteSucceededEvent)Sut.UncommittedEvents.Last().AggregateEvent;
        ApplyEvent(@event);
    }

    private void ApplyEvent<TEvent>(TEvent aggregateEvent)
        where TEvent : IAggregateEvent<PollAggregate, PollId>
    {
        // EventFlow rejects an event whose sequence number doesn't follow the current
        // version, so each applied event has to advance it.
        Sut.ApplyEvents([ADomainEvent<PollAggregate, PollId, TEvent>(aggregateEvent, Sut.Version + 1)]);
    }
}
