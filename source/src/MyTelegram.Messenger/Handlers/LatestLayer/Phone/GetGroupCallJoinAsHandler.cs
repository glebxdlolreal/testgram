using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Services.Services;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;
/// <summary>
/// Get a list of peers that can be used to join a group call, presenting yourself as a specific user/channel.
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/phone.getGroupCallJoinAs"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetGroupCallJoinAsHandler(
    IPeerHelper peerHelper,
    IAccessHashHelper2 accessHashHelper2,
    IChannelAdminRightsChecker channelAdminRightsChecker,
    IUserConverterService userConverterService,
    IChatConverterService chatConverterService)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestGetGroupCallJoinAs, MyTelegram.Schema.Phone.IJoinAsPeers>
{
    protected override async Task<MyTelegram.Schema.Phone.IJoinAsPeers> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestGetGroupCallJoinAs obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer == null || peer.PeerType is not (PeerType.Channel or PeerType.Chat))
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
            return null!;
        }

        await accessHashHelper2.CheckAccessHashAsync(input, obj.Peer);

        // Everybody can join as themselves; an admin who can manage calls can also join anonymously as
        // the chat itself. A plain member is offered only their own account, as on Telegram.
        var peers = new TVector<IPeer> { new TPeerUser { UserId = input.UserId } };
        var channelIds = new List<long>();
        if (peer.PeerType == PeerType.Channel &&
            await channelAdminRightsChecker.HasChatAdminRightAsync(peer.PeerId, input.UserId, rights => rights.ManageCall))
        {
            peers.Add(new TPeerChannel { ChannelId = peer.PeerId });
            channelIds.Add(peer.PeerId);
        }

        var (users, chats) = await GroupCallStateHelper.LoadUsersAndChatsAsync(
            input,
            userConverterService,
            chatConverterService,
            [input.UserId],
            channelIds);

        return new MyTelegram.Schema.Phone.TJoinAsPeers
        {
            Peers = peers,
            Chats = chats,
            Users = users
        };
    }
}
