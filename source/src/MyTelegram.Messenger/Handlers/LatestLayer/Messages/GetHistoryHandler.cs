using MyTelegram.Services.Services;
namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Returns the conversation history with one interlocutor / within a chat
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 406 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 400 CHAT_ID_INVALID The provided chat id is invalid.
/// 400 CHAT_NOT_MODIFIED No changes were made to chat information because the new information you passed is identical to the current information.
/// 400 FROZEN_PARTICIPANT_MISSING The current account is <a href="https://corefork.telegram.org/api/auth#frozen-accounts">frozen</a>, and cannot access the specified peer.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// 500 NEED_DOC_INVALID  
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 TAKEOUT_INVALID The specified takeout ID is invalid.
/// 500 VOLUME_MOVE_INVALID  
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getHistory"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetHistoryHandler(IMessageAppService messageAppService, IQueryProcessor queryProcessor, IPeerHelper peerHelper, IChannelAppService channelAppService, IGetHistoryConverterService getHistoryConverterService, IAccessHashHelper2 accessHashHelper2) : RpcResultObjectHandler<RequestGetHistory, IMessages>
{
    /// <summary>Upper bound on one history page, matching the paging limit used by the search handlers.</summary>
    private const int MaxHistoryLimit = 100;

    protected override async Task<IMessages> HandleCoreAsync(IRequestInput input, RequestGetHistory obj)
    {
        var userId = input.UserId;
        // A wrong hash is CHANNEL_INVALID for a channel and PEER_ID_INVALID for a user, as on Telegram.
        await accessHashHelper2.CheckAccessHashAsync(input, obj.Peer);
        var peer = peerHelper.GetPeer(obj.Peer, userId);
        var ownerPeerId = peer.PeerType == PeerType.Channel ? peer.PeerId : userId;
        if (peer.PeerType == PeerType.Channel)
        {
            var channelMember = await queryProcessor.ProcessAsync(new GetChannelMemberByUserIdQuery(peer.PeerId, input.UserId));
            if (channelMember?.Kicked == true)
            {
                return new TChannelMessages
                {
                    Chats = new TVector<IChat>(),
                    Messages = new TVector<IMessage>(),
                    Users = new TVector<IUser>()
                };
            }

            var channelReadModel = await channelAppService.GetAsync(peer.PeerId);
            // A user who checked an invite link may read the history before joining, until the
            // peek window granted by messages.checkChatInvite runs out.
            if (await channelAppService.SendRpcErrorIfNoReadAccessAsync(input, channelReadModel!))
            {
                return null!;
            }
        }

        int channelHistoryMinId;
        //if (peer.PeerType == PeerType.Channel || peer.PeerType == PeerType.Chat)
        {
            var dialogReadModel = await queryProcessor.ProcessAsync(new GetDialogByIdQuery(DialogId.Create(input.UserId, peer).Value));
            channelHistoryMinId = dialogReadModel?.ChannelHistoryMinId ?? 0;
        }

        // The read-model store only applies a Mongo limit when it is > 0, so limit=0 or a negative
        // value meant "no limit at all" and returned the whole history in one response.
        var limit = obj.Limit is <= 0 or > MaxHistoryLimit ? MaxHistoryLimit : obj.Limit;

        var r = await messageAppService.GetHistoryAsync(new GetHistoryInput { OwnerPeerId = ownerPeerId, SelfUserId = userId, AddOffset = obj.AddOffset, Limit = limit, MaxId = obj.MaxId, MinId = obj.MinId, OffsetId = obj.OffsetId, Peer = peerHelper.GetPeer(obj.Peer, userId), ChannelHistoryMinId = channelHistoryMinId });
        return getHistoryConverterService.ToMessages(input, r, input.Layer);
    }
}