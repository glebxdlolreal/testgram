using MyTelegram.Services.Services;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;
/// <summary>
/// Get info about RTMP streams in a group call or livestream.<br/>
/// This method should be invoked to the same group/channel-related DC used for <a href="https://corefork.telegram.org/api/files#downloading-files">downloading livestream chunks</a>.<br/>
/// As usual, the media DC is preferred, if available.
/// Possible errors
/// Code Type Description
/// 400 GROUPCALL_INVALID The specified group call is invalid.
/// 400 GROUPCALL_JOIN_MISSING You haven't joined this group call.
/// <para><c>See <a href="https://corefork.telegram.org/method/phone.getGroupCallStreamChannels"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetGroupCallStreamChannelsHandler(
    IMongoDatabase mongoDatabase,
    IAccessHashHelper2 accessHashHelper2,
    IHlsGroupCallStreamService hlsGroupCallStreamService)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestGetGroupCallStreamChannels, MyTelegram.Schema.Phone.IGroupCallStreamChannels>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    protected override async Task<MyTelegram.Schema.Phone.IGroupCallStreamChannels> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestGetGroupCallStreamChannels obj)
    {
        var groupCall = await _groupCallCollection.Find(GroupCallStateHelper.Filter(obj.Call, input, accessHashHelper2)).FirstOrDefaultAsync();
        if (groupCall == null)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }
        if (!GroupCallStateHelper.IsJoinedByUser(groupCall, input.UserId) && groupCall.CreatorId != input.UserId)
        {
            RpcErrors.RpcErrors400.GroupcallJoinMissing.ThrowRpcError();
            return null!;
        }

        var channels = new TVector<IGroupCallStreamChannel>();
        foreach (var channel in await hlsGroupCallStreamService.GetChannelsAsync(groupCall))
        {
            channels.Add(new TGroupCallStreamChannel
            {
                Channel = channel.Channel,
                Scale = channel.Scale,
                LastTimestampMs = channel.LastTimestampMs
            });
        }

        return new MyTelegram.Schema.Phone.TGroupCallStreamChannels
        {
            Channels = channels
        };
    }
}
