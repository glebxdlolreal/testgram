using MyTelegram.Services.Services;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;

internal sealed class GetGroupCallStarsHandler(
    IMongoDatabase mongoDatabase,
    IAccessHashHelper2 accessHashHelper2)
    : RpcResultObjectHandler<RequestGetGroupCallStars, IGroupCallStars>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    protected override async Task<IGroupCallStars> HandleCoreAsync(IRequestInput input, RequestGetGroupCallStars obj)
    {
        var groupCall = await _groupCallCollection.Find(GroupCallStateHelper.Filter(obj.Call, input, accessHashHelper2)).FirstOrDefaultAsync();
        if (groupCall == null)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }

        return new TGroupCallStars
        {
            TotalStars = 0,
            TopDonors = new TVector<IGroupCallDonor>(),
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>()
        };
    }
}
