namespace MyTelegram.QueryHandlers.InMemory.QuickReply;

public class GetQuickReplyMessagesQueryHandler(IQueryOnlyReadModelStore<MessageReadModel> store)
    : IQueryHandler<GetQuickReplyMessagesQuery, IReadOnlyCollection<IMessageReadModel>>
{
    public async Task<IReadOnlyCollection<IMessageReadModel>> ExecuteQueryAsync(GetQuickReplyMessagesQuery query,
        CancellationToken cancellationToken)
    {
        Expression<Func<MessageReadModel, bool>> predicate =
            p => p.OwnerPeerId == query.OwnerUserId && p.IsQuickReplyMessage;
        predicate = predicate
            .WhereIf(query.ShortcutIds?.Count > 0, p => query.ShortcutIds!.Contains(p.ShortcutId!.Value))
            .WhereIf(query.MessageIds?.Count > 0, p => query.MessageIds!.Contains(p.MessageId));

        return await store.FindAsync(predicate,
            sort: new SortOptions<MessageReadModel>(p => p.MessageId, SortType.Ascending),
            cancellationToken: cancellationToken);
    }
}
