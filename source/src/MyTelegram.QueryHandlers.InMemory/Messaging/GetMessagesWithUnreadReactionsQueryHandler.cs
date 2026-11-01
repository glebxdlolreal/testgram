namespace MyTelegram.QueryHandlers.InMemory.Messaging;

public class GetMessagesWithUnreadReactionsQueryHandler(IQueryOnlyReadModelStore<MessageReadModel> store)
    : IQueryHandler<GetMessagesWithUnreadReactionsQuery, IReadOnlyCollection<IMessageReadModel>>
{
    public async Task<IReadOnlyCollection<IMessageReadModel>> ExecuteQueryAsync(
        GetMessagesWithUnreadReactionsQuery query, CancellationToken cancellationToken)
    {
        var toPeerType = query.ToPeer.PeerType;
        var toPeerId = query.ToPeer.PeerId;

        // add_offset is how the official client walks the reaction history: it asks for limit=1 at
        // add_offset=count-1 to land on the oldest unread reaction, and treats an empty answer as
        // "nothing left" and calls messages.readReactions.
        var skip = query.AddOffset > 0 ? query.AddOffset : 0;

        return await store.FindAsync(
            m => m.OwnerPeerId == query.OwnerPeerId &&
                 m.ToPeerType == toPeerType &&
                 m.ToPeerId == toPeerId &&
                 m.SenderUserId == query.SenderUserId &&
                 m.RecentReactions2 != null &&
                 m.RecentReactions2.Any(r => r.SenderUserId != query.SenderUserId && r.Date > query.ReadDate) &&
                 (!query.TopMsgId.HasValue || m.TopMsgId == query.TopMsgId.Value) &&
                 (query.SavedPeerId == null ||
                  m.SavedPeerId != null &&
                  m.SavedPeerId.PeerType == query.SavedPeerId.PeerType &&
                  m.SavedPeerId.PeerId == query.SavedPeerId.PeerId) &&
                 (query.OffsetId == 0 || m.MessageId < query.OffsetId) &&
                 (query.MaxId == 0 || m.MessageId <= query.MaxId) &&
                 (query.MinId == 0 || m.MessageId >= query.MinId),
            skip,
            query.Limit,
            new SortOptions<MessageReadModel>(m => m.MessageId, SortType.Descending),
            cancellationToken);
    }
}
