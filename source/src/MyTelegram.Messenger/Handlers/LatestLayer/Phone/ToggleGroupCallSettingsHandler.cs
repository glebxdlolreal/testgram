using MyTelegram.Services.Services;
using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;
using MyTelegram.Messenger.Services.Phone;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;
/// <summary>
/// Change group call settings
/// Possible errors
/// Code Type Description
/// 400 GROUPCALL_INVALID The specified group call is invalid.
/// 400 GROUPCALL_NOT_MODIFIED Group call settings weren't modified.
/// <para><c>See <a href="https://corefork.telegram.org/method/phone.toggleGroupCallSettings"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ToggleGroupCallSettingsHandler(
    IMongoDatabase mongoDatabase,
    IAccessHashHelper2 accessHashHelper2,
    IPeerHelper peerHelper,
    IChannelAdminRightsChecker channelAdminRightsChecker,
    IObjectMessageSender objectMessageSender)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestToggleGroupCallSettings, MyTelegram.Schema.IUpdates>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestToggleGroupCallSettings obj)
    {
        var filter = GroupCallStateHelper.Filter(obj.Call, input, accessHashHelper2);
        var groupCall = await _groupCallCollection.Find(filter).FirstOrDefaultAsync();
        if (groupCall == null)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }

        var modified = false;
        var joinMutedChanged = false;

        await GroupCallStateHelper.EnsureCanManageCallAsync(groupCall, input.UserId, channelAdminRightsChecker);

        if (obj.JoinMuted.HasValue && groupCall.JoinMuted != obj.JoinMuted.Value)
        {
            groupCall.JoinMuted = obj.JoinMuted.Value;
            joinMutedChanged = true;
            modified = true;
        }
        if (obj.MessagesEnabled.HasValue && groupCall.MessagesEnabled != obj.MessagesEnabled.Value)
        {
            groupCall.MessagesEnabled = obj.MessagesEnabled.Value;
            modified = true;
        }
        if (obj.SendPaidMessagesStars.HasValue && groupCall.SendPaidMessagesStars != obj.SendPaidMessagesStars)
        {
            groupCall.SendPaidMessagesStars = obj.SendPaidMessagesStars;
            modified = true;
        }
        if (obj.ResetInviteHash)
        {
            groupCall.InviteHash = null;
            groupCall.InviteLink = null;
            modified = true;
        }
        if (!modified)
        {
            RpcErrors.RpcErrors400.GroupcallNotModified.ThrowRpcError();
            return null!;
        }

        await _groupCallCollection.ReplaceOneAsync(filter, groupCall);
        if (joinMutedChanged)
        {
            await AdminLogHelper.LogToggleGroupCallSetting(mongoDatabase, groupCall, input.UserId, groupCall.JoinMuted);
        }

        var updates = GroupCallStateHelper.Updates(GroupCallStateHelper.CreateCallUpdate(groupCall, input.UserId, peerHelper));
        await GroupCallStateHelper.PushUpdatesToCallSubscribersAsync(
            objectMessageSender,
            groupCall,
            updates,
            input.UserId);
        return updates;
    }
}
