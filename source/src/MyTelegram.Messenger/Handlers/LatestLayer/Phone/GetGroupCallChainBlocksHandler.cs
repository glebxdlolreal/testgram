using MyTelegram.Services.Services;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;
/// <summary>
/// Fetch the blocks of a <a href="https://corefork.telegram.org/api/end-to-end/group-calls">conference blockchain »</a>.
/// Possible errors
/// Code Type Description
/// 400 GROUPCALL_INVALID The specified group call is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/phone.getGroupCallChainBlocks"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetGroupCallChainBlocksHandler(
    IMongoDatabase mongoDatabase,
    IAccessHashHelper2 accessHashHelper2)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestGetGroupCallChainBlocks, MyTelegram.Schema.IUpdates>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestGetGroupCallChainBlocks obj)
    {
        var groupCall = await _groupCallCollection.Find(GroupCallStateHelper.Filter(obj.Call, input, accessHashHelper2)).FirstOrDefaultAsync();
        if (groupCall == null || !groupCall.Conference)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }

        // Subchain 0 carries the conference key material, so only participants may page the chain.
        if (!GroupCallStateHelper.IsJoinedByUser(groupCall, input.UserId) && groupCall.CreatorId != input.UserId)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }

        var blocks = GroupCallStateHelper.GetChainBlocksPage(
            groupCall,
            obj.SubChainId,
            obj.Offset,
            obj.Limit,
            out var nextOffset);

        return GroupCallStateHelper.Updates(GroupCallStateHelper.CreateChainBlocksUpdate(
            groupCall,
            obj.SubChainId,
            blocks,
            nextOffset));
    }
}
