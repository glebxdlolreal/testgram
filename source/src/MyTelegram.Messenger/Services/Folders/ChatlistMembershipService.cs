using MyTelegram.Messenger.Services.Premium;
using MyTelegram.Messenger.Helpers;

namespace MyTelegram.Messenger.Services.Folders;

/// <summary>
/// Joins and leaves the channels of a shared folder.
///
/// <para>Importing a <a href="https://corefork.telegram.org/api/links#chat-folder-links">chat folder link</a>
/// is documented as "joining some or all the chats in the folder", and deleting an imported folder offers to
/// leave them again — a folder whose chats the user is not in would draw an empty tab.</para>
/// </summary>
public interface IChatlistMembershipService
{
    /// <summary>
    /// Joins every channel of <paramref name="peers"/> the user is not already in. Users and legacy chats are
    /// skipped — there is nothing to join for them.
    /// </summary>
    Task JoinAsync(IRequestInput input, IReadOnlyCollection<Peer> peers);

    /// <summary>Leaves every channel of <paramref name="peers"/> the user is still in.</summary>
    Task LeaveAsync(IRequestInput input, IReadOnlyCollection<Peer> peers);
}

/// <inheritdoc />
public class ChatlistMembershipService(
    ICommandBus commandBus,
    IChannelAppService channelAppService,
    IQueryProcessor queryProcessor,
    IPremiumLimitGuard premiumLimitGuard,
    ILogger<ChatlistMembershipService> logger) : IChatlistMembershipService, ITransientDependency
{
    public async Task JoinAsync(IRequestInput input, IReadOnlyCollection<Peer> peers)
    {
        var channels = await GetChannelsAsync(input, peers);

        // Every outright join counts against channels_limit; checked for the whole folder up front so a
        // link is either joined completely or not at all. Join requests count only once approved.
        await premiumLimitGuard.EnsureCanJoinChannelsAsync(input.UserId,
            channels.Count(c => !c.IsMember && !c.Channel.JoinRequest));

        foreach (var (channelReadModel, isMember) in channels)
        {
            if (isMember)
            {
                continue;
            }

            // ReqMsgId = 0: the join must not answer the folder request. The saga's domain event handler skips
            // the RPC reply for that value, exactly as a service message send does.
            var requestInfo = input.ToRequestInfo() with { ReqMsgId = 0 };

            if (channelReadModel.JoinRequest)
            {
                await commandBus.PublishAsync(new CreateJoinChannelRequestCommand(
                    JoinChannelId.Create(channelReadModel.ChannelId, input.UserId),
                    requestInfo,
                    channelReadModel.ChannelId,
                    null));

                continue;
            }

            var channelHistoryMinId = channelReadModel.HiddenPreHistory ? channelReadModel.TopMessageId : 0;

            // Each command addresses a fresh TempId aggregate, so the ReqMsgId based duplicate detection of
            // DistinctCommand cannot collapse the batch into a single join.
            await commandBus.PublishAsync(new StartJoinChannelCommand(
                TempId.New,
                requestInfo,
                channelReadModel.ChannelId,
                channelReadModel.Broadcast,
                channelReadModel.TopMessageId,
                channelHistoryMinId));

            logger.LogDebug("User {UserId} joins channel {ChannelId} through a chat folder link",
                input.UserId, channelReadModel.ChannelId);
        }
    }

    public async Task LeaveAsync(IRequestInput input, IReadOnlyCollection<Peer> peers)
    {
        foreach (var (channelReadModel, isMember) in await GetChannelsAsync(input, peers))
        {
            if (!isMember)
            {
                continue;
            }

            await commandBus.PublishAsync(new LeaveChannelCommand(
                ChannelMemberId.Create(channelReadModel.ChannelId, input.UserId),
                input.ToRequestInfo() with { ReqMsgId = 0 },
                channelReadModel.ChannelId,
                input.UserId,
                false));

            logger.LogDebug("User {UserId} leaves channel {ChannelId} with a shared folder",
                input.UserId, channelReadModel.ChannelId);
        }
    }

    private async Task<List<(IChannelReadModel Channel, bool IsMember)>> GetChannelsAsync(IRequestInput input,
        IReadOnlyCollection<Peer> peers)
    {
        var result = new List<(IChannelReadModel, bool)>();
        var currentDate = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var peer in peers.Where(p => p.PeerType == PeerType.Channel).DistinctBy(p => p.PeerId))
        {
            var channelReadModel = await channelAppService.GetAsync(peer.PeerId);
            if (channelReadModel == null)
            {
                continue;
            }

            var member = await queryProcessor.ProcessAsync(
                new GetChannelMemberByUserIdQuery(channelReadModel.ChannelId, input.UserId));

            var isMember = member is { Left: false } && !BannedRightsHelper.IsCurrentlyKicked(member, currentDate);
            result.Add((channelReadModel, isMember));
        }

        return result;
    }
}
