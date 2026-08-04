namespace MyTelegram.QueryHandlers.InMemory.Poll;

public class GetPollVotesByPollIdsQueryHandler(IQueryOnlyReadModelStore<PollAnswerVoterReadModel> store)
    : IQueryHandler<GetPollVotesByPollIdsQuery, IReadOnlyCollection<IPollAnswerVoterReadModel>>
{
    public async Task<IReadOnlyCollection<IPollAnswerVoterReadModel>> ExecuteQueryAsync(
        GetPollVotesByPollIdsQuery query,
        CancellationToken cancellationToken)
    {
        return await store.FindAsync(
            p => query.PollIds.Contains(p.PollId) && p.Date > query.MinDate,
            cancellationToken: cancellationToken);
    }
}
