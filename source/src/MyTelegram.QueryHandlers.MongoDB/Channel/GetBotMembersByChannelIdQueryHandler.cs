namespace MyTelegram.QueryHandlers.MongoDB.Channel;

/// <summary>The bots that are current members of a channel or supergroup.</summary>
public class GetBotMembersByChannelIdQueryHandler(IQueryOnlyReadModelStore<ChannelMemberReadModel> store)
    : IQueryHandler<GetBotMembersByChannelIdQuery, IReadOnlyCollection<IChannelMemberReadModel>>
{
    public async Task<IReadOnlyCollection<IChannelMemberReadModel>> ExecuteQueryAsync(GetBotMembersByChannelIdQuery query,
        CancellationToken cancellationToken)
    {
        // inviteToChannel did not mark bots for a long time, so members already in carry IsBot = false;
        // bot accounts are recognised by their id block as well.
        return await store.FindAsync(p => p.ChannelId == query.ChannelId && !p.Left && !p.Kicked &&
                                          (p.IsBot || (p.UserId >= MyTelegramConsts.BotUserInitId &&
                                                       p.UserId <= MyTelegramConsts.ChatIdInitId)),
            cancellationToken: cancellationToken);
    }
}
