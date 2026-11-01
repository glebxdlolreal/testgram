using MyTelegram.Services.Services;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;
/// <summary>
/// Subscribe or unsubscribe to a scheduled group call
/// Possible errors
/// Code Type Description
/// 403 GROUPCALL_ALREADY_STARTED The groupcall has already started, you can join directly using <a href="https://corefork.telegram.org/method/phone.joinGroupCall">phone.joinGroupCall</a>.
/// 400 GROUPCALL_INVALID The specified group call is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/phone.toggleGroupCallStartSubscription"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ToggleGroupCallStartSubscriptionHandler(
    IMongoDatabase mongoDatabase,
    IAccessHashHelper2 accessHashHelper2,
    IPeerHelper peerHelper,
    IObjectMessageSender objectMessageSender,
    IChannelAppService channelAppService)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestToggleGroupCallStartSubscription, MyTelegram.Schema.IUpdates>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestToggleGroupCallStartSubscription obj)
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

        // Any member of the chat may subscribe; outsiders get the usual not-a-member error.
        if ((PeerType)groupCall.PeerType == PeerType.Channel &&
            await channelAppService.SendRpcErrorIfNotChannelMemberAsync(input, groupCall.PeerId))
        {
            return null!;
        }

        groupCall.ScheduleStartSubscriberIds.Remove(input.UserId);
        if (obj.Subscribed)
        {
            groupCall.ScheduleStartSubscriberIds.Add(input.UserId);
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
