namespace MyTelegram.QueryHandlers.InMemory.Poll;

public class GetPollVotesCountQueryHandler(IQueryOnlyReadModelStore<PollAnswerVoterReadModel> store)
    : IQueryHandler<GetPollVotesCountQuery, long>
{
    public async Task<long> ExecuteQueryAsync(GetPollVotesCountQuery query, CancellationToken cancellationToken)
    {
        Expression<Func<PollAnswerVoterReadModel, bool>> predicate = p => p.PollId == query.PollId;
        predicate = predicate.WhereIf(!string.IsNullOrEmpty(query.Option), p => p.Option == query.Option);

        return await store.CountAsync(predicate, cancellationToken);
    }
}
