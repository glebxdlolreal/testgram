using System.Text;

namespace MyTelegram.Converters;

/// <summary>
/// Builds <c>pollResults</c> from a poll read model. Shared by the poll and poll-results
/// converters, which otherwise carried identical copies of this logic.
/// </summary>
public static class PollResultsBuilder
{
    /// <summary>
    /// Fills in the per-answer tallies, chosen flags and recent voters.
    /// </summary>
    /// <param name="pollResults">Destination, already mapped from the read model.</param>
    /// <param name="pollReadModel">Poll being rendered.</param>
    /// <param name="chosenOptions">Options the requesting peer picked, for the chosen flag.</param>
    /// <param name="recentVoterPeerIds">Most recent voters, newest first; only used for public polls.</param>
    /// <param name="selfUserId">Requesting user, used to decide whether hidden results may be revealed.</param>
    public static IPollResults Build(
        IPollResults pollResults,
        IPollReadModel pollReadModel,
        IList<string>? chosenOptions,
        IReadOnlyCollection<long>? recentVoterPeerIds = null,
        long selfUserId = 0)
    {
        chosenOptions ??= [];

        // hide_results_until_close keeps the tallies secret while voting is open. The
        // creator always sees them, and everyone sees them once the poll is closed.
        var hideResults = pollReadModel.HideResultsUntilClose
                          && !pollReadModel.Closed
                          && (selfUserId == 0 || pollReadModel.CreatorUserId != selfUserId);

        if (pollReadModel.AnswerVoters != null)
        {
            var voters = pollReadModel.AnswerVoters.Select(p => new TPollAnswerVoters
            {
                Correct = p.Correct,
                Voters = hideResults ? 0 : p.Voters,
                Option = Encoding.UTF8.GetBytes(p.Option),
                Chosen = chosenOptions.Contains(p.Option),
                RecentVoters = new TVector<IPeer>()
            });
            pollResults.Results = new TVector<IPollAnswerVoters>(voters);
        }
        else
        {
            var voters = pollReadModel.Answers.Select(p => new TPollAnswerVoters
            {
                Correct = false,
                Voters = 0,
                Option = Encoding.UTF8.GetBytes(p.Option),
                Chosen = chosenOptions.Contains(p.Option),
                RecentVoters = new TVector<IPeer>()
            });
            pollResults.Results = new TVector<IPollAnswerVoters>(voters);
        }

        if (hideResults)
        {
            pollResults.TotalVoters = 0;
        }

        // Recent voters are only meaningful for non-anonymous polls: exposing them for an
        // anonymous poll would deanonymize the voters.
        if (pollReadModel.PublicVoters && !hideResults && recentVoterPeerIds is { Count: > 0 })
        {
            pollResults.RecentVoters = new TVector<IPeer>(
                recentVoterPeerIds
                    .Take(MyTelegramConsts.MaxPollRecentVoters)
                    .Select(IPeer (p) => new TPeerUser { UserId = p }));
        }

        return pollResults;
    }
}
