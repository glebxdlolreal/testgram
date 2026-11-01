using MyTelegram.Services.Services;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;
/// <summary>
/// Possible errors
/// Code Type Description
/// 400 GROUPCALL_INVALID The specified group call is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/phone.deleteGroupCallMessages"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class DeleteGroupCallMessagesHandler(
    IMongoDatabase mongoDatabase,
    IAccessHashHelper2 accessHashHelper2,
    IObjectMessageSender objectMessageSender)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestDeleteGroupCallMessages, MyTelegram.Schema.IUpdates>, IObjectHandler
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestDeleteGroupCallMessages obj)
    {
        var filter = GroupCallStateHelper.Filter(obj.Call, input, accessHashHelper2);
        var groupCall = await _groupCallCollection.Find(filter).FirstOrDefaultAsync();
        if (groupCall == null || !groupCall.Active)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }

        var ids = obj.Messages.ToHashSet();
        foreach (var message in groupCall.Messages.Where(message => ids.Contains(message.Id)))
        {
            message.Deleted = true;
        }
        groupCall.Version++;
        await _groupCallCollection.ReplaceOneAsync(filter, groupCall);

        var updates = GroupCallStateHelper.Updates(GroupCallStateHelper.CreateDeleteMessagesUpdate(groupCall, obj.Messages));
        await GroupCallStateHelper.PushUpdatesToCallSubscribersAsync(
            objectMessageSender,
            groupCall,
            updates,
            input.UserId);

        return updates;
    }
}
