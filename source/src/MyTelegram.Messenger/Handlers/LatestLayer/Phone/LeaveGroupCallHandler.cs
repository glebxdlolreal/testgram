using MyTelegram.Services.Services;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;

internal sealed class LeaveGroupCallHandler(
    IMongoDatabase mongoDatabase,
    IAccessHashHelper2 accessHashHelper2,
    IPeerHelper peerHelper,
    IObjectMessageSender objectMessageSender,
    IGroupCallMediaServer mediaServer)
    : RpcResultObjectHandler<RequestLeaveGroupCall, IUpdates>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestLeaveGroupCall obj)
    {
        var filter = GroupCallStateHelper.Filter(obj.Call, input, accessHashHelper2);
        var groupCall = await _groupCallCollection.Find(filter).FirstOrDefaultAsync();
        if (groupCall == null)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }

        var participant = GroupCallStateHelper.FindParticipantByUser(groupCall, input.UserId, obj.Source);
        if (participant != null)
        {
            var peerType = participant.PeerType;
            var peerId = participant.PeerId;
            var source = participant.Source;
            GroupCallParticipantDoc? removed = null;
            var updated = await GroupCallStateHelper.UpdateWithRetryAsync(_groupCallCollection, groupCall, call =>
            {
                removed = call.Participants.FirstOrDefault(p =>
                    p.PeerType == peerType && p.PeerId == peerId && p.Source == source && !p.Left);
                if (removed == null)
                {
                    return false;
                }

                call.Participants.Remove(removed);
                call.Version++;
                return true;
            });
            if (updated == null || removed == null)
            {
                return GroupCallStateHelper.Updates();
            }

            groupCall = updated;
            await mediaServer.LeaveAsync(groupCall.CallId, input.UserId);
            removed.Left = true;
            removed.Muted = false;
            removed.RaiseHand = false;
            removed.Volume = null;
            removed.VolumeBySelf = false;
            var participantsUpdate = GroupCallStateHelper.Updates(
                GroupCallStateHelper.CreateParticipantsUpdate(groupCall, input.UserId, peerHelper, [removed]));
            await GroupCallStateHelper.PushUpdatesToCallSubscribersAsync(
                objectMessageSender,
                groupCall,
                participantsUpdate,
                input.UserId,
                [input.UserId]);

            // Telegram pushes the versioned "left" entry to everybody else, but answers the leaver itself
            // with an unversioned one.
            var response = GroupCallStateHelper.CreateParticipantsUpdate(groupCall, input.UserId, peerHelper, [removed]);
            foreach (var entry in response.Participants.OfType<TGroupCallParticipant>())
            {
                entry.Versioned = false;
            }

            return GroupCallStateHelper.Updates(response);
        }

        return GroupCallStateHelper.Updates();
    }
}
