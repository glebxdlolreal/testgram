namespace MyTelegram.Messenger.Services.AntiSpam;

public interface IAntiSpamActorService
{
    Task EnsureAvailableAsync(CancellationToken cancellationToken = default);
}

public interface IAntiSpamAccessService
{
    Task<IChannelReadModel> RequireAdminAsync(IRequestInput input, IInputChannel channel, bool requireDeleteMessages = true);
    Task<Peer> ResolveParticipantAsync(IRequestInput input, IInputPeer participant);
}

/// <summary>Authorization shared by the channel antispam RPCs; never trusts a client-supplied caller ID.</summary>
public sealed class AntiSpamAccessService(
    IQueryProcessor queryProcessor,
    IAccessHashHelper2 accessHashHelper,
    IFromMessagePeerResolver fromMessagePeerResolver,
    IPeerHelper peerHelper) : IAntiSpamAccessService, ITransientDependency
{
    public async Task<IChannelReadModel> RequireAdminAsync(IRequestInput input, IInputChannel channel, bool requireDeleteMessages = true)
    {
        var user = await queryProcessor.ProcessAsync(new GetUserByIdQuery(input.UserId));
        if (input.UserId <= 0 || user == null || user.IsDeleted == true)
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
        if (user!.Bot || peerHelper.IsBotUser(input.UserId))
            RpcErrors.RpcErrors400.BotMethodInvalid.ThrowRpcError();

        long channelId;
        switch (channel)
        {
            case TInputChannel direct:
                if (!await accessHashHelper.IsAccessHashValidAsync(input, direct.ChannelId, direct.AccessHash, AccessHashType.Channel))
                    RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
                channelId = direct.ChannelId;
                break;
            case TInputChannelFromMessage cited:
                channelId = await fromMessagePeerResolver.ResolveChannelIdAsync(input, cited.Peer, cited.MsgId, cited.ChannelId);
                break;
            default:
                RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
                return null!;
        }

        var model = await queryProcessor.ProcessAsync(new GetChannelByIdQuery(channelId));
        if (model == null || model.IsDeleted || !model.MegaGroup || model.Gigagroup)
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
        var member = await queryProcessor.ProcessAsync(new GetChannelMemberByUserIdQuery(channelId, input.UserId));
        if (member == null || member.Left || member.Kicked)
            RpcErrors.RpcErrors400.ChannelPrivate.ThrowRpcError();
        if (model!.CreatorId != input.UserId &&
            (!member!.IsAdmin || (requireDeleteMessages && !new ChatAdminRights(member.AdminRights).DeleteMessages)))
            RpcErrors.RpcErrors400.ChatAdminRequired.ThrowRpcError();
        return model;
    }

    public async Task<Peer> ResolveParticipantAsync(IRequestInput input, IInputPeer participant)
    {
        switch (participant)
        {
            case TInputPeerUserFromMessage cited:
                await fromMessagePeerResolver.ResolveUserIdAsync(input, cited.Peer, cited.MsgId, cited.UserId);
                break;
            case TInputPeerChannelFromMessage cited:
                await fromMessagePeerResolver.ResolveChannelIdAsync(input, cited.Peer, cited.MsgId, cited.ChannelId);
                break;
            case TInputPeerUser:
            case TInputPeerChannel:
                await accessHashHelper.CheckAccessHashAsync(input, participant);
                break;
            case TInputPeerSelf:
                break;
            default:
                RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
                return null!;
        }
        var peer = peerHelper.GetPeer(participant, input.UserId)!;
        if (peer.PeerType is PeerType.User or PeerType.Self)
        {
            var user = await queryProcessor.ProcessAsync(new GetUserByIdQuery(peer.PeerId));
            if (user == null) RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
            if (user!.IsDeleted == true) RpcErrors.RpcErrors400.InputUserDeactivated.ThrowRpcError();
        }
        else
        {
            var channel = await queryProcessor.ProcessAsync(new GetChannelByIdQuery(peer.PeerId));
            if (channel == null || channel.IsDeleted) RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
        }
        return peer;
    }
}
