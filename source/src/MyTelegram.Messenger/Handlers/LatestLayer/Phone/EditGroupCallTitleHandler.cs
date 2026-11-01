using MyTelegram.Services.Services;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;
/// <summary>
/// Edit the title of a group call or livestream
/// Possible errors
/// Code Type Description
/// 403 GROUPCALL_FORBIDDEN The group call has already ended.
/// 400 GROUPCALL_INVALID The specified group call is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/phone.editGroupCallTitle"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class EditGroupCallTitleHandler(
    IMongoDatabase mongoDatabase,
    IAccessHashHelper2 accessHashHelper2,
    IPeerHelper peerHelper,
    IChannelAdminRightsChecker channelAdminRightsChecker,
    IObjectMessageSender objectMessageSender)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestEditGroupCallTitle, MyTelegram.Schema.IUpdates>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestEditGroupCallTitle obj)
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

        groupCall.Title = obj.Title;
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
