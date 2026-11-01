using MyTelegram.Services.Services;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;
/// <summary>
/// Invite a user to a conference call.
/// Possible errors
/// Code Type Description
/// 400 GROUPCALL_INVALID The specified group call is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/phone.inviteConferenceCallParticipant"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class InviteConferenceCallParticipantHandler(
    IMongoDatabase mongoDatabase,
    IAccessHashHelper2 accessHashHelper2,
    IPeerHelper peerHelper,
    IIdGenerator idGenerator,
    IObjectMessageSender objectMessageSender)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestInviteConferenceCallParticipant, MyTelegram.Schema.IUpdates>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestInviteConferenceCallParticipant obj)
    {
        var filter = GroupCallStateHelper.Filter(obj.Call, input, accessHashHelper2);
        var groupCall = await _groupCallCollection.Find(filter).FirstOrDefaultAsync();
        if (groupCall == null || !groupCall.Conference || !groupCall.Active)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }

        // Inviting adds the target to InvitedUserIds (which grants them join rights) and pushes a
        // service message attributed to the caller, so only participants may invite.
        if (!GroupCallStateHelper.IsJoinedByUser(groupCall, input.UserId) && groupCall.CreatorId != input.UserId)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }

        var userId = peerHelper.GetPeer(obj.UserId, input.UserId).PeerId;
        var date = GroupCallStateHelper.CurrentDate();
        var messageId = await idGenerator.NextIdAsync(IdType.MessageId, userId);
        if (!groupCall.InvitedUserIds.Contains(userId))
        {
            groupCall.InvitedUserIds.Add(userId);
        }

        groupCall.InviteMessages.RemoveAll(message => message.UserId == userId && message.MessageId == (int)messageId);
        groupCall.InviteMessages.Add(new GroupCallInviteMessageDoc
        {
            MessageId = (int)messageId,
            UserId = userId,
            FromUserId = input.UserId,
            Video = obj.Video,
            Date = date
        });
        groupCall.Version++;
        await _groupCallCollection.ReplaceOneAsync(filter, groupCall);

        var otherParticipants = new TVector<MyTelegram.Schema.IPeer>(
            groupCall.Participants
                .Where(p => p.PeerType == (int)PeerType.User && p.PeerId != userId)
                .Take(4)
                .Select(p => GroupCallStateHelper.ToPeer((PeerType)p.PeerType, p.PeerId)));
        var action = new MyTelegram.Schema.TMessageActionConferenceCall
        {
            CallId = groupCall.CallId,
            Video = obj.Video,
            OtherParticipants = otherParticipants.Count > 0 ? otherParticipants : null
        };

        var targetUpdates = GroupCallStateHelper.Updates(new MyTelegram.Schema.TUpdateNewMessage
        {
            Pts = 0,
            PtsCount = 0,
            Message = new MyTelegram.Schema.TMessageService
            {
                Id = (int)messageId,
                FromId = new MyTelegram.Schema.TPeerUser { UserId = input.UserId },
                PeerId = new MyTelegram.Schema.TPeerUser { UserId = input.UserId },
                Date = date,
                Action = action,
                Out = false
            }
        });
        await objectMessageSender.PushMessageToPeerAsync(new Peer(PeerType.User, userId), targetUpdates);

        return GroupCallStateHelper.Updates(new MyTelegram.Schema.TUpdateNewMessage
        {
            Pts = 0,
            PtsCount = 0,
            Message = new MyTelegram.Schema.TMessageService
            {
                Id = (int)messageId,
                FromId = new MyTelegram.Schema.TPeerUser { UserId = input.UserId },
                PeerId = new MyTelegram.Schema.TPeerUser { UserId = userId },
                Date = date,
                Action = action,
                Out = true
            }
        });
    }
}
