using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Bots;
using MyTelegram.Messenger.Services.Phone;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Press an inline callback button and get a callback answer from the bot.
/// See https://corefork.telegram.org/method/messages.getBotCallbackAnswer
/// </summary>
internal sealed class GetBotCallbackAnswerHandler(
    IBotFatherBotService botFatherBotService,
    IPeerHelper peerHelper,
    IMongoDatabase database,
    IMessageAppService messageAppService,
    IQueryProcessor queryProcessor,
    IBotUpdatesSender botUpdatesSender,
    ILogger<GetBotCallbackAnswerHandler> logger) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetBotCallbackAnswer, MyTelegram.Schema.Messages.IBotCallbackAnswer>
{
    private const string PendingCollection = "pending_callback_queries";

    protected override async Task<MyTelegram.Schema.Messages.IBotCallbackAnswer> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetBotCallbackAnswer obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);

        if (obj.Data.HasValue)
        {
            var data = System.Text.Encoding.UTF8.GetString(obj.Data.Value.Span);

            // Handle channel transfer rejection
            if (data.StartsWith("reject_channel_transfer:"))
            {
                await HandleRejectChannelTransferAsync(input, data);
                return new MyTelegram.Schema.Messages.TBotCallbackAnswer
                {
                    CacheTime = 0,
                    Message = "Channel transfer rejected successfully"
                };
            }

            // Handle BotFather bot callbacks
            if (peer.PeerId == BotFatherBotService.BotUserId)
            {
                _ = Task.Run(() => botFatherBotService.HandleCallbackAsync(input, input.UserId, obj.MsgId, data));
                return new MyTelegram.Schema.Messages.TBotCallbackAnswer { CacheTime = 0 };
            }
        }

        var botId = await ResolveBotIdAsync(peer, input.UserId, obj.MsgId);
        if (botId == null)
        {
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
        }

        return await ForwardCallbackToBotAsync(input, obj, peer, botId!.Value);
    }

    /// <summary>
    /// Pushes updateBotCallbackQuery (or the inline variant) to the bot and waits for its
    /// messages.setBotCallbackAnswer reply.
    /// </summary>
    private async Task<MyTelegram.Schema.Messages.IBotCallbackAnswer> ForwardCallbackToBotAsync(
        IRequestInput input,
        MyTelegram.Schema.Messages.RequestGetBotCallbackAnswer obj,
        Peer peer,
        long botId)
    {
        var queryId = Random.Shared.NextInt64();

        // chat_instance identifies the chat the button lives in; it must stay stable per chat+user
        // so bots can group presses coming from the same conversation.
        var chatInstance = HashCode.Combine(peer.PeerId, input.UserId);

        var update = new TUpdateBotCallbackQuery
        {
            QueryId = queryId,
            UserId = input.UserId,
            Peer = GroupCallStateHelper.ToPeer(peer.PeerType, peer.PeerId),
            MsgId = obj.MsgId,
            ChatInstance = chatInstance,
            Data = obj.Data
        };

        var extraFields = new BsonDocument
        {
            ["user_id"] = input.UserId,
            ["peer_id"] = peer.PeerId,
            ["msg_id"] = obj.MsgId,
            ["alert"] = false,
            ["message"] = string.Empty,
            ["url"] = string.Empty,
            ["cache_time"] = 0
        };

        var result = await botUpdatesSender.SendQueryAndWaitAsync(
            PendingCollection, queryId, botId, update, extraFields);

        if (!result.Success || result.Document == null)
        {
            logger.LogWarning("Bot did not answer callback query: botId={BotId} queryId={QueryId}", botId, queryId);
            throw new RpcException(new RpcError(400,
                result.Error ?? RpcErrors.RpcErrors400.BotResponseTimeout.Message));
        }

        var doc = result.Document;
        var message = GetString(doc, "message");
        var url = GetString(doc, "url");

        return new MyTelegram.Schema.Messages.TBotCallbackAnswer
        {
            Alert = doc.TryGetValue("alert", out var alertValue) && alertValue.IsBoolean && alertValue.AsBoolean,
            Message = string.IsNullOrEmpty(message) ? null : message,
            Url = string.IsNullOrEmpty(url) ? null : url,
            HasUrl = !string.IsNullOrEmpty(url),
            CacheTime = GetInt32(doc, "cache_time")
        };
    }

    /// <summary>
    /// Determines which bot owns the message carrying the pressed button. In a private chat with a
    /// bot that is the peer itself; elsewhere it is the message sender.
    /// </summary>
    private async Task<long?> ResolveBotIdAsync(Peer peer, long userId, int msgId)
    {
        if (peer.PeerType == PeerType.User)
        {
            var peerUser = await queryProcessor.ProcessAsync(new GetUserByIdQuery(peer.PeerId));
            if (peerUser is { Bot: true })
            {
                return peer.PeerId;
            }
        }

        var ownerPeerId = peer.PeerType == PeerType.Channel ? peer.PeerId : userId;
        var messageReadModel =
            await queryProcessor.ProcessAsync(new GetMessageByIdQuery(MessageId.Create(ownerPeerId, msgId).Value));

        if (messageReadModel == null)
        {
            return null;
        }

        var sender = await queryProcessor.ProcessAsync(new GetUserByIdQuery(messageReadModel.SenderUserId));
        return sender is { Bot: true } ? messageReadModel.SenderUserId : null;
    }

    private static string GetString(BsonDocument doc, string name)
    {
        return doc.TryGetValue(name, out var value) && value.IsString ? value.AsString : string.Empty;
    }

    private static int GetInt32(BsonDocument doc, string name)
    {
        if (!doc.TryGetValue(name, out var value))
        {
            return 0;
        }

        return value.BsonType switch
        {
            BsonType.Int32 => value.AsInt32,
            BsonType.Int64 => (int)value.AsInt64,
            BsonType.Double => (int)value.AsDouble,
            _ => 0
        };
    }

    private async Task HandleRejectChannelTransferAsync(IRequestInput input, string callbackData)
    {
        // Parse callback data: reject_channel_transfer:{channelId}:{fromUserId}
        var parts = callbackData.Split(':');
        if (parts.Length != 3)
            return;

        if (!long.TryParse(parts[1], out var channelId))
            return;

        if (!long.TryParse(parts[2], out var fromUserId))
            return;

        // Find pending transfer
        var transfersCol = database.GetCollection<BsonDocument>("channel_pending_transfers");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("ChannelId", channelId),
            Builders<BsonDocument>.Filter.Eq("ToUserId", input.UserId),
            Builders<BsonDocument>.Filter.Eq("FromUserId", fromUserId)
        );

        var transfer = await transfersCol.Find(filter).FirstOrDefaultAsync();
        if (transfer == null)
            return;

        // Delete pending transfer
        await transfersCol.DeleteOneAsync(filter);

        // Get channel info
        var channelCol = database.GetCollection<BsonDocument>("eventflow-channelreadmodel");
        var channel = await channelCol.Find(Builders<BsonDocument>.Filter.Eq("ChannelId", channelId)).FirstOrDefaultAsync();
        if (channel == null)
            return;

        var channelTitle = channel["Title"].AsString;

        // Get new owner name for notification
        var userCol = database.GetCollection<BsonDocument>("eventflow-userreadmodel");
        var newOwner = await userCol.Find(Builders<BsonDocument>.Filter.Eq("UserId", input.UserId)).FirstOrDefaultAsync();
        var newOwnerName = "User";
        if (newOwner != null)
        {
            newOwnerName = newOwner.Contains("FirstName") ? newOwner["FirstName"].AsString : "User";
            if (newOwner.Contains("LastName") && !newOwner["LastName"].IsBsonNull)
            {
                newOwnerName += " " + newOwner["LastName"].AsString;
            }
        }

        // Send notification to old owner
        var messageText = $"⚠️ Channel Transfer Rejected: {channelTitle}\n\n" +
                         $"You recently transferred ownership of this channel to {newOwnerName}. " +
                         $"The user has rejected the transfer, so the channel has been assigned back to you.";

        var sendInput = new SendMessageInput(
            new RequestInfo(
                ConnectionId: string.Empty,
                SessionId: 0,
                ReqMsgId: 0,
                UserId: 777000, // Telegram service bot
                AccessHashKeyId: 0,
                AuthKeyId: 0,
                PermAuthKeyId: 0,
                RequestId: Guid.NewGuid(),
                Layer: 222,
                Date: DateTime.UtcNow.ToTimestamp(),
                DeviceType: DeviceType.Android
            ),
            777000,
            new Peer(PeerType.User, fromUserId),
            messageText,
            Random.Shared.NextInt64(),
            sendMessageType: SendMessageType.Text,
            messageType: MessageType.Text
        );

        await messageAppService.SendMessageAsync([sendInput]);

        logger.LogInformation(
            "Channel transfer rejected: channelId={ChannelId} fromUserId={FromUserId} toUserId={ToUserId}",
            channelId, fromUserId, input.UserId);
    }
}
