namespace MyTelegram.QueryHandlers.MongoDB.QuickReply;

public class GetQuickReplyListQueryHandler(IQueryOnlyReadModelStore<QuickReplyListReadModel> store)
    : IQueryHandler<GetQuickReplyListQuery, IQuickReplyListReadModel?>
{
    public async Task<IQuickReplyListReadModel?> ExecuteQueryAsync(GetQuickReplyListQuery query,
        CancellationToken cancellationToken)
    {
        return await store.FirstOrDefaultAsync(p => p.OwnerUserId == query.OwnerUserId, cancellationToken);
    }
}
