namespace MyTelegram.QueryHandlers.InMemory.Poll;

public class GetRecentPollVotersQueryHandler(IQueryOnlyReadModelStore<PollAnswerVoterReadModel> store)
    : IQueryHandler<GetRecentPollVotersQuery, IReadOnlyCollection<IPollAnswerVoterReadModel>>
{
    public async Task<IReadOnlyCollection<IPollAnswerVoterReadModel>> ExecuteQueryAsync(
        GetRecentPollVotersQuery query,
        CancellationToken cancellationToken)
    {
        return await store.FindAsync(
            p => p.PollId == query.PollId,
            limit: query.Limit,
            sort: new SortOptions<PollAnswerVoterReadModel>(p => p.Date, SortType.Descending),
            cancellationToken: cancellationToken);
    }
}
