namespace MyTelegram.QueryHandlers.InMemory.Poll;

public class GetActivePollsWithCloseDateQueryHandler(IQueryOnlyReadModelStore<PollReadModel> store)
    : IQueryHandler<GetActivePollsWithCloseDateQuery, IReadOnlyCollection<IPollReadModel>>
{
    public async Task<IReadOnlyCollection<IPollReadModel>> ExecuteQueryAsync(
        GetActivePollsWithCloseDateQuery query,
        CancellationToken cancellationToken)
    {
        return await store.FindAsync(
            p => !p.Closed && p.CloseDate != null && p.CloseDate <= query.MaxCloseDate,
            cancellationToken: cancellationToken);
    }
}
