using MyTelegram.Services.Services;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;
/// <summary>
/// Broadcast a blockchain block to all members of a conference call, see <a href="https://corefork.telegram.org/api/end-to-end/group-calls">here »</a> for more info.
/// Possible errors
/// Code Type Description
/// 400 GROUPCALL_INVALID The specified group call is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/phone.sendConferenceCallBroadcast"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SendConferenceCallBroadcastHandler(
    IMongoDatabase mongoDatabase,
    IAccessHashHelper2 accessHashHelper2,
    IObjectMessageSender objectMessageSender)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestSendConferenceCallBroadcast, MyTelegram.Schema.IUpdates>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestSendConferenceCallBroadcast obj)
    {
        var filter = GroupCallStateHelper.Filter(obj.Call, input, accessHashHelper2);
        var groupCall = await _groupCallCollection.Find(filter).FirstOrDefaultAsync();
        if (groupCall == null || !groupCall.Conference || !groupCall.Active)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }

        var isParticipant = GroupCallStateHelper.IsJoinedByUser(groupCall, input.UserId);
        if (!isParticipant && groupCall.CreatorId != input.UserId)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }

        groupCall.ChainBlocks.Add(new GroupCallChainBlockDoc { SubChainId = 1, Block = obj.Block.ToArray() });
        groupCall.Version++;
        await _groupCallCollection.ReplaceOneAsync(filter, groupCall);

        var updates = GroupCallStateHelper.Updates(GroupCallStateHelper.CreateChainBlocksUpdate(
            groupCall,
            1,
            [obj.Block.ToArray()],
            groupCall.ChainBlocks.Count(block => block.SubChainId == 1)));

        await GroupCallStateHelper.PushUpdatesToCallSubscribersAsync(
            objectMessageSender,
            groupCall,
            updates,
            input.UserId);

        return updates;
    }
}
