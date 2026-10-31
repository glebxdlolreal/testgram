using MyTelegram.Messenger.Services.Premium;
using MyTelegram.Messenger.Helpers;
using MyTelegram.Messenger.Converters.ConverterServices;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Dismiss or approve all <a href="https://corefork.telegram.org/api/invites#join-requests">join requests</a> related to a specific chat or channel.
/// Possible errors
/// Code Type Description
/// 400 CHANNELS_TOO_MUCH You have joined too many channels/supergroups.
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 400 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// 403 CHAT_WRITE_FORBIDDEN You can't write in this chat.
/// 400 HIDE_REQUESTER_MISSING The join request was missing or was already handled.
/// 400 INVITE_HASH_EXPIRED The invite link has expired.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 USER_CHANNELS_TOO_MUCH One of the users you tried to add is already in too many channels/supergroups.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.hideAllChatJoinRequests"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class HideAllChatJoinRequestsHandler(IQueryProcessor queryProcessor, IPeerHelper peerHelper, IChannelAppService channelAppService, IChatConverterService chatConverterService, IChannelAdminRightsChecker channelAdminRightsChecker, ICommandBus commandBus, IChatInviteLinkHelper chatInviteLinkHelper, IChatInviteExportedConverterService chatInviteExportedConverterService, MongoDB.Driver.IMongoDatabase mongoDatabase, IPremiumLimitGuard premiumLimitGuard) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestHideAllChatJoinRequests, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestHideAllChatJoinRequests obj)
    {
        var channelPeer = peerHelper.GetPeer(obj.Peer);
        var channelId = channelPeer.PeerId;
        if (string.IsNullOrEmpty(obj.Link))
        {
            RpcErrors.RpcErrors400.InviteHashEmpty.ThrowRpcError();
        }

        await channelAdminRightsChecker.CheckAdminRightAsync(channelId, input.UserId, p => p.InviteUsers, RpcErrors.RpcErrors403.ChatAdminRequired);
        long? inviteId = null;
        if (!string.IsNullOrEmpty(obj.Link))
        {
            var chatInviteReadModel = await queryProcessor.ProcessAsync(new GetChatInviteByLinkQuery(chatInviteLinkHelper.GetHashFromLink(obj.Link)));

            // Invite hashes are global, so the link has to belong to the channel being managed.
            if (chatInviteReadModel == null || chatInviteReadModel.PeerId != channelId)
            {
                RpcErrors.RpcErrors400.InviteHashInvalid.ThrowRpcError();
            }

            inviteId = chatInviteReadModel!.InviteId;
        }

        var channelHistoryMinId = 0;
        int topMessageId = 0;
        bool broadcast = false;
        if (obj.Approved)
        {
            var channelReadModel = await channelAppService.GetAsync(channelId);
            if (channelReadModel.HiddenPreHistory)
            {
                channelHistoryMinId = channelReadModel.TopMessageId;
            }

            topMessageId = channelReadModel.TopMessageId;
            broadcast = channelReadModel.Broadcast;
        }

        await HideAllChatJoinRequestsAsync(input.ToRequestInfo(), channelId, inviteId, obj.Approved, topMessageId, channelHistoryMinId, broadcast);
        var channel = await chatConverterService.GetChannelAsync(input, channelId, false, false, input.Layer);
        return new TUpdates
        {
            Chats = [channel],
            Date = CurrentDate,
            Updates = new TVector<IUpdate>(new TUpdateChannel { ChannelId = channelPeer.PeerId, }),
            Users = new TVector<IUser>()
        };
    }

    private async Task HideAllChatJoinRequestsAsync(RequestInfo requestInfo, long channelId, long? inviteId, bool approved, int topMessageId, int channelHistoryMinId, bool broadcast)
    {
        var pageSize = 1000;
        var hasMoreData = true;

        // Requests approved in bulk usually share a handful of links at most, so the admin log
        // entries below reuse the invite objects instead of re-reading one per requester.
        var invites = new Dictionary<long, MyTelegram.Schema.IExportedChatInvite>();
        while (hasMoreData)
        {
            var chatInviteImporters = await queryProcessor.ProcessAsync(new GetChatInviteImportersQuery(channelId, ChatInviteRequestState.WaitingForApproval, inviteId, null, null, null, pageSize));
            foreach (var joinChannelRequestReadModel in chatInviteImporters)
            {
                if (joinChannelRequestReadModel.IsJoinRequestProcessed)
                {
                    continue;
                }

                // A requester already in as many channels as channels_limit allows cannot be added; their
                // request stays pending rather than failing the whole batch.
                if (approved && await premiumLimitGuard.IsAtChannelLimitAsync(joinChannelRequestReadModel.UserId))
                {
                    continue;
                }

                var command = new HideChatJoinRequestCommand(JoinChannelId.Create(channelId, joinChannelRequestReadModel.UserId), requestInfo, joinChannelRequestReadModel.UserId, approved, topMessageId, channelHistoryMinId, broadcast);
                await commandBus.PublishAsync(command);

                if (approved)
                {
                    var requestInviteId = joinChannelRequestReadModel.InviteId ?? 0;
                    if (!invites.TryGetValue(requestInviteId, out var invite))
                    {
                        invite = await ChatInviteExportedFiller.ToRequestInviteAsync(chatInviteExportedConverterService,
                            queryProcessor, channelId, joinChannelRequestReadModel.InviteId, MyTelegramConsts.Layer);
                        invites[requestInviteId] = invite;
                    }

                    await AdminLogHelper.LogParticipantJoinByRequest(mongoDatabase, channelId,
                        joinChannelRequestReadModel.UserId, invite, requestInfo.UserId);
                }
            }

            hasMoreData = chatInviteImporters.Count == pageSize;
        }
    }
}