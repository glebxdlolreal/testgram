using MyTelegram.Services.Services;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;
/// <summary>
/// Stop screen sharing in a group call
/// Possible errors
/// Code Type Description
/// 400 GROUPCALL_INVALID The specified group call is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/phone.leaveGroupCallPresentation"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class LeaveGroupCallPresentationHandler(
    IMongoDatabase mongoDatabase,
    IAccessHashHelper2 accessHashHelper2,
    IPeerHelper peerHelper,
    IObjectMessageSender objectMessageSender,
    IGroupCallMediaServer mediaServer)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestLeaveGroupCallPresentation, MyTelegram.Schema.IUpdates>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestLeaveGroupCallPresentation obj)
    {
        var filter = GroupCallStateHelper.Filter(obj.Call, input, accessHashHelper2);
        var groupCall = await _groupCallCollection.Find(filter).FirstOrDefaultAsync();
        if (groupCall == null)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }

        var participant = GroupCallStateHelper.FindParticipantByUser(groupCall, input.UserId);
        if (participant == null)
        {
            return GroupCallStateHelper.Updates();
        }

        participant.PresentationPaused = true;
        participant.PresentationParamsJson = null;
        participant.PresentationEndpoint = null;
        await _groupCallCollection.ReplaceOneAsync(filter, groupCall);
        await mediaServer.LeaveAsync(groupCall.CallId, GroupCallMediaAllocation.PresentationKey(input.UserId));

        var updates = GroupCallStateHelper.Updates(
            GroupCallStateHelper.CreateParticipantsUpdate(groupCall, input.UserId, peerHelper, [participant]));
        await GroupCallStateHelper.PushUpdatesToCallSubscribersAsync(
            objectMessageSender,
            groupCall,
            updates,
            input.UserId);
        return updates;
    }
}
