namespace MyTelegram.QueryHandlers.InMemory.Messaging;

// ReSharper disable once UnusedMember.Global
public class GetMessagesByGroupedIdQueryHandler(IQueryOnlyReadModelStore<MessageReadModel> store)
    : IQueryHandler<GetMessagesByGroupedIdQuery, IReadOnlyCollection<IMessageReadModel>>
{
    public async Task<IReadOnlyCollection<IMessageReadModel>> ExecuteQueryAsync(
        GetMessagesByGroupedIdQuery query,
        CancellationToken cancellationToken)
    {
        return await store
            .FindAsync(p => p.OwnerPeerId == query.OwnerPeerId && !p.IsQuickReplyMessage && p.GroupedId == query.GroupedId,
                cancellationToken: cancellationToken);
    }
}
