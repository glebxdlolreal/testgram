using MyTelegram.Messenger.Services.Premium;

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
    IMessageAppService messageAppService,
    IPremiumBotService premiumBotService) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestSendBotRequestedPeer, MyTelegram.Schema.IUpdates>
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

        var peers = new TVector<IPeer>();
        var chosen = new List<Peer>();
        foreach (var requestedPeer in obj.RequestedPeers)
        {
            await accessHashHelper.CheckAccessHashAsync(input, requestedPeer);
            var peer = peerHelper.GetPeer(requestedPeer, input.UserId);
            IPeer? tlPeer = peer?.PeerType switch
            {
                PeerType.User or PeerType.Self => new TPeerUser { UserId = peer.PeerId },
                PeerType.Chat => new TPeerChat { ChatId = peer.PeerId },
                PeerType.Channel => new TPeerChannel { ChannelId = peer.PeerId },
                _ => null
            };
            if (tlPeer == null)
            {
                RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
            }

            peers.Add(tlPeer!);
            chosen.Add(peer!.PeerType == PeerType.Self ? new Peer(PeerType.User, peer.PeerId) : peer);
        }

        // The choice lands in the chat as a service message replying to the message with the button.
        await messageAppService.SendMessageAsync([new SendMessageInput(
            input.ToRequestInfo() with { ReqMsgId = 0 },
            input.UserId,
            new Peer(PeerType.User, botPeer.PeerId),
            string.Empty,
            Random.Shared.NextInt64(),
            inputReplyTo: new TInputReplyToMessage { ReplyToMsgId = obj.MsgId!.Value },
            sendMessageType: SendMessageType.MessageService,
            messageType: MessageType.Text,
            messageAction: new TMessageActionRequestedPeer
            {
                ButtonId = obj.ButtonId,
                Peers = peers
            })]);

        if (botPeer.PeerId == PremiumBotService.BotUserId)
        {
            _ = Task.Run(() => premiumBotService.HandleRequestedPeerAsync(input, obj.ButtonId, chosen));
        }

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
