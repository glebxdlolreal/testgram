using MyTelegram.Services.Services;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Phone;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;
/// <summary>
/// Get an <a href="https://corefork.telegram.org/api/links#video-chat-livestream-links">invite link</a> for a group call or livestream
/// Possible errors
/// Code Type Description
/// 400 GROUPCALL_INVALID The specified group call is invalid.
/// 403 PUBLIC_CHANNEL_MISSING You can only export group call invite links for public chats or channels.
/// <para><c>See <a href="https://corefork.telegram.org/method/phone.exportGroupCallInvite"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ExportGroupCallInviteHandler(
    IMongoDatabase mongoDatabase,
    IAccessHashHelper2 accessHashHelper2,
    IChannelAppService channelAppService,
    IChannelAdminRightsChecker channelAdminRightsChecker)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestExportGroupCallInvite, MyTelegram.Schema.Phone.IExportedGroupCallInvite>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    protected override async Task<MyTelegram.Schema.Phone.IExportedGroupCallInvite> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestExportGroupCallInvite obj)
    {
        var filter = GroupCallStateHelper.Filter(obj.Call, input, accessHashHelper2);
        var groupCall = await _groupCallCollection.Find(filter).FirstOrDefaultAsync();
        if (groupCall == null)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }

        await GroupCallStateHelper.EnsureCanManageCallAsync(groupCall, input.UserId, channelAdminRightsChecker);

        var changed = false;
        if (groupCall.Conference)
        {
            if (groupCall.InviteHash == null)
            {
                groupCall.InviteHash = GroupCallStateHelper.CreateInviteHash();
                changed = true;
            }

            var link = TelegramDeepLinkHelper.GetConferenceCallLink(groupCall.InviteHash);
            if (groupCall.InviteLink != link)
            {
                groupCall.InviteLink = link;
                changed = true;
            }
        }
        else
        {
            if (groupCall.PeerType != (int)PeerType.Channel)
            {
                RpcErrors.RpcErrors403.PublicChannelMissing.ThrowRpcError();
                return null!;
            }

            var channel = await channelAppService.GetAsync(groupCall.PeerId);
            if (string.IsNullOrWhiteSpace(channel?.UserName))
            {
                RpcErrors.RpcErrors403.PublicChannelMissing.ThrowRpcError();
                return null!;
            }

            if (obj.CanSelfUnmute)
            {
                if (groupCall.InviteHash == null)
                {
                    groupCall.InviteHash = GroupCallStateHelper.CreateInviteHash();
                    changed = true;
                }
            }
            else if (groupCall.InviteHash != null)
            {
                groupCall.InviteHash = null;
                changed = true;
            }

            var link = TelegramDeepLinkHelper.GetGroupCallInviteLink(
                channel.UserName,
                groupCall.InviteHash,
                channel.Broadcast);

            if (groupCall.InviteLink != link)
            {
                groupCall.InviteLink = link;
                changed = true;
            }
        }

        if (changed)
        {
            groupCall.Version++;
            await _groupCallCollection.ReplaceOneAsync(filter, groupCall);
        }

        return new MyTelegram.Schema.Phone.TExportedGroupCallInvite { Link = groupCall.InviteLink };
    }
}
