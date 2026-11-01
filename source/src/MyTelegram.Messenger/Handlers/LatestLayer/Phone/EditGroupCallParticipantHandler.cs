using MyTelegram.Services.Services;
using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;
/// <summary>
/// Edit information about a given group call participantNote: <a href="https://corefork.telegram.org/mtproto/TL-combinators#conditional-fields">flags</a>.N?<a href="https://corefork.telegram.org/type/Bool">Bool</a> parameters can have three possible values:
/// Possible errors
/// Code Type Description
/// 403 GROUPCALL_FORBIDDEN The group call has already ended.
/// 400 GROUPCALL_INVALID The specified group call is invalid.
/// 400 PARTICIPANT_JOIN_MISSING Trying to enable a presentation, when the user hasn't joined the Video Chat with <a href="https://corefork.telegram.org/method/phone.joinGroupCall">phone.joinGroupCall</a>.
/// 400 RAISE_HAND_FORBIDDEN You cannot raise your hand.
/// 400 USER_VOLUME_INVALID The specified user volume is invalid.
/// 400 VIDEO_PAUSE_FORBIDDEN You cannot pause the video stream.
/// 400 VIDEO_STOP_FORBIDDEN You cannot stop the video stream.
/// <para><c>See <a href="https://corefork.telegram.org/method/phone.editGroupCallParticipant"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class EditGroupCallParticipantHandler(
    IMongoDatabase mongoDatabase,
    IAccessHashHelper2 accessHashHelper2,
    IPeerHelper peerHelper,
    IChannelAdminRightsChecker channelAdminRightsChecker,
    IObjectMessageSender objectMessageSender)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestEditGroupCallParticipant, MyTelegram.Schema.IUpdates>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    // Participant volume is expressed in 1/100 of a percent; clients clamp it to [1, 20000] (1% - 200%).
    private const int MinParticipantVolume = 1;
    private const int MaxParticipantVolume = 20000;

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestEditGroupCallParticipant obj)
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

        var peer = GroupCallStateHelper.NormalizeSelf(peerHelper.GetPeer(obj.Participant, input.UserId));
        var participant = peer == null
            ? null
            : groupCall.Participants.FirstOrDefault(p => p.PeerId == peer.PeerId && p.PeerType == (int)peer.PeerType);
        if (participant == null)
        {
            RpcErrors.RpcErrors400.ParticipantJoinMissing.ThrowRpcError();
            return null!;
        }

        if (obj.Volume.HasValue && (obj.Volume.Value < MinParticipantVolume || obj.Volume.Value > MaxParticipantVolume))
        {
            RpcErrors.RpcErrors400.UserVolumeInvalid.ThrowRpcError();
            return null!;
        }

        // A user may only raise their own hand; raising another participant's hand is forbidden.
        if (obj.RaiseHand is true &&
            !GroupCallStateHelper.IsParticipantControlledByUser(groupCall, participant, input.UserId))
        {
            RpcErrors.RpcErrors400.RaiseHandForbidden.ThrowRpcError();
            return null!;
        }

        // The same applies to every other participant field: mute/volume/video state may only be
        // changed on one's own participant entry, or by whoever can manage the call.
        var actedOnSomeoneElse = !GroupCallStateHelper.IsParticipantControlledByUser(groupCall, participant, input.UserId);
        if (actedOnSomeoneElse)
        {
            await GroupCallStateHelper.EnsureCanManageCallAsync(groupCall, input.UserId, channelAdminRightsChecker);
        }

        var canManage = !actedOnSomeoneElse &&
                        await GroupCallStateHelper.CanManageCallAsync(groupCall, input.UserId, channelAdminRightsChecker);
        var peerType = participant.PeerType;
        var peerId = participant.PeerId;
        var prevMuted = participant.Muted;
        var prevMutedByAdmin = participant.MutedByAdmin;
        var prevVolume = participant.Volume;

        var updated = await GroupCallStateHelper.UpdateWithRetryAsync(_groupCallCollection, groupCall, call =>
        {
            var target = call.Participants.FirstOrDefault(p => p.PeerId == peerId && p.PeerType == peerType);
            if (target == null)
            {
                return false;
            }

            ApplyChanges(target, obj, actedOnSomeoneElse, canManage);
            return true;
        });
        if (updated == null)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }

        groupCall = updated;
        participant = groupCall.Participants.FirstOrDefault(p => p.PeerId == peerId && p.PeerType == peerType);
        if (participant == null)
        {
            RpcErrors.RpcErrors400.ParticipantJoinMissing.ThrowRpcError();
            return null!;
        }

        await LogAdminActionAsync(input, groupCall, participant, actedOnSomeoneElse, prevMuted, prevMutedByAdmin, prevVolume);

        var updates = GroupCallStateHelper.Updates(
            GroupCallStateHelper.CreateParticipantsUpdate(groupCall, input.UserId, peerHelper, [participant]));
        await GroupCallStateHelper.PushUpdatesToCallSubscribersAsync(
            objectMessageSender,
            groupCall,
            updates,
            input.UserId);
        return updates;
    }

    /// <summary>
    /// Telegram's rules, as observed on the production servers:
    /// <list type="bullet">
    /// <item>A participant muted by an admin (or who joined a join_muted call) cannot unmute
    /// themselves; the request succeeds and leaves them muted.</item>
    /// <item>An admin "unmuting" somebody only allows them to speak: they stay muted until they
    /// unmute themselves, and their raised hand is lowered.</item>
    /// <item>An admin muting somebody also takes away the right to unmute.</item>
    /// <item>A volume set by the participant themselves is not "by admin".</item>
    /// </list>
    /// </summary>
    private static void ApplyChanges(
        GroupCallParticipantDoc participant,
        MyTelegram.Schema.Phone.RequestEditGroupCallParticipant obj,
        bool actedOnSomeoneElse,
        bool canManageCall)
    {
        if (obj.Muted.HasValue)
        {
            if (actedOnSomeoneElse)
            {
                if (obj.Muted.Value)
                {
                    participant.Muted = true;
                    participant.MutedByAdmin = true;
                }
                else
                {
                    participant.MutedByAdmin = false;
                    participant.RaiseHand = false;
                }
            }
            else if (obj.Muted.Value)
            {
                participant.Muted = true;
            }
            else if (!participant.MutedByAdmin || canManageCall)
            {
                participant.Muted = false;
                participant.MutedByAdmin = false;
                participant.RaiseHand = false;
            }
        }

        if (obj.Volume.HasValue)
        {
            participant.Volume = obj.Volume.Value;
            participant.VolumeBySelf = !actedOnSomeoneElse;
        }

        if (obj.RaiseHand.HasValue) participant.RaiseHand = obj.RaiseHand.Value;
        if (obj.VideoStopped.HasValue) participant.VideoStopped = obj.VideoStopped.Value;
        if (obj.VideoPaused.HasValue) participant.VideoPaused = obj.VideoPaused.Value;
        if (obj.PresentationPaused.HasValue) participant.PresentationPaused = obj.PresentationPaused.Value;
    }

    /// <summary>
    /// Only what an admin does to somebody else belongs in the admin log: a participant muting themselves
    /// or changing their own volume is not a moderation action.
    /// </summary>
    private async Task LogAdminActionAsync(
        IRequestInput input,
        GroupCallDocument groupCall,
        GroupCallParticipantDoc participant,
        bool actedOnSomeoneElse,
        bool prevMuted,
        bool prevMutedByAdmin,
        int? prevVolume)
    {
        if (!actedOnSomeoneElse || groupCall.PeerType != (int)PeerType.Channel)
        {
            return;
        }

        var tlParticipant = GroupCallStateHelper.ToParticipant(participant, input.UserId, peerHelper, call: groupCall);

        // An admin unmute does not change Muted; it lifts the admin's mute (lets the participant speak).
        if (participant.Muted != prevMuted || participant.MutedByAdmin != prevMutedByAdmin)
        {
            if (participant.MutedByAdmin)
            {
                await AdminLogHelper.LogParticipantMute(mongoDatabase, groupCall.PeerId, input.UserId, tlParticipant);
            }
            else
            {
                await AdminLogHelper.LogParticipantUnmute(mongoDatabase, groupCall.PeerId, input.UserId, tlParticipant);
            }
        }

        if (participant.Volume != prevVolume)
        {
            await AdminLogHelper.LogParticipantVolume(mongoDatabase, groupCall.PeerId, input.UserId, tlParticipant);
        }
    }
}
