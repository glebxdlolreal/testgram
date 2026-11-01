using MyTelegram.Services.Services;
using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;
/// <summary>
/// Terminate a group call
/// Possible errors
/// Code Type Description
/// 400 GROUPCALL_ALREADY_DISCARDED The group call was already discarded.
/// 403 GROUPCALL_FORBIDDEN The group call has already ended.
/// 400 GROUPCALL_INVALID The specified group call is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/phone.discardGroupCall"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class DiscardGroupCallHandler(
    IMongoDatabase mongoDatabase,
    IAccessHashHelper2 accessHashHelper2,
    IPeerHelper peerHelper,
    IObjectMessageSender objectMessageSender,
    IMessageAppService messageAppService,
    IChannelAdminRightsChecker channelAdminRightsChecker)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestDiscardGroupCall, MyTelegram.Schema.IUpdates>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestDiscardGroupCall obj)
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
            RpcErrors.RpcErrors400.GroupcallAlreadyDiscarded.ThrowRpcError();
            return null!;
        }

        await GroupCallStateHelper.EnsureCanManageCallAsync(
            groupCall,
            input.UserId,
            channelAdminRightsChecker,
            RpcErrors.RpcErrors403.GroupcallForbidden);

        groupCall.Active = false;
        groupCall.Version++;
        var date = GroupCallStateHelper.CurrentDate();
        await _groupCallCollection.ReplaceOneAsync(filter, groupCall);
        var updateGroupCall = new TUpdateGroupCall
        {
            LiveStory = groupCall.LiveStory,
            Peer = peerHelper.ToPeer((PeerType)groupCall.PeerType, groupCall.PeerId),
            Call = GroupCallStateHelper.ToDiscardedGroupCall(groupCall, date)
        };
        var updateList = new List<IUpdate>
        {
            GroupCallStateHelper.CreatePeerChangedUpdate(groupCall),
            updateGroupCall
        };

        if (groupCall.LiveStory && groupCall.StoryId.HasValue)
        {
            var storyId = groupCall.StoryId.Value;
            await MarkLiveStoryDeletedAsync(groupCall);
            updateList.Add(new TUpdateStory
            {
                Peer = StoryHelper.CreatePeer(StoryHelper.ToStoryPeerType((PeerType)groupCall.PeerType), groupCall.PeerId),
                Story = new TStoryItemDeleted
                {
                    Id = storyId
                }
            });
        }

        var updates = GroupCallStateHelper.Updates(updateList.ToArray());
        await GroupCallStateHelper.PushUpdatesToCallSubscribersAsync(
            objectMessageSender,
            groupCall,
            updates,
            input.UserId,
            groupCall.InvitedUserIds);

        if (!groupCall.LiveStory)
        {
            await GroupCallStateHelper.SendGroupCallServiceMessageAsync(
                messageAppService,
                input,
                groupCall,
                new TMessageActionGroupCall
                {
                    Call = GroupCallStateHelper.ToInputGroupCall(groupCall),
                    Duration = GroupCallStateHelper.GetCallDuration(groupCall, date)
                });
        }

        await AdminLogHelper.LogDiscardGroupCall(mongoDatabase, groupCall, input.UserId);
        return updates;
    }

    private async Task MarkLiveStoryDeletedAsync(GroupCallDocument groupCall)
    {
        var storyPeerType = StoryHelper.ToStoryPeerType((PeerType)groupCall.PeerType);
        if (storyPeerType < 0 || !groupCall.StoryId.HasValue)
        {
            return;
        }
        var storyId = groupCall.StoryId.Value;

        var storyFilter = Builders<StoryDocument>.Filter.And(
            Builders<StoryDocument>.Filter.Eq(story => story.OwnerPeerId, groupCall.PeerId),
            Builders<StoryDocument>.Filter.Eq(story => story.OwnerPeerType, storyPeerType),
            Builders<StoryDocument>.Filter.Eq(story => story.StoryId, storyId),
            Builders<StoryDocument>.Filter.Eq(story => story.IsLive, true));

        var storyUpdate = Builders<StoryDocument>.Update
            .Set(story => story.Deleted, true)
            .Set(story => story.ExpireDate, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        await _storyCollection.UpdateOneAsync(storyFilter, storyUpdate);
    }
}
