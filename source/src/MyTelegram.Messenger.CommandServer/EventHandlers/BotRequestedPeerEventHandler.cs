using MyTelegram.Services.Extensions;
using MyTelegram.Messenger.Services.Bots;
using MyTelegram.Messenger.Services.Premium;

namespace MyTelegram.Messenger.CommandServer.EventHandlers;

/// <summary>
/// Posts the <c>messageActionRequestedPeer</c> a user's chat choice leaves in the bot chat, replying to
/// the message with the button, and lets @PremiumBot answer it.
/// </summary>
public class BotRequestedPeerEventHandler(IMessageAppService messageAppService, IPremiumBotService premiumBotService)
    : IEventHandler<BotRequestedPeerEvent>, ITransientDependency
{
    public async Task HandleEventAsync(BotRequestedPeerEvent eventData)
    {
        var input = new RequestInput(string.Empty, ConnectionType.UnKnown, Guid.NewGuid(), 0, 0, 0, eventData.UserId,
            eventData.PermAuthKeyId, eventData.PermAuthKeyId, eventData.Layer, DateTime.UtcNow.ToTimestamp(),
            DeviceType.Unknown, string.Empty, 0, 0);

        var peers = new TVector<IPeer>(eventData.Peers.Select(peer => (IPeer)(peer.PeerType switch
        {
            PeerType.Chat => new TPeerChat { ChatId = peer.PeerId },
            PeerType.Channel => new TPeerChannel { ChannelId = peer.PeerId },
            _ => new TPeerUser { UserId = peer.PeerId }
        })));

        await messageAppService.SendMessageAsync([new SendMessageInput(
            input.ToRequestInfo() with { ReqMsgId = 0 },
            eventData.UserId,
            new Peer(PeerType.User, eventData.BotUserId),
            string.Empty,
            Random.Shared.NextInt64(),
            inputReplyTo: new TInputReplyToMessage { ReplyToMsgId = eventData.MsgId },
            sendMessageType: SendMessageType.MessageService,
            messageType: MessageType.Text,
            messageAction: new TMessageActionRequestedPeer
            {
                ButtonId = eventData.ButtonId,
                Peers = peers
            })]);

        if (eventData.BotUserId == PremiumBotService.BotUserId)
        {
            await premiumBotService.HandleRequestedPeerAsync(input, eventData.ButtonId, eventData.Peers);
        }
    }
}
