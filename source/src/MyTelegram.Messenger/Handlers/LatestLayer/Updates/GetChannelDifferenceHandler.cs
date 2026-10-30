namespace MyTelegram.Messenger.Handlers.LatestLayer.Updates;
/// <summary>
/// Returns the difference between the current state of updates of a certain channel and transmitted.
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 406 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 403 CHANNEL_PUBLIC_GROUP_NA channel/supergroup not available.
/// 400 CHAT_NOT_MODIFIED No changes were made to chat information because the new information you passed is identical to the current information.
/// 403 CHAT_WRITE_FORBIDDEN You can't write in this chat.
/// 400 FROM_MESSAGE_BOT_DISABLED Bots can't use fromMessage min constructors.
/// 400 FROZEN_PARTICIPANT_MISSING The current account is <a href="https://corefork.telegram.org/api/auth#frozen-accounts">frozen</a>, and cannot access the specified peer.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// 500 NEED_DOC_INVALID  
/// 400 PERSISTENT_TIMESTAMP_EMPTY Persistent timestamp empty.
/// 400 PERSISTENT_TIMESTAMP_INVALID Persistent timestamp invalid.
/// 500 PERSISTENT_TIMESTAMP_OUTDATED Channel internal replication issues, try again later (treat this like an RPC_CALL_FAIL).
/// 400 PINNED_DIALOGS_TOO_MUCH Too many pinned dialogs.
/// 400 RANGES_INVALID Invalid range provided.
/// 400 USER_BANNED_IN_CHANNEL You're banned from sending messages in supergroups/channels.
/// <para><c>See <a href="https://corefork.telegram.org/method/updates.getChannelDifference"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class GetChannelDifferenceHandler(IMessageAppService messageAppService, IQueryProcessor queryProcessor, IAckCacheService ackCacheService, //ILayeredService<IDifferenceConverter> layeredService,
 IDifferenceConverterService differenceConverterService, ILogger<GetChannelDifferenceHandler> logger, IChannelAppService channelAppService,
 MyTelegram.Messenger.Services.Pts.IChannelPtsAllocator channelPtsAllocator) : RpcResultObjectHandler<MyTelegram.Schema.Updates.RequestGetChannelDifference, MyTelegram.Schema.Updates.IChannelDifference>
{
    //private readonly IRpcResultProcessor _rpcResultProcessor;
    private readonly ILogger<GetChannelDifferenceHandler> _logger = logger;
    protected override async Task<MyTelegram.Schema.Updates.IChannelDifference> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Updates.RequestGetChannelDifference obj)
    {
        var channelId = obj.Channel switch
        {
            TInputChannel inputChannel => inputChannel.ChannelId,
            TInputChannelFromMessage inputChannelFromMessage => inputChannelFromMessage.ChannelId,
            _ => 0L,
        };
        if (obj.Channel is TInputChannelFromMessage channelFromMessage)
        {
            await ValidateSourceMessageAsync(input, channelFromMessage.Peer, channelFromMessage.MsgId);
        }

        if (channelId != 0)
        {
            // Gate membership BEFORE reading any updates or messages. Computing isChannelMember alone
            // is not enforcement: a null member read model (never a member) used to fall straight
            // through and return the channel's message history to anyone, since the access-hash check
            // on this path was removed and PeerHelper.GetPeer does not validate hashes.
            var membershipCheckChannel = await channelAppService.GetAsync((long?)channelId);
            if (membershipCheckChannel == null)
            {
                RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
            }

            if (await channelAppService.SendRpcErrorIfNotChannelMemberAsync(input, membershipCheckChannel!))
            {
                return null!;
            }

            var isChannelMember = true;
            var channelMemberReadModel = await queryProcessor.ProcessAsync(new GetChannelMemberByUserIdQuery(channelId, input.UserId));
            isChannelMember = channelMemberReadModel != null;
            if (channelMemberReadModel != null && channelMemberReadModel.Kicked)
            {
                RpcErrors.RpcErrors403.ChannelPublicGroupNa.ThrowRpcError();
            }

            // The shared membership helper intentionally lets non-members read broadcast channels,
            // which is right for public broadcasts (they have a username). A private broadcast
            // channel (no username) must still require membership, otherwise getChannelDifference
            // leaks its updates to anyone. This is a targeted check; the shared helper is left
            // untouched so other read paths keep their public-channel policy.
            if (channelMemberReadModel == null &&
                membershipCheckChannel!.Broadcast &&
                string.IsNullOrEmpty(membershipCheckChannel.UserName))
            {
                RpcErrors.RpcErrors400.ChannelPrivate.ThrowRpcError();
            }

            // An unpublished native reservation must not be acknowledged as a readable cursor.
            // Keep the caller's cursor unchanged (including callers already beyond the ceiling).
            if (await channelPtsAllocator.GetReadablePtsCeilingAsync(channelId) != null)
            {
                return new MyTelegram.Schema.Updates.TChannelDifferenceEmpty
                {
                    Pts = obj.Pts, Final = false, Timeout = 1
                };
            }

            var limit = obj.Limit == 0 ? MyTelegramConsts.DefaultPtsTotalLimit : obj.Limit;
            limit = Math.Min(limit, MyTelegramConsts.DefaultPtsTotalLimit);
            var pts = obj.Pts;
            //if (pts == 1)
            //{
            //    pts = 0;
            //}
            var updatesReadModels = await queryProcessor.ProcessAsync(new GetUpdatesQuery(input.UserId, channelId, pts, 0, limit));
            var messageIds = updatesReadModels.Where(p => p.UpdatesType == UpdatesType.NewMessages).Select(p => p.MessageId ?? 0).ToList();
            var users = updatesReadModels.SelectMany(p => p.Users ?? []).ToList();
            var chats = updatesReadModels.SelectMany(p => p.Chats ?? []).ToList();
            chats.Add(channelId);
            // Add monoforum so client can cache it
            var channelReadModel = await channelAppService.GetAsync(channelId);
            if (channelReadModel?.LinkedMonoforumId.HasValue == true)
                chats.Add(channelReadModel.LinkedMonoforumId.Value);
            var dto = await messageAppService.GetChannelDifferenceAsync(new GetDifferenceInput(input.UserId, channelId, obj.Pts, limit, messageIds, users, chats));
            var maxPts = 0;
            if (updatesReadModels.Count > 0)
            {
                maxPts = updatesReadModels.Max(p => p.Pts);
            }

            var allUpdateList = StoredUpdateNormalizer.Normalize(updatesReadModels.Where(p => p.UpdatesType == UpdatesType.Updates).SelectMany(p => p.Updates ?? [])).ToList();
            // Recheck after materializing the response: a reservation may have begun during reads.
            // If it cleared again, its update is already durable before the ceiling can advance.
            if (await channelPtsAllocator.GetReadablePtsCeilingAsync(channelId) != null)
            {
                return new MyTelegram.Schema.Updates.TChannelDifferenceEmpty
                {
                    Pts = obj.Pts, Final = false, Timeout = 1
                };
            }
            if (dto.MessageList.Count == 0 && allUpdateList.Count == 0)
            {
                // A Pts projection can be newer than the updates snapshot read above. Without
                // an actual delivered update, advancing to it would hide a concurrently saved delete.
                return new MyTelegram.Schema.Updates.TChannelDifferenceEmpty
                {
                    Pts = obj.Pts, Final = true, Timeout = 30
                };
            }
            var r = differenceConverterService.ToChannelDifference(input, dto, isChannelMember, allUpdateList, maxPts, layer: input.Layer);
            if (updatesReadModels.Count > 0)
            {
                var channelMaxGlobalSeqNo = updatesReadModels.Max(p => p.GlobalSeqNo);
                await ackCacheService.AddRpcPtsToCacheAsync(input.ReqMsgId, 0, channelMaxGlobalSeqNo, new Peer(PeerType.Channel, channelId), true);
            }
            return r;
        }

        RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
        return null!;
    }

    private async Task ValidateSourceMessageAsync(IRequestInput input, IInputPeer peer, int messageId)
    {
        var ownerPeerId = GetOwnerPeerId(input, peer);
        if (ownerPeerId == null)
        {
            RpcErrors.RpcErrors400.MsgIdInvalid.ThrowRpcError();
        }

        var messageReadModel = await queryProcessor.ProcessAsync(new GetMessageByIdQuery(MessageId.Create(ownerPeerId.Value, messageId).Value));
        if (messageReadModel == null)
        {
            RpcErrors.RpcErrors400.MsgIdInvalid.ThrowRpcError();
        }
    }

    private static long? GetOwnerPeerId(IRequestInput input, IInputPeer peer) =>
        peer switch
        {
            TInputPeerChannel inputPeerChannel => inputPeerChannel.ChannelId,
            TInputPeerChat inputPeerChat => inputPeerChat.ChatId,
            TInputPeerUser or TInputPeerSelf => input.UserId,
            _ => null,
        };
}
