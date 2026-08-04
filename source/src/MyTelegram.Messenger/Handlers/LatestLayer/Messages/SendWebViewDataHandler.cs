namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Used by the user to relay data from an opened <a href="https://corefork.telegram.org/api/bots/webapps">reply keyboard bot mini app</a> to the bot that owns it.
/// Possible errors
/// Code Type Description
/// 400 BOT_INVALID This is not a valid bot.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.sendWebViewData"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SendWebViewDataHandler(
    IQueryProcessor queryProcessor,
    IAccessHashHelper accessHashHelper,
    IMessageAppService messageAppService) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestSendWebViewData, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestSendWebViewData obj)
    {
        if (obj.Bot is not TInputUser inputBot)
        {
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
            return null!;
        }

        await accessHashHelper.CheckAccessHashAsync(input, inputBot.UserId, inputBot.AccessHash);

        var botReadModel = await queryProcessor.ProcessAsync(new GetUserByIdQuery(inputBot.UserId));
        if (botReadModel == null || !botReadModel.Bot)
        {
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
        }

        // The bot receives the payload as messageActionWebViewDataSentMe; the user sees only
        // messageActionWebViewDataSent, without the data. Both live in the user/bot chat, so a
        // single service message from the user to the bot carries the data-bearing variant.
        var sendInput = new SendMessageInput(
            input.ToRequestInfo() with { ReqMsgId = 0 },
            input.UserId,
            new Peer(PeerType.User, inputBot.UserId),
            string.Empty,
            obj.RandomId,
            sendMessageType: SendMessageType.MessageService,
            messageType: MessageType.Text,
            messageAction: new TMessageActionWebViewDataSentMe
            {
                Text = obj.ButtonText,
                Data = obj.Data
            });

        await messageAppService.SendMessageAsync([sendInput]);

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
