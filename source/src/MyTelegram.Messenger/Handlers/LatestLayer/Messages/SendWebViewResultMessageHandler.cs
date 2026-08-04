using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Bots;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Terminate webview interaction started with <a href="https://corefork.telegram.org/method/messages.requestWebView">messages.requestWebView</a>, sending the specified message to the chat on behalf of the user.
/// Possible errors
/// Code Type Description
/// 400 QUERY_ID_INVALID The query ID is invalid.
/// 400 USER_BOT_REQUIRED This method can only be called by a bot.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.sendWebViewResultMessage"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✖] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class SendWebViewResultMessageHandler(
    IMongoDatabase mongoDatabase,
    IQueryProcessor queryProcessor,
    IMessageAppService messageAppService) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestSendWebViewResultMessage, MyTelegram.Schema.IWebViewMessageSent>
{
    private const string SessionCollection = "web_view_sessions";

    protected override async Task<MyTelegram.Schema.IWebViewMessageSent> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestSendWebViewResultMessage obj)
    {
        var botReadModel = await queryProcessor.ProcessAsync(new GetUserByIdQuery(input.UserId));
        if (botReadModel == null || !botReadModel.Bot)
        {
            RpcErrors.RpcErrors400.UserBotRequired.ThrowRpcError();
        }

        if (!long.TryParse(obj.BotQueryId, out var queryId))
        {
            RpcErrors.RpcErrors400.QueryIdInvalid.ThrowRpcError();
        }

        var collection = mongoDatabase.GetCollection<BsonDocument>(SessionCollection);
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("query_id", queryId),
            Builders<BsonDocument>.Filter.Eq("bot_id", input.UserId));

        var session = await collection.Find(filter).FirstOrDefaultAsync();
        if (session == null)
        {
            RpcErrors.RpcErrors400.QueryIdInvalid.ThrowRpcError();
        }

        var userId = GetInt64(session!, "user_id");
        var peer = new Peer((PeerType)GetInt64(session!, "peer_type"), GetInt64(session!, "peer_id"));
        var sendMessage = InlineResultConverter.ToBotInlineMessage(GetSendMessage(obj.Result));

        // The message is posted on behalf of the user who opened the webview, into the chat the
        // session was opened for.
        var sendInput = new SendMessageInput(
            input.ToRequestInfo() with { ReqMsgId = 0, UserId = userId },
            userId,
            peer,
            InlineResultConverter.GetMessageText(sendMessage),
            Random.Shared.NextInt64(),
            entities: InlineResultConverter.GetMessageEntities(sendMessage),
            replyMarkup: InlineResultConverter.GetReplyMarkup(sendMessage),
            sendMessageType: SendMessageType.Text,
            messageType: MessageType.Text);

        await messageAppService.SendMessageAsync([sendInput]);

        // The interaction is over, so the session is done regardless of what the client does next.
        await collection.DeleteOneAsync(filter);

        // msg_id would identify the sent message for later inline edits; the send pipeline is
        // asynchronous and does not hand back an id, so it is left unset.
        return new TWebViewMessageSent();
    }

    private static IInputBotInlineMessage GetSendMessage(IInputBotInlineResult result)
    {
        return result switch
        {
            TInputBotInlineResult r => r.SendMessage,
            TInputBotInlineResultPhoto r => r.SendMessage,
            TInputBotInlineResultDocument r => r.SendMessage,
            TInputBotInlineResultGame r => r.SendMessage,
            _ => new TInputBotInlineMessageText { Message = string.Empty }
        };
    }

    private static long GetInt64(BsonDocument doc, string name)
    {
        if (!doc.TryGetValue(name, out var value))
        {
            return 0;
        }

        return value.BsonType switch
        {
            BsonType.Int32 => value.AsInt32,
            BsonType.Int64 => value.AsInt64,
            BsonType.Double => (long)value.AsDouble,
            _ => 0
        };
    }
}
