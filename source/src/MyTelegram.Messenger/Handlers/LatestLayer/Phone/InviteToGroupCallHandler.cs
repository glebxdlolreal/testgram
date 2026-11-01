using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Services.Services;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;
/// <summary>
/// Invite a set of users to a group call.
/// Possible errors
/// Code Type Description
/// 403 CHAT_TYPE_INVALID The specified user type is invalid.
/// 403 GROUPCALL_FORBIDDEN The group call has already ended.
/// 400 GROUPCALL_INVALID The specified group call is invalid.
/// 400 INVITE_FORBIDDEN_WITH_JOINAS If the user has anonymously joined a group call as a channel, they can't invite other users to the group call because that would cause deanonymization, because the invite would be sent using the original user ID, not the anonymized channel ID.
/// 400 USER_ALREADY_INVITED You have already invited this user.
/// 403 USER_NOT_PARTICIPANT You're not a member of this supergroup/channel.
/// <para><c>See <a href="https://corefork.telegram.org/method/phone.inviteToGroupCall"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class InviteToGroupCallHandler(
    IMongoDatabase mongoDatabase,
    IAccessHashHelper2 accessHashHelper2,
    IPeerHelper peerHelper,
    IMessageAppService messageAppService)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestInviteToGroupCall, MyTelegram.Schema.IUpdates>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestInviteToGroupCall obj)
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

        // Only someone who is in the call can invite others to it (Telegram answers GROUPCALL_FORBIDDEN),
        // and not while joined as a channel: the invite would reveal who is behind it.
        var inviter = GroupCallStateHelper.FindParticipantByUser(groupCall, input.UserId);
        if (inviter == null)
        {
            RpcErrors.RpcErrors403.GroupcallForbidden.ThrowRpcError();
            return null!;
        }
        if (inviter.PeerType != (int)PeerType.User)
        {
            RpcErrors.RpcErrors400.InviteForbiddenWithJoinas.ThrowRpcError();
            return null!;
        }

        var invitedUserIds = new List<long>();
        foreach (var user in obj.Users)
        {
            var userId = peerHelper.GetPeer(user, input.UserId).PeerId;
            if (userId <= 0 || userId == input.UserId || peerHelper.IsBotUser(userId))
            {
                RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
                return null!;
            }
            if (GroupCallStateHelper.IsJoinedByUser(groupCall, userId))
            {
                RpcErrors.RpcErrors400.UserAlreadyParticipant.ThrowRpcError();
                return null!;
            }
            if (!invitedUserIds.Contains(userId))
            {
                invitedUserIds.Add(userId);
            }
        }

        if (invitedUserIds.Count == 0)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
            return null!;
        }

        var updated = await GroupCallStateHelper.UpdateWithRetryAsync(_groupCallCollection, groupCall, call =>
        {
            var added = false;
            foreach (var userId in invitedUserIds.Where(userId => !call.InvitedUserIds.Contains(userId)))
            {
                call.InvitedUserIds.Add(userId);
                added = true;
            }

            return added;
        });
        if (updated == null)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }

        // The invitation is a service message in the call's chat from the inviter; it reaches everybody,
        // the inviter included, through the normal message push.
        if (groupCall.PeerType != (int)PeerType.User)
        {
            await GroupCallStateHelper.SendGroupCallServiceMessageAsync(
                messageAppService,
                input,
                updated,
                new TMessageActionInviteToGroupCall
                {
                    Call = GroupCallStateHelper.ToInputGroupCall(updated),
                    Users = new TVector<long>(invitedUserIds)
                });
        }

        return GroupCallStateHelper.Updates();
    }
}
