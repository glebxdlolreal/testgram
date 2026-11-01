using MyTelegram.Services.Services;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;
/// <summary>
/// Check whether the group call Server Forwarding Unit is currently receiving the streams with the specified WebRTC source IDs.<br/>
/// Returns an intersection of the source IDs specified in <code>sources</code>, and the source IDs currently being forwarded by the SFU.
/// Possible errors
/// Code Type Description
/// 400 GROUPCALL_INVALID The specified group call is invalid.
/// 400 GROUPCALL_JOIN_MISSING You haven't joined this group call.
/// <para><c>See <a href="https://corefork.telegram.org/method/phone.checkGroupCall"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class CheckGroupCallHandler(
    IMongoDatabase mongoDatabase,
    IAccessHashHelper2 accessHashHelper2,
    IGroupCallMediaServer mediaServer)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestCheckGroupCall, TVector<int>>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    protected override async Task<TVector<int>> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestCheckGroupCall obj)
    {
        var groupCall = await _groupCallCollection.Find(GroupCallStateHelper.Filter(obj.Call, input, accessHashHelper2)).FirstOrDefaultAsync();
        if (groupCall == null)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }
        if (!GroupCallStateHelper.IsJoinedByUser(groupCall, input.UserId))
        {
            RpcErrors.RpcErrors400.GroupcallJoinMissing.ThrowRpcError();
            return null!;
        }

        // The answer is what the media server actually holds: tgcalls rejoins when its own source is
        // missing from it. Without an SFU (or when it cannot be asked) the joined sources stand in.
        IReadOnlySet<int> sources = !groupCall.RtmpStream && await mediaServer.GetSourcesAsync(groupCall.CallId) is { } live
            ? live
            : GroupCallStateHelper.GetForwardedSources(groupCall.Participants);
        return new TVector<int>(obj.Sources.Where(sources.Contains));
    }
}
