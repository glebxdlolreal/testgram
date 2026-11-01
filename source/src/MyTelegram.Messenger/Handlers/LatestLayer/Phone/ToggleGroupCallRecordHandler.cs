using MyTelegram.Services.Services;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;
/// <summary>
/// Start or stop recording a group call: the recorded audio and video streams will be automatically sent to <code>Saved messages</code> (the chat with ourselves).
/// Possible errors
/// Code Type Description
/// 403 GROUPCALL_FORBIDDEN The group call has already ended.
/// 400 GROUPCALL_INVALID The specified group call is invalid.
/// 400 GROUPCALL_NOT_MODIFIED Group call settings weren't modified.
/// <para><c>See <a href="https://corefork.telegram.org/method/phone.toggleGroupCallRecord"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ToggleGroupCallRecordHandler(
    IMongoDatabase mongoDatabase,
    IAccessHashHelper2 accessHashHelper2,
    IPeerHelper peerHelper,
    IObjectMessageSender objectMessageSender,
    IChannelAdminRightsChecker channelAdminRightsChecker)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestToggleGroupCallRecord, MyTelegram.Schema.IUpdates>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestToggleGroupCallRecord obj)
    {
        var filter = GroupCallStateHelper.Filter(obj.Call, input, accessHashHelper2);
        var groupCall = await _groupCallCollection.Find(filter).FirstOrDefaultAsync();
        if (groupCall == null)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }
        if (!groupCall.Active)
        {
            RpcErrors.RpcErrors403.GroupcallForbidden.ThrowRpcError();
            return null!;
        }
        await GroupCallStateHelper.EnsureCanManageCallAsync(
            groupCall,
            input.UserId,
            channelAdminRightsChecker,
            RpcErrors.RpcErrors403.GroupcallForbidden);

        var recordingActive = groupCall.RecordStartDate.HasValue;
        if (recordingActive == obj.Start)
        {
            RpcErrors.RpcErrors400.GroupcallNotModified.ThrowRpcError();
            return null!;
        }

        if (obj.Start)
        {
            groupCall.RecordStartDate = GroupCallStateHelper.CurrentDate();
            groupCall.RecordVideoActive = obj.Video;
            groupCall.RecordTitle = obj.Title;
        }
        else
        {
            groupCall.RecordStartDate = null;
            groupCall.RecordVideoActive = false;
            groupCall.RecordTitle = null;
        }
        await _groupCallCollection.ReplaceOneAsync(filter, groupCall);
        var updates = GroupCallStateHelper.Updates(GroupCallStateHelper.CreateCallUpdate(groupCall, input.UserId, peerHelper));
        await GroupCallStateHelper.PushUpdatesToCallSubscribersAsync(
            objectMessageSender,
            groupCall,
            updates,
            input.UserId);
        return updates;
    }
}
