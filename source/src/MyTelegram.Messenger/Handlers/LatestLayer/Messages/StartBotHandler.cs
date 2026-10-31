using MyTelegram.Messenger.Services.Premium;
using MyTelegram.Messenger.Services.Bots;
namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Start a conversation with a bot using a <a href="https://corefork.telegram.org/api/links#bot-links">deep linking parameter</a>
/// Possible errors
/// Code Type Description
/// 400 BOT_INVALID This is not a valid bot.
/// 400 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// 403 CHAT_WRITE_FORBIDDEN You can't write in this chat.
/// 400 INPUT_USER_DEACTIVATED The specified user was deleted.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 500 RANDOM_ID_DUPLICATE You provided a random ID that was already used.
/// 400 START_PARAM_EMPTY The start parameter is empty.
/// 400 START_PARAM_INVALID Start parameter invalid.
/// 400 START_PARAM_TOO_LONG Start parameter is too long.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.startBot"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class StartBotHandler(
    IQueryProcessor queryProcessor,
    IMessageAppService messageAppService,
    IPeerHelper peerHelper,
    IBotFatherBotService botFatherBotService,
    IPremiumBotService premiumBotService) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestStartBot, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestStartBot obj)
    {
        // Validate start_param
        if (string.IsNullOrEmpty(obj.StartParam))
            RpcErrors.RpcErrors400.StartParamEmpty.ThrowRpcError();

        if (obj.StartParam.Length > 64)
            RpcErrors.RpcErrors400.StartParamTooLong.ThrowRpcError();

        // Validate start_param format (a-z, A-Z, 0-9, _, -)
        if (!System.Text.RegularExpressions.Regex.IsMatch(obj.StartParam, @"^[a-zA-Z0-9_-]+$"))
            RpcErrors.RpcErrors400.StartParamInvalid.ThrowRpcError();

        // Validate bot

        long botUserId = obj.Bot switch
        {
            TInputUser inputUser => inputUser.UserId,
            TInputUserSelf => input.UserId,
            _ => 0
        };

        if (botUserId == 0)
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();

        var botUser = await queryProcessor.ProcessAsync(new GetUserByIdQuery(botUserId));
        if (botUser == null)
            RpcErrors.RpcErrors400.InputUserDeactivated.ThrowRpcError();

        if (!botUser.Bot)
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();

        // Validate peer
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);

        // Build /start command message
        var messageText = $"/start {obj.StartParam}";

        // Send message to bot (regular text message, not service message)
        var sendInput = new SendMessageInput(
            input.ToRequestInfo(),
            input.UserId,
            peer,
            messageText,
            obj.RandomId,
            sendMessageType: SendMessageType.Text,
            messageType: MessageType.Text,
            clearDraft: true
        );

        await messageAppService.SendMessageAsync([sendInput]);

        // Built-in bots have no session to receive the /start message through; they are answered
        // in-process, the same way sendMessage hands them plain text. Clients open @PremiumBot with
        // startBot and a start parameter. See https://corefork.telegram.org/api/premium
        if (peer.PeerType == PeerType.User && peer.PeerId == botUserId)
        {
            if (botUserId == PremiumBotService.BotUserId)
                _ = Task.Run(() => premiumBotService.HandleMessageAsync(input, messageText));
            else if (botUserId == BotFatherBotService.BotUserId)
                _ = Task.Run(() => botFatherBotService.HandleMessageAsync(input, input.UserId, messageText));
        }

        // Return empty Updates (message will arrive via push)
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
