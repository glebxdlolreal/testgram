using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Services.Services;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;
/// <summary>
/// Get group call participants
/// Possible errors
/// Code Type Description
/// 400 GROUPCALL_INVALID The specified group call is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/phone.getGroupParticipants"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetGroupParticipantsHandler(
    IMongoDatabase mongoDatabase,
    IAccessHashHelper2 accessHashHelper2,
    IPeerHelper peerHelper,
    IUserConverterService userConverterService,
    IChatConverterService chatConverterService)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestGetGroupParticipants, MyTelegram.Schema.Phone.IGroupParticipants>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    protected override async Task<MyTelegram.Schema.Phone.IGroupParticipants> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestGetGroupParticipants obj)
    {
        var groupCall = await _groupCallCollection.Find(GroupCallStateHelper.Filter(obj.Call, input, accessHashHelper2)).FirstOrDefaultAsync();
        if (groupCall == null)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }

        var selectedPeers = obj.Ids
            .Select(id => peerHelper.GetPeer(id, input.UserId))
            .Where(peer => peer != null)
            .Select(peer => peer!.PeerId)
            .ToHashSet();
        var selectedSources = obj.Sources.ToHashSet();
        var participants = groupCall.Participants.AsEnumerable();
        if (selectedPeers.Count > 0)
        {
            participants = participants.Where(p => selectedPeers.Contains(p.PeerId));
        }
        if (selectedSources.Count > 0)
        {
            participants = participants.Where(p => selectedSources.Contains(p.Source));
        }

        var offset = GroupCallStateHelper.ParseOffset(obj.Offset);
        var limit = obj.Limit > 0 ? obj.Limit : 100;
        var page = participants.Skip(offset).Take(limit).ToList();
        var total = participants.Count();
        var nextOffset = total > offset + page.Count ? (offset + page.Count).ToString() : string.Empty;

        var (users, chats) = await GroupCallStateHelper.LoadUsersAndChatsAsync(
            input,
            userConverterService,
            chatConverterService,
            GroupCallStateHelper.GetParticipantPeerUserIds(page),
            GroupCallStateHelper.GetParticipantChannelIds(page));

        return new MyTelegram.Schema.Phone.TGroupParticipants
        {
            Count = total,
            Participants = new TVector<IGroupCallParticipant>(
                page.Select(p => (IGroupCallParticipant)GroupCallStateHelper.ToParticipant(p, input.UserId, peerHelper, call: groupCall))),
            NextOffset = nextOffset,
            Chats = chats,
            Users = users,
            Version = groupCall.Version
        };
    }
}
