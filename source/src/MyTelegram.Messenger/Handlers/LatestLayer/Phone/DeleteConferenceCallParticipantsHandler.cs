using MyTelegram.Services.Services;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;
/// <summary>
/// Remove participants from a conference call.Exactly one of the <code>only_left</code> and <code>kick</code> flags must be set.
/// Possible errors
/// Code Type Description
/// 400 GROUPCALL_INVALID The specified group call is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/phone.deleteConferenceCallParticipants"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class DeleteConferenceCallParticipantsHandler(
    IMongoDatabase mongoDatabase,
    IAccessHashHelper2 accessHashHelper2,
    IPeerHelper peerHelper,
    IChannelAdminRightsChecker channelAdminRightsChecker,
    IObjectMessageSender objectMessageSender)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestDeleteConferenceCallParticipants, MyTelegram.Schema.IUpdates>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestDeleteConferenceCallParticipants obj)
    {
        if (obj.OnlyLeft == obj.Kick)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }

        var filter = GroupCallStateHelper.Filter(obj.Call, input, accessHashHelper2);
        var groupCall = await _groupCallCollection.Find(filter).FirstOrDefaultAsync();
        if (groupCall == null || !groupCall.Conference || !groupCall.Active)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }

        // This both kicks participants and appends a caller-supplied block to subchain 0 (the key
        // material chain), so it must be restricted to whoever can manage the call.
        await GroupCallStateHelper.EnsureCanManageCallAsync(groupCall, input.UserId, channelAdminRightsChecker,
            RpcErrors.RpcErrors400.GroupcallInvalid);

        var ids = obj.Ids.ToHashSet();
        var previousParticipantUserIds = GroupCallStateHelper.GetParticipantUserIds(groupCall).ToList();
        var removed = groupCall.Participants
            .Where(p => p.PeerType == (int)PeerType.User && ids.Contains(p.PeerId))
            .ToList();
        foreach (var participant in removed)
        {
            participant.Left = true;
            participant.Muted = true;
        }

        var missing = ids
            .Except(removed.Select(p => p.PeerId))
            .Select(id => new GroupCallParticipantDoc
            {
                PeerId = id,
                PeerType = (int)PeerType.User,
                Source = 0,
                Date = GroupCallStateHelper.CurrentDate(),
                Left = true,
                Muted = true
            })
            .ToList();

        removed.AddRange(missing);
        groupCall.Participants.RemoveAll(p => p.PeerType == (int)PeerType.User && ids.Contains(p.PeerId));
        groupCall.ChainBlocks.Add(new GroupCallChainBlockDoc { SubChainId = 0, Block = obj.Block.ToArray() });
        groupCall.Version++;
        await _groupCallCollection.ReplaceOneAsync(filter, groupCall);

        var updates = GroupCallStateHelper.Updates(
            GroupCallStateHelper.CreateParticipantsUpdate(groupCall, input.UserId, peerHelper, removed),
            GroupCallStateHelper.CreateChainBlocksUpdate(
                groupCall,
                0,
                [obj.Block.ToArray()],
                groupCall.ChainBlocks.Count(block => block.SubChainId == 0)));

        await GroupCallStateHelper.PushUpdatesToCallSubscribersAsync(
            objectMessageSender,
            groupCall,
            updates,
            input.UserId,
            previousParticipantUserIds.Concat(ids));

        return updates;
    }
}
