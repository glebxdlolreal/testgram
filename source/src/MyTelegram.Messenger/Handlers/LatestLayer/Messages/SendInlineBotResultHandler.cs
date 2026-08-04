using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Bots;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Send a result obtained using <a href="https://corefork.telegram.org/method/messages.getInlineBotResults">messages.getInlineBotResults</a>.
/// Possible errors
/// Code Type Description
/// 400 INLINE_RESULT_EXPIRED The inline query expired.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 QUERY_ID_EMPTY The query ID is empty.
/// 400 RESULT_ID_EMPTY Result ID empty.
/// 400 RESULT_ID_INVALID One of the specified result IDs is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.sendInlineBotResult"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SendInlineBotResultHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IMessageAppService messageAppService,
    IBotUpdatesSender botUpdatesSender) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestSendInlineBotResult, MyTelegram.Schema.IUpdates>
{
    private const string ResultsCollection = "inline_bot_results";

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestSendInlineBotResult obj)
    {
        if (obj.QueryId == 0)
        {
            RpcErrors.RpcErrors400.QueryIdEmpty.ThrowRpcError();
        }

        if (string.IsNullOrEmpty(obj.Id))
        {
            RpcErrors.RpcErrors400.ResultIdEmpty.ThrowRpcError();
        }

        var stored = await mongoDatabase.GetCollection<BsonDocument>(ResultsCollection)
            .Find(Builders<BsonDocument>.Filter.Eq("query_id", obj.QueryId))
            .FirstOrDefaultAsync();

        if (stored == null)
        {
            RpcErrors.RpcErrors400.InlineResultExpired.ThrowRpcError();
        }

        var expiresAt = GetInt32(stored!, "expires_at");
        if (expiresAt > 0 && expiresAt < DateTime.UtcNow.ToTimestamp())
        {
            await mongoDatabase.GetCollection<BsonDocument>(ResultsCollection)
                .DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("query_id", obj.QueryId));
            RpcErrors.RpcErrors400.InlineResultExpired.ThrowRpcError();
        }

        var inputResults = ReadObject<TVector<IInputBotInlineResult>>(stored!, "input_results");
        var selected = FindResult(inputResults, obj.Id);
        if (selected == null)
        {
            RpcErrors.RpcErrors400.ResultIdInvalid.ThrowRpcError();
        }

        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        var sendMessage = InlineResultConverter.ToBotInlineMessage(GetSendMessage(selected!));

        var sendInput = new SendMessageInput(
            input.ToRequestInfo(),
            input.UserId,
            peer,
            InlineResultConverter.GetMessageText(sendMessage),
            obj.RandomId,
            entities: InlineResultConverter.GetMessageEntities(sendMessage),
            inputReplyTo: obj.ReplyTo,
            clearDraft: obj.ClearDraft,
            replyMarkup: InlineResultConverter.GetReplyMarkup(sendMessage),
            silent: obj.Silent,
            scheduleDate: obj.ScheduleDate,
            sendMessageType: SendMessageType.Text,
            messageType: MessageType.Text);

        await messageAppService.SendMessageAsync([sendInput]);

        var botId = GetInt64(stored!, "bot_id");
        var query = stored!.TryGetValue("query", out var queryValue) && queryValue.IsString
            ? queryValue.AsString
            : string.Empty;

        // Tell the bot which result the user picked, so it can track usage / edit it later.
        await botUpdatesSender.PushUpdateToBotAsync(botId, new TUpdateBotInlineSend
        {
            UserId = input.UserId,
            Query = query,
            Id = obj.Id
        });

        // The message itself reaches the client through the push pipeline.
        return new TUpdates
        {
            Updates = new TVector<IUpdate>(),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = DateTime.UtcNow.ToTimestamp(),
            Seq = 0
        };
    }

    private static IInputBotInlineResult? FindResult(TVector<IInputBotInlineResult>? results, string id)
    {
        if (results == null)
        {
            return null;
        }

        foreach (var result in results)
        {
            var resultId = result switch
            {
                TInputBotInlineResult r => r.Id,
                TInputBotInlineResultPhoto r => r.Id,
                TInputBotInlineResultDocument r => r.Id,
                TInputBotInlineResultGame r => r.Id,
                _ => null
            };

            if (resultId == id)
            {
                return result;
            }
        }

        return null;
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

    private static T? ReadObject<T>(BsonDocument doc, string name) where T : class, IObject
    {
        if (!doc.TryGetValue(name, out var value) || value.BsonType != BsonType.Binary)
        {
            return null;
        }

        var bytes = value.AsBsonBinaryData.Bytes;
        if (bytes.Length == 0)
        {
            return null;
        }

        var buffer = new ReadOnlyMemory<byte>(bytes);
        return buffer.Read<T>();
    }

    private static int GetInt32(BsonDocument doc, string name)
    {
        return (int)GetInt64(doc, name);
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
