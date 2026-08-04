namespace MyTelegram.QueryHandlers.InMemory.Messaging;

public class GetPollMessagesQueryHandler(IQueryOnlyReadModelStore<MessageReadModel> store)
    : IQueryHandler<GetPollMessagesQuery, IReadOnlyCollection<IMessageReadModel>>
{
    public async Task<IReadOnlyCollection<IMessageReadModel>> ExecuteQueryAsync(
        GetPollMessagesQuery query,
        CancellationToken cancellationToken)
    {
        return await store.FindAsync(
            m => m.OwnerPeerId == query.OwnerPeerId &&
                 m.SenderUserId == query.SenderUserId &&
                 m.PollId != null &&
                 (!query.TopMsgId.HasValue || m.TopMsgId == query.TopMsgId.Value) &&
                 (query.OffsetId == 0 || m.MessageId < query.OffsetId) &&
                 (query.MaxId == 0 || m.MessageId <= query.MaxId) &&
                 (query.MinId == 0 || m.MessageId >= query.MinId),
            Math.Max(0, query.AddOffset),
            query.Limit,
            new SortOptions<MessageReadModel>(m => m.MessageId, SortType.Descending),
            cancellationToken);
    }
}
