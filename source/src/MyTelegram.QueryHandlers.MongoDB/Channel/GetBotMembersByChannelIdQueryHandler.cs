namespace MyTelegram.QueryHandlers.MongoDB.Channel;

/// <summary>The bots that are current members of a channel or supergroup.</summary>
public class GetBotMembersByChannelIdQueryHandler(IQueryOnlyReadModelStore<ChannelMemberReadModel> store)
    : IQueryHandler<GetBotMembersByChannelIdQuery, IReadOnlyCollection<IChannelMemberReadModel>>
{
    public async Task<IReadOnlyCollection<IChannelMemberReadModel>> ExecuteQueryAsync(GetBotMembersByChannelIdQuery query,
        CancellationToken cancellationToken)
    {
        return await store.FindAsync(p => p.ChannelId == query.ChannelId && p.IsBot && !p.Left && !p.Kicked,
            cancellationToken: cancellationToken);
    }
}
