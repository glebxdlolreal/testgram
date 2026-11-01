namespace MyTelegram.QueryHandlers.MongoDB.Messaging;

public class
    GetSimpleMessageListQueryHandler(IQueryOnlyReadModelStore<MessageReadModel> store) : IQueryHandler<GetSimpleMessageListQuery, IReadOnlyCollection<SimpleMessageItem>>
{
    /// <summary>
    /// Builds the read model predicate of the query. Exposed so the pin/unpin scoping rules
    /// (<c>top_msg_id</c>, <c>saved_peer_id</c>) can be verified directly against the read model.
    /// </summary>
    public static Expression<Func<MessageReadModel, bool>> BuildPredicate(GetSimpleMessageListQuery query)
    {
        // top_msg_id/saved_peer_id scope the result to one forum or monoforum topic. The saved peer is
        // compared by id only: the peer type is implied by the id range and comparing the whole
        // sub-document would depend on its field order.
        var topMsgId = query.TopMsgId.GetValueOrDefault();
        var savedPeerId = query.SavedPeerId?.PeerId ?? 0;

        Expression<Func<MessageReadModel, bool>> predicate = query.ToPeer.PeerType == PeerType.Channel
            ? p => p.OwnerPeerId == query.ToPeer.PeerId
            : p => p.OwnerPeerId == query.OwnerPeerId && !p.IsQuickReplyMessage && p.ToPeerId == query.ToPeer.PeerId;

        return predicate
                .WhereIf(query.Pinned.HasValue, p => p.Pinned == query.Pinned!.Value)
                .WhereIf(query.MessageIds?.Count > 0, p => query.MessageIds!.Contains(p.MessageId))
                .WhereIf(topMsgId > 0, p => p.TopMsgId == topMsgId)
                .WhereIf(savedPeerId > 0, p => p.SavedPeerId!.PeerId == savedPeerId)
            ;
    }

    public async Task<IReadOnlyCollection<SimpleMessageItem>> ExecuteQueryAsync(GetSimpleMessageListQuery query, CancellationToken cancellationToken)
    {
        var predicate = BuildPredicate(query);

        if (query.ToPeer.PeerType == PeerType.Channel)
        {
            return await store.FindAsync(predicate,
                p => new SimpleMessageItem(p.OwnerPeerId, p.MessageId, p.ToPeerType, p.ToPeerId), limit: query.Limit,
                cancellationToken: cancellationToken);
        }

        if (!query.IncludeOtherParticipantMessages)
        {
            return await store.FindAsync(predicate,
                p => new SimpleMessageItem(p.OwnerPeerId, p.MessageId, p.ToPeerType, p.ToPeerId),
                limit: query.Limit, cancellationToken: cancellationToken
            );
        }

        var batchIds = await store.FindAsync(
            predicate,
            p => p.BatchId,
            limit: query.Limit, cancellationToken: cancellationToken);

        if (batchIds.Count == 0)
        {
            return [];
        }

        return await store.FindAsync(p => batchIds.Contains(p.BatchId),
            p => new SimpleMessageItem(p.OwnerPeerId, p.MessageId, p.ToPeerType, p.ToPeerId),
            cancellationToken: cancellationToken);
    }
}
