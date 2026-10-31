using MyTelegram.Messenger.Services.Bots;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Send one or more chosen peers, as requested by a <a href="https://corefork.telegram.org/constructor/keyboardButtonRequestPeer">keyboardButtonRequestPeer</a> button.
/// Possible errors
/// Code Type Description
/// 400 MESSAGE_ID_INVALID The provided message id is invalid.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.sendBotRequestedPeer"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SendBotRequestedPeerHandler(
    IQueryProcessor queryProcessor,
    IPeerHelper peerHelper,
    IAccessHashHelper2 accessHashHelper,
    IEventBus eventBus) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestSendBotRequestedPeer, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestSendBotRequestedPeer obj)
    {
        await accessHashHelper.CheckAccessHashAsync(input, obj.Peer);
        var botPeer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (botPeer is not { PeerType: PeerType.User })
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        var bot = await queryProcessor.ProcessAsync(new GetUserByIdQuery(botPeer!.PeerId));
        if (bot is not { Bot: true })
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        // Only the keyboard-button form is served: a Mini App's requestChat (webapp_req_id, no msg_id)
        // is answered to the Mini App, which this server does not host.
        if (obj.MsgId is not > 0)
        {
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
        }

        if (obj.RequestedPeers.Count == 0)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        var chosen = new List<Peer>();
        foreach (var requestedPeer in obj.RequestedPeers)
        {
            await accessHashHelper.CheckAccessHashAsync(input, requestedPeer);
            var peer = peerHelper.GetPeer(requestedPeer, input.UserId);
            if (peer?.PeerType is not (PeerType.User or PeerType.Self or PeerType.Chat or PeerType.Channel))
            {
                RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
            }

            chosen.Add(peer!.PeerType == PeerType.Self ? new Peer(PeerType.User, peer.PeerId) : peer);
        }

        // The service message (and an in-process bot's answer) is sent by the command server: this
        // request is answered by the query server, whose message ids would collide.
        await eventBus.PublishAsync(new BotRequestedPeerEvent(input.UserId, input.PermAuthKeyId, input.Layer, botPeer.PeerId,
            obj.MsgId!.Value, obj.ButtonId, chosen));

        // The service message reaches both sides through the push pipeline.
        return new TUpdates
        {
            Updates = new TVector<IUpdate>(),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = DateTime.UtcNow.ToTimestamp(),
            Seq = 0
        };
    }
}
