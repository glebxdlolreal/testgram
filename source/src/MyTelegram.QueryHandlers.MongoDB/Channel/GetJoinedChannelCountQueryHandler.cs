namespace MyTelegram.QueryHandlers.MongoDB.Channel;

public class GetJoinedChannelCountQueryHandler
    (IQueryOnlyReadModelStore<ChannelMemberReadModel> store) : IQueryHandler<GetJoinedChannelCountQuery, int>
{
    public async Task<int> ExecuteQueryAsync(GetJoinedChannelCountQuery query,
        CancellationToken cancellationToken)
    {
        return (int)await store.CountAsync(p => p.UserId == query.UserId && !p.Left && !p.Kicked, cancellationToken);
    }
}