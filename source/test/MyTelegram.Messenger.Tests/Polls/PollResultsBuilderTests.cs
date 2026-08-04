using System.Text;
using MyTelegram.Converters;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Polls;

public class PollResultsBuilderTests
{
    [Fact]
    public void ShouldMarkChosenOptions()
    {
        var poll = CreatePoll();

        var results = PollResultsBuilder.Build(new TPollResults(), poll, ["1"]);

        OptionOf(results, "0").Chosen.ShouldBeFalse();
        OptionOf(results, "1").Chosen.ShouldBeTrue();
    }

    [Fact]
    public void ShouldHideTalliesUntilCloseForOtherUsers()
    {
        var poll = CreatePoll(hideResultsUntilClose: true, totalVoters: 7);

        var results = PollResultsBuilder.Build(MappedResults(poll), poll, ["1"], selfUserId: 999);

        results.TotalVoters.ShouldBe(0);
        OptionOf(results, "0").Voters.ShouldBe(0);
        OptionOf(results, "1").Voters.ShouldBe(0);

        // The chosen flag survives: a voter still sees what they picked.
        OptionOf(results, "1").Chosen.ShouldBeTrue();
    }

    [Fact]
    public void ShouldRevealHiddenTalliesToCreator()
    {
        var poll = CreatePoll(hideResultsUntilClose: true, totalVoters: 7);

        var results = PollResultsBuilder.Build(MappedResults(poll), poll, [], selfUserId: 111);

        results.TotalVoters.ShouldBe(7);
        OptionOf(results, "0").Voters.ShouldBe(3);
    }

    [Fact]
    public void ShouldRevealHiddenTalliesOnceClosed()
    {
        var poll = CreatePoll(hideResultsUntilClose: true, totalVoters: 7, closed: true);

        var results = PollResultsBuilder.Build(MappedResults(poll), poll, [], selfUserId: 999);

        results.TotalVoters.ShouldBe(7);
        OptionOf(results, "0").Voters.ShouldBe(3);
    }

    [Fact]
    public void ShouldExposeRecentVotersOnlyForPublicPolls()
    {
        var publicPoll = CreatePoll(publicVoters: true);
        var anonymousPoll = CreatePoll();

        PollResultsBuilder.Build(new TPollResults(), publicPoll, [], [11L, 22L])
            .RecentVoters!.Count.ShouldBe(2);

        PollResultsBuilder.Build(new TPollResults(), anonymousPoll, [], [11L, 22L])
            .RecentVoters.ShouldBeNull();
    }

    [Fact]
    public void ShouldCapRecentVoters()
    {
        var poll = CreatePoll(publicVoters: true);

        var results = PollResultsBuilder.Build(new TPollResults(), poll, [], [11L, 22L, 33L, 44L, 55L]);

        results.RecentVoters!.Count.ShouldBe(MyTelegramConsts.MaxPollRecentVoters);
    }

    [Fact]
    public void HashShouldChangeWhenTalliesChange()
    {
        var before = PollHashHelper.ComputeHash(CreatePoll(totalVoters: 7));
        var after = PollHashHelper.ComputeHash(CreatePoll(totalVoters: 8));

        before.ShouldNotBe(after);
    }

    [Fact]
    public void HashShouldChangeWhenPollCloses()
    {
        var open = PollHashHelper.ComputeHash(CreatePoll());
        var closed = PollHashHelper.ComputeHash(CreatePoll(closed: true));

        open.ShouldNotBe(closed);
    }

    [Fact]
    public void HashShouldBeStableForUnchangedPoll()
    {
        PollHashHelper.ComputeHash(CreatePoll()).ShouldBe(PollHashHelper.ComputeHash(CreatePoll()));
    }

    [Fact]
    public void HashShouldBeNonNegative()
    {
        // 0 means "no hash" to clients, and a negative value would round-trip badly.
        PollHashHelper.ComputeHash(CreatePoll()).ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// Stands in for PollResultsMapper, which copies TotalVoters onto the results before the
    /// builder runs. Without it the hide/reveal assertions would test nothing.
    /// </summary>
    private static TPollResults MappedResults(IPollReadModel poll)
    {
        return new TPollResults { TotalVoters = poll.TotalVoters };
    }

    private static IPollAnswerVoters OptionOf(IPollResults results, string option)
    {
        return results.Results!.Single(p =>
            Encoding.UTF8.GetString(((TPollAnswerVoters)p).Option.Span) == option);
    }

    private static IPollReadModel CreatePoll(
        bool hideResultsUntilClose = false,
        bool publicVoters = false,
        bool closed = false,
        int totalVoters = 0)
    {
        return new FakePollReadModel
        {
            PollId = 1001,
            ToPeerId = 500,
            Question = "question",
            Answers = [new PollAnswer("a", "0", null), new PollAnswer("b", "1", null)],
            AnswerVoters =
            [
                new PollAnswerVoter(false, "0", 3),
                new PollAnswerVoter(false, "1", 4)
            ],
            CreatorUserId = 111,
            HideResultsUntilClose = hideResultsUntilClose,
            PublicVoters = publicVoters,
            Closed = closed,
            TotalVoters = totalVoters
        };
    }

    private sealed class FakePollReadModel : IPollReadModel
    {
        public string Id { get; init; } = "poll-1001";
        public long ToPeerId { get; init; }
        public long PollId { get; init; }
        public bool MultipleChoice { get; init; }
        public bool Quiz { get; init; }
        public bool PublicVoters { get; init; }
        public string Question { get; init; } = string.Empty;
        public IReadOnlyCollection<PollAnswer> Answers { get; init; } = [];
        public IReadOnlyCollection<string>? CorrectAnswers { get; init; }
        public string? Solution { get; init; }
        public byte[]? SolutionEntities { get; init; }
        public IList<IMessageEntity>? SolutionEntities2 { get; init; }
        public bool Closed { get; init; }
        public int? CloseDate { get; init; }
        public int? ClosePeriod { get; init; }
        public int TotalVoters { get; init; }
        public IReadOnlyCollection<PollAnswerVoter>? AnswerVoters { get; init; }
        public byte[]? QuestionEntities { get; init; }
        public IList<IMessageEntity>? QuestionEntities2 { get; init; }
        public long? CreatorUserId { get; init; }
        public bool OpenAnswers { get; init; }
        public bool RevotingDisabled { get; init; }
        public bool ShuffleAnswers { get; init; }
        public bool HideResultsUntilClose { get; init; }
    }
}
