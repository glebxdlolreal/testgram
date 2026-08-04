namespace MyTelegram.QueryHandlers.InMemory.Messaging;

public class GetMessageIdByPollIdQueryHandler(IQueryOnlyReadModelStore<MessageReadModel> store)
    : IQueryHandler<GetMessageIdByPollIdQuery, int?>
{
    public async Task<int?> ExecuteQueryAsync(GetMessageIdByPollIdQuery query, CancellationToken cancellationToken)
    {
        return await store.FirstOrDefaultAsync(
            p => p.OwnerPeerId == query.OwnerPeerId && p.PollId == query.PollId,
            p => (int?)p.MessageId,
            cancellationToken: cancellationToken);
    }
}
