using MyTelegram.Services.Services;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;
/// <summary>
/// Possible errors
/// Code Type Description
/// 400 GROUPCALL_INVALID The specified group call is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/phone.deleteGroupCallParticipantMessages"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class DeleteGroupCallParticipantMessagesHandler(
    IMongoDatabase mongoDatabase,
    IAccessHashHelper2 accessHashHelper2,
    IPeerHelper peerHelper,
    IObjectMessageSender objectMessageSender)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestDeleteGroupCallParticipantMessages, MyTelegram.Schema.IUpdates>, IObjectHandler
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestDeleteGroupCallParticipantMessages obj)
    {
        var filter = GroupCallStateHelper.Filter(obj.Call, input, accessHashHelper2);
        var groupCall = await _groupCallCollection.Find(filter).FirstOrDefaultAsync();
        if (groupCall == null || !groupCall.Active)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }

        var participant = peerHelper.GetPeer(obj.Participant, input.UserId);
        if (participant == null)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
            return null!;
        }

        var deletedIds = groupCall.Messages
            .Where(message =>
                !message.Deleted &&
                message.FromPeerId == participant.PeerId &&
                message.FromPeerType == (int)participant.PeerType)
            .Select(message => message.Id)
            .ToList();
        foreach (var message in groupCall.Messages.Where(message => deletedIds.Contains(message.Id)))
        {
            message.Deleted = true;
        }

        groupCall.Version++;
        await _groupCallCollection.ReplaceOneAsync(filter, groupCall);

        var updates = GroupCallStateHelper.Updates(GroupCallStateHelper.CreateDeleteMessagesUpdate(groupCall, deletedIds));
        await GroupCallStateHelper.PushUpdatesToCallSubscribersAsync(
            objectMessageSender,
            groupCall,
            updates,
            input.UserId);

        return updates;
    }
}
