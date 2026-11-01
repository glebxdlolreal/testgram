using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;
/// <summary>
/// Create a group call or livestream
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 400 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// 400 CREATE_CALL_FAILED An error occurred while creating the call.
/// 400 GROUPCALL_ALREADY_DISCARDED The group call was already discarded.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 SCHEDULE_DATE_INVALID Invalid schedule date provided.
/// <para><c>See <a href="https://corefork.telegram.org/method/phone.createGroupCall"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class CreateGroupCallHandler(
    IIdGenerator idGenerator,
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IMessageAppService messageAppService,
    IOptionsMonitor<MyTelegramMessengerServerOptions> options,
    IChannelAdminRightsChecker channelAdminRightsChecker,
    IQueryProcessor queryProcessor)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestCreateGroupCall, MyTelegram.Schema.IUpdates>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");
    private readonly IMongoCollection<GroupCallRtmpStreamDocument> _rtmpStreamCollection =
        mongoDatabase.GetCollection<GroupCallRtmpStreamDocument>("group_call_rtmp_streams");

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestCreateGroupCall obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer == null)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
            return null!;
        }

        var broadcast = false;
        if (peer.PeerType == PeerType.Channel)
        {
            await channelAdminRightsChecker.CheckAdminRightAsync(
                peer.PeerId,
                input.UserId,
                rights => rights.ManageCall,
                RpcErrors.RpcErrors400.ChatAdminRequired);
            broadcast = (await queryProcessor.ProcessAsync(new GetChannelByIdQuery(peer.PeerId)))?.Broadcast == true;
        }

        if (obj.ScheduleDate is { } scheduleDate && scheduleDate <= GroupCallStateHelper.CurrentDate())
        {
            RpcErrors.RpcErrors400.ScheduleDateInvalid.ThrowRpcError();
            return null!;
        }

        var existing = await _groupCallCollection
            .Find(g => g.CreatorId == input.UserId && g.RandomId == obj.RandomId)
            .FirstOrDefaultAsync();
        if (existing != null)
        {
            return GroupCallStateHelper.Updates(GroupCallStateHelper.CreateCallUpdate(existing, input.UserId, peerHelper));
        }

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var callId = await idGenerator.NextIdAsync(IdType.MessageId, input.UserId);
            var idExists = await _groupCallCollection
                .Find(g => g.Id == callId || g.CallId == callId)
                .AnyAsync();
            if (idExists)
            {
                continue;
            }

            var rtmpStreamId = GroupCallRtmpHelper.GetStreamId(peer.PeerId, (int)peer.PeerType);
            var rtmpStream = obj.RtmpStream
                ? await _rtmpStreamCollection.Find(stream => stream.Id == rtmpStreamId).FirstOrDefaultAsync()
                : null;
            var rtmpUrl = GroupCallRtmpHelper.GetRtmpStreamUrl(options.CurrentValue);
            if (obj.RtmpStream && rtmpStream == null)
            {
                rtmpStream = GroupCallRtmpHelper.CreateStreamDocument(
                    rtmpStreamId,
                    peer.PeerId,
                    (int)peer.PeerType,
                    rtmpUrl);
                await _rtmpStreamCollection.ReplaceOneAsync(
                    stream => stream.Id == rtmpStreamId,
                    rtmpStream,
                    new ReplaceOptions { IsUpsert = true });
            }

            var call = new GroupCallDocument
            {
                Id = callId,
                CallId = callId,
                AccessHash = GroupCallStateHelper.CreateAccessHash(),
                RandomId = obj.RandomId,
                PeerId = peer.PeerId,
                PeerType = (int)peer.PeerType,
                CreatorId = input.UserId,
                Title = obj.Title,
                ScheduleDate = obj.ScheduleDate,
                RtmpStream = obj.RtmpStream,
                // A channel's video chat starts join_muted and stays so (captured 2026-11-01).
                Broadcast = broadcast,
                JoinMuted = broadcast,
                RtmpUrl = obj.RtmpStream ? GroupCallRtmpHelper.GetStoredOrDefault(rtmpStream?.Url, rtmpUrl) : null,
                RtmpStreamKey = obj.RtmpStream ? rtmpStream?.Key : null,
                Date = GroupCallStateHelper.CurrentDate()
            };

            try
            {
                await _groupCallCollection.InsertOneAsync(call);
                await SendServiceMessageAsync(input, call);
                if (!call.ScheduleDate.HasValue)
                {
                    await AdminLogHelper.LogStartGroupCall(mongoDatabase, call, input.UserId);
                }

                return GroupCallStateHelper.Updates(GroupCallStateHelper.CreateCallUpdate(call, input.UserId, peerHelper));
            }
            catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
            {
            }
        }

        RpcErrors.RpcErrors400.CreateCallFailed.ThrowRpcError();
        return null!;
    }

    private async Task SendServiceMessageAsync(IRequestInput input, GroupCallDocument call)
    {
        IMessageAction action = call.ScheduleDate.HasValue
            ? new TMessageActionGroupCallScheduled
            {
                Call = GroupCallStateHelper.ToInputGroupCall(call),
                ScheduleDate = call.ScheduleDate.Value
            }
            : new TMessageActionGroupCall
            {
                Call = GroupCallStateHelper.ToInputGroupCall(call)
            };

        await GroupCallStateHelper.SendGroupCallServiceMessageAsync(messageAppService, input, call, action);
    }
}
