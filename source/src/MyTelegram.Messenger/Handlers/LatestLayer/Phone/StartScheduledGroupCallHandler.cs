using MyTelegram.Services.Services;
using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;
/// <summary>
/// Start a scheduled group call.
/// Possible errors
/// Code Type Description
/// 403 GROUPCALL_ALREADY_STARTED The groupcall has already started, you can join directly using <a href="https://corefork.telegram.org/method/phone.joinGroupCall">phone.joinGroupCall</a>.
/// 400 GROUPCALL_INVALID The specified group call is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/phone.startScheduledGroupCall"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class StartScheduledGroupCallHandler(
    IMongoDatabase mongoDatabase,
    IAccessHashHelper2 accessHashHelper2,
    IPeerHelper peerHelper,
    IObjectMessageSender objectMessageSender,
    IMessageAppService messageAppService,
    IChannelAdminRightsChecker channelAdminRightsChecker)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestStartScheduledGroupCall, MyTelegram.Schema.IUpdates>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestStartScheduledGroupCall obj)
    {
        var filter = GroupCallStateHelper.Filter(obj.Call, input, accessHashHelper2);
        var groupCall = await _groupCallCollection.Find(filter).FirstOrDefaultAsync();
        if (groupCall == null)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }
        if (!groupCall.ScheduleDate.HasValue)
        {
            RpcErrors.RpcErrors403.GroupcallAlreadyStarted.ThrowRpcError();
            return null!;
        }

        // Only those who can manage the call may start it; Telegram refuses everybody else this way.
        await GroupCallStateHelper.EnsureCanManageCallAsync(
            groupCall,
            input.UserId,
            channelAdminRightsChecker,
            RpcErrors.RpcErrors403.GroupcallForbidden);

        var subscriberIds = groupCall.ScheduleStartSubscriberIds.ToList();
        groupCall.ScheduleDate = null;
        groupCall.ScheduleStartSubscriberIds.Clear();
        await _groupCallCollection.ReplaceOneAsync(filter, groupCall);
        await GroupCallStateHelper.SendGroupCallServiceMessageAsync(
            messageAppService,
            input,
            groupCall,
            new TMessageActionGroupCall
            {
                Call = GroupCallStateHelper.ToInputGroupCall(groupCall)
            });
        await AdminLogHelper.LogStartGroupCall(mongoDatabase, groupCall, input.UserId);
        var updates = GroupCallStateHelper.Updates(GroupCallStateHelper.CreateCallUpdate(groupCall, input.UserId, peerHelper));
        await GroupCallStateHelper.PushUpdatesToCallSubscribersAsync(
            objectMessageSender,
            groupCall,
            updates,
            input.UserId,
            subscriberIds);
        return updates;
    }
}
