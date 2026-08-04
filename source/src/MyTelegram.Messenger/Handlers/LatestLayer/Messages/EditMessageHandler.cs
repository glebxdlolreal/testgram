using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Edit message
/// Possible errors
/// Code Type Description
/// 400 BOT_DOMAIN_INVALID Bot domain invalid.
/// 400 BOT_INVALID This is not a valid bot.
/// 400 BUSINESS_CONNECTION_INVALID The <code>connection_id</code> passed to the wrapping <a href="https://corefork.telegram.org/api/business">invokeWithBusinessConnection</a> call is invalid.
/// 400 BUSINESS_PEER_INVALID Messages can't be set to the specified peer through the current <a href="https://corefork.telegram.org/api/business#connected-bots">business connection</a>.
/// 400 BUTTON_COPY_TEXT_INVALID The specified <a href="https://corefork.telegram.org/constructor/keyboardButtonCopy">keyboardButtonCopy</a>.<code>copy_text</code> is invalid.
/// 400 BUTTON_DATA_INVALID The data of one or more of the buttons you provided is invalid.
/// 400 BUTTON_TYPE_INVALID The type of one or more of the buttons you provided is invalid.
/// 400 BUTTON_URL_INVALID Button URL invalid.
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 406 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 403 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// 400 CHAT_FORWARDS_RESTRICTED You can't forward messages from a protected chat.
/// 403 CHAT_SEND_GIFS_FORBIDDEN You can't send gifs in this chat.
/// 403 CHAT_WRITE_FORBIDDEN You can't write in this chat.
/// 400 DOCUMENT_INVALID The specified document is invalid.
/// 400 ENTITIES_TOO_LONG You provided too many styled message entities.
/// 400 ENTITY_BOUNDS_INVALID A specified <a href="https://corefork.telegram.org/api/entities#entity-length">entity offset or length</a> is invalid, see <a href="https://corefork.telegram.org/api/entities#entity-length">here »</a> for info on how to properly compute the entity offset/length.
/// 400 FILE_PARTS_INVALID The number of file parts is invalid.
/// 400 IMAGE_PROCESS_FAILED Failure while processing image.
/// 403 INLINE_BOT_REQUIRED Only the inline bot can edit message.
/// 400 INPUT_USER_DEACTIVATED The specified user was deleted.
/// 400 MEDIA_CAPTION_TOO_LONG The caption is too long.
/// 400 MEDIA_EMPTY The provided media object is invalid.
/// 400 MEDIA_GROUPED_INVALID You tried to send media of different types in an album.
/// 400 MEDIA_INVALID Media invalid.
/// 400 MEDIA_NEW_INVALID The new media is invalid.
/// 400 MEDIA_PREV_INVALID Previous media invalid.
/// 400 MEDIA_TTL_INVALID The specified media TTL is invalid.
/// 403 MESSAGE_AUTHOR_REQUIRED Message author required.
/// 400 MESSAGE_EDIT_TIME_EXPIRED You can't edit this message anymore, too much time has passed since its creation.
/// 400 MESSAGE_EMPTY The provided message is empty.
/// 400 MESSAGE_ID_INVALID The provided message id is invalid.
/// 400 MESSAGE_NOT_MODIFIED The provided message data is identical to the previous message data, the message wasn't modified.
/// 400 MESSAGE_TOO_LONG The provided message is too long.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// 500 MSG_WAIT_FAILED A waiting call returned an error.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 PEER_TYPES_INVALID The passed <a href="https://corefork.telegram.org/constructor/keyboardButtonSwitchInline">keyboardButtonSwitchInline</a>.<code>peer_types</code> field is invalid.
/// 400 PHOTO_EXT_INVALID The extension of the photo is invalid.
/// 400 PHOTO_INVALID_DIMENSIONS The photo dimensions are invalid.
/// 400 PHOTO_SAVE_FILE_INVALID Internal issues, try again later.
/// 400 REPLY_MARKUP_INVALID The provided reply markup is invalid.
/// 400 REPLY_MARKUP_TOO_LONG The specified reply_markup is too long.
/// 400 SCHEDULE_DATE_INVALID Invalid schedule date provided.
/// 400 TODO_ITEMS_EMPTY A checklist was specified, but no <a href="https://corefork.telegram.org/api/todo">checklist items</a> were passed.
/// 400 TODO_ITEM_DUPLICATE Duplicate <a href="https://corefork.telegram.org/api/todo">checklist items</a> detected.
/// 400 USER_BANNED_IN_CHANNEL You're banned from sending messages in supergroups/channels.
/// 400 WEBPAGE_NOT_FOUND A preview for the specified webpage <code>url</code> could not be generated.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.editMessage"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class EditMessageHandler(IMediaHelper mediaHelper, ICommandBus commandBus, IPeerHelper peerHelper, IMessageAppService messageAppService, IDataEncryptionHelper dataEncryptionHelper,
    IChannelAdminRightsChecker channelAdminRightsChecker,
    IOptionsMonitor<MyTelegramMessengerServerOptions> options, IQueryProcessor queryProcessor, IMongoDatabase mongoDatabase, IObjectMessageSender objectMessageSender, IMessageConverterService messageConverterService) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestEditMessage, MyTelegram.Schema.IUpdates>
{
    private static byte[]? _encryptionKey;
    private static KeyConfig? _keyConfig;
    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestEditMessage obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        var ownerPeerId = input.UserId;
        if (peer.PeerType == PeerType.Channel)
        {
            ownerPeerId = peer.PeerId;
        }

        IMessageMedia? media = null;
        if (obj.Media != null)
        {
            if (obj.Media is TInputMediaEmpty)
            {
                media = new TMessageMediaEmpty();
            }
            else if (obj.Media is TInputMediaPoll)
            {
                // Handled after the message is loaded: closing a poll is a domain
                // operation on the poll aggregate, not a media replacement. Leaving media
                // null keeps EditOutboxMessageCommand from overwriting the stored media —
                // renderers always read the live poll state from the read model anyway.
            }
            else
            {
                media = await mediaHelper.SaveMediaAsync(obj.Media);
            }
        }

        var messageReadModel = await queryProcessor.ProcessAsync(new GetMessageByIdQuery(MessageId.Create(ownerPeerId, obj.Id).Value));
        if (messageReadModel == null)
        {
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
        }

        if (messageReadModel.SenderUserId != input.UserId)
        {
            switch (messageReadModel.ToPeerType)
            {
                case PeerType.Channel:
                    await channelAdminRightsChecker.CheckAdminRightAsync(messageReadModel.ToPeerId, input.UserId,
                        p => p.EditMessages);
                    break;
                default:
                    RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
                    break;
            }
        }

        // Clients stop a poll by editing the message with poll.closed = true; there is no
        // dedicated messages.closePoll method. Authorization was handled just above: either
        // the sender edits their own message, or a channel admin with EditMessages does.
        if (obj.Media is TInputMediaPoll editPoll)
        {
            if (messageReadModel!.PollId == null)
            {
                RpcErrors.RpcErrors400.MediaInvalid.ThrowRpcError();
            }

            if (editPoll.Poll.Closed)
            {
                await commandBus.PublishAsync(
                    new ClosePollCommand(PollId.Create(messageReadModel.PollId!.Value), CurrentDate));

                if (peer.PeerType == PeerType.Channel)
                {
                    await CreateStopPollAdminLogAsync(input, peer, messageReadModel);
                }
            }
        }

        var message = messageReadModel.Message;
        var messageTextEdited = false;
        if (!string.IsNullOrEmpty(obj.Message))
        {
            message = obj.Message;
            messageTextEdited = true;
        }

        byte[]? encryptedData = null;
        byte[]? inboxMessageEncryptedData = null;
        if (messageTextEdited && options.CurrentValue.EncryptionConfig is { Enabled: true, MessageKeys.Count: > 0 })
        {
            _keyConfig ??= options.CurrentValue.EncryptionConfig.MessageKeys[0];
            _encryptionKey ??= Encoding.UTF8.GetBytes(_keyConfig.Key);
            encryptedData = dataEncryptionHelper.Encrypt(_keyConfig.Id, _encryptionKey, ownerPeerId, message);
            if (messageReadModel.ToPeerType == PeerType.User && messageReadModel.ToPeerId != input.UserId)
            {
                inboxMessageEncryptedData = dataEncryptionHelper.Encrypt(_keyConfig.Id, _encryptionKey, messageReadModel.ToPeerId, message);
            }

            message = string.Empty;
        }

        //InboxItem? inboxItem = null;
        //if (messageReadModel!.ToPeerType == PeerType.User)
        //{
        //    var inboxMessageReadModel =
        //        await queryProcessor.ProcessAsync(new GetMessageByBatchIdQuery(messageReadModel.BatchId,
        //            messageReadModel.OwnerPeerId));
        //    if (inboxMessageReadModel != null)
        //    {
        //        inboxItem = new InboxItem(inboxMessageReadModel.OwnerPeerId, inboxMessageReadModel.MessageId);
        //    }
        //}
        var entities = obj.Entities ?? [];
        await messageAppService.ProcessMessageEntitiesAsync(obj.Message, entities, peer);
        if (entities.Count == 0)
        {
            entities = null;
        }

        var hashtags = messageAppService.GetHashtags(obj.Message);
        var command = new EditOutboxMessageCommand(MessageId.Create(ownerPeerId, obj.Id, obj.QuickReplyShortcutId.HasValue), input.ToRequestInfo(), obj.Id, message, CurrentDate, entities, media, obj.ReplyMarkup, obj.InvertMedia, hashtags, encryptedData, inboxMessageEncryptedData);
        await commandBus.PublishAsync(command);

        // Create admin log for channel message edit
        if (peer.PeerType == PeerType.Channel)
        {
            await CreateEditMessageAdminLogAsync(input, peer, messageReadModel, obj.Message ?? message, encryptedData);
        }

        // Notify connected business bots about edited message
        await NotifyBusinessBotsEditAsync(input.UserId, peer.PeerId, obj.Id, obj.Message ?? message);

        return null !;
    }

    private async Task NotifyBusinessBotsEditAsync(long userId, long peerId, int messageId, string newText)
    {
        var collection = mongoDatabase.GetCollection<BsonDocument>("connected_business_bots");
        var filter = Builders<BsonDocument>.Filter.Eq("UserId", userId);
        var connections = await collection.Find(filter).ToListAsync();

        foreach (var conn in connections)
        {
            var botId = conn["BotId"].AsInt64;
            var connectionId = conn["ConnectionId"].AsString;

            // Check if chat is paused
            var pausedCollection = mongoDatabase.GetCollection<BsonDocument>("paused_business_bot_chats");
            var pausedFilter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("UserId", userId),
                Builders<BsonDocument>.Filter.Eq("PeerId", peerId)
            );
            var isPaused = await pausedCollection.Find(pausedFilter).AnyAsync();
            if (isPaused) continue;

            // Check if peer is in ExcludeUsers
            var recipientsDoc = conn["Recipients"].AsBsonDocument;
            if (recipientsDoc.Contains("ExcludeUsers"))
            {
                var excludeArray = recipientsDoc["ExcludeUsers"].AsBsonArray;
                if (excludeArray.Any(u => u.AsInt64 == peerId)) continue;
            }

            // Check if bot has ReadMessages right
            var rightsDoc = conn["Rights"].AsBsonDocument;
            if (!rightsDoc.Contains("ReadMessages") || !rightsDoc["ReadMessages"].AsBoolean)
                continue;

            // Get Qts
            var countersCollection = mongoDatabase.GetCollection<BsonDocument>("counters");
            var qtsFilter = Builders<BsonDocument>.Filter.Eq("_id", $"qts_{botId}");
            var qtsUpdate = Builders<BsonDocument>.Update.Inc("seq", 1);
            var qtsOptions = new FindOneAndUpdateOptions<BsonDocument>
            {
                IsUpsert = true,
                ReturnDocument = ReturnDocument.After
            };
            var qtsResult = await countersCollection.FindOneAndUpdateAsync(qtsFilter, qtsUpdate, qtsOptions);
            var qts = qtsResult["seq"].AsInt32;

            // Create updateBotEditBusinessMessage
            var updateBotEditBusinessMessage = new TUpdateBotEditBusinessMessage
            {
                ConnectionId = connectionId,
                Message = new TMessage
                {
                    Id = messageId,
                    FromId = new TPeerUser { UserId = userId },
                    PeerId = new TPeerUser { UserId = peerId },
                    Message = newText,
                    Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Out = false
                },
                Qts = qts,
                ReplyToMessage = null
            };

            var botUpdates = new TUpdates
            {
                Updates = new TVector<IUpdate> { updateBotEditBusinessMessage },
                Users = new TVector<IUser>(),
                Chats = new TVector<IChat>(),
                Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            await objectMessageSender.PushMessageToPeerAsync(
                new Peer(PeerType.User, botId),
                botUpdates,
                pts: 0
            );
        }
    }

    private async Task CreateEditMessageAdminLogAsync(IRequestInput input, Peer peer, IMessageReadModel oldMessage, string newMessageText, byte[]? newEncryptedData)
    {
        var adminLogCol = mongoDatabase.GetCollection<BsonDocument>("channel_admin_log");

        // Decrypt old message text if needed
        var oldMessageText = oldMessage.Message ?? string.Empty;
        if (string.IsNullOrEmpty(oldMessageText) && oldMessage.EncryptedData.HasValue && oldMessage.EncryptedData.Value.Length > 0)
        {
            oldMessageText = messageConverterService.DecryptMessage(peer.PeerId, oldMessage.MessageId, oldMessage.EncryptedData.Value);
        }

        // Decrypt new message text if needed
        if (string.IsNullOrEmpty(newMessageText) && newEncryptedData != null && newEncryptedData.Length > 0)
        {
            newMessageText = messageConverterService.DecryptMessage(peer.PeerId, oldMessage.MessageId, newEncryptedData);
        }

        // Build old message
        var prevMessage = new TMessage
        {
            Id = oldMessage.MessageId,
            PeerId = new TPeerChannel { ChannelId = peer.PeerId },
            Message = oldMessageText,
            Date = oldMessage.Date,
            Out = oldMessage.Out,
            Post = oldMessage.Post,
            Media = new TMessageMediaEmpty(),
            ReplyTo = null,
            Entities = new TVector<IMessageEntity>()
        };

        if (oldMessage.SenderUserId > 0)
            prevMessage.FromId = new TPeerUser { UserId = oldMessage.SenderUserId };
        if (oldMessage.Views.HasValue && oldMessage.Views.Value > 0)
            prevMessage.Views = oldMessage.Views.Value;
        if (oldMessage.EditDate.HasValue && oldMessage.EditDate.Value > 0)
            prevMessage.EditDate = oldMessage.EditDate.Value;
        if (!string.IsNullOrEmpty(oldMessage.PostAuthor))
            prevMessage.PostAuthor = oldMessage.PostAuthor;

        // Build new message (copy from old, update text and edit date)
        var newMessage = new TMessage
        {
            Id = oldMessage.MessageId,
            PeerId = new TPeerChannel { ChannelId = peer.PeerId },
            Message = newMessageText,
            Date = oldMessage.Date,
            Out = oldMessage.Out,
            Post = oldMessage.Post,
            Media = new TMessageMediaEmpty(),
            ReplyTo = null,
            Entities = new TVector<IMessageEntity>(),
            EditDate = CurrentDate
        };

        if (oldMessage.SenderUserId > 0)
            newMessage.FromId = new TPeerUser { UserId = oldMessage.SenderUserId };
        if (oldMessage.Views.HasValue && oldMessage.Views.Value > 0)
            newMessage.Views = oldMessage.Views.Value;
        if (!string.IsNullOrEmpty(oldMessage.PostAuthor))
            newMessage.PostAuthor = oldMessage.PostAuthor;

        // Create admin log action
        var action = new TChannelAdminLogEventActionEditMessage
        {
            PrevMessage = prevMessage,
            NewMessage = newMessage
        };

        // Serialize action
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        action.Serialize(buffer);
        var actionData = buffer.WrittenSpan.ToArray();

        var eventId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var logEntry = new BsonDocument
        {
            ["_id"] = $"adminlog-{peer.PeerId}-{eventId}",
            ["channel_id"] = peer.PeerId,
            ["event_id"] = eventId,
            ["user_id"] = input.UserId,
            ["date"] = DateTime.UtcNow,
            ["action"] = new BsonDocument
            {
                ["type"] = "TChannelAdminLogEventActionEditMessage",
                ["data"] = actionData
            }
        };

        await adminLogCol.InsertOneAsync(logEntry);
    }

    /// <summary>
    /// Records a <c>channelAdminLogEventActionStopPoll</c> entry when a poll is stopped in a channel.
    /// </summary>
    private async Task CreateStopPollAdminLogAsync(IRequestInput input, Peer peer, IMessageReadModel pollMessage)
    {
        var adminLogCol = mongoDatabase.GetCollection<BsonDocument>("channel_admin_log");

        var messageText = pollMessage.Message ?? string.Empty;
        if (string.IsNullOrEmpty(messageText) && pollMessage.EncryptedData is { Length: > 0 })
        {
            messageText = messageConverterService.DecryptMessage(peer.PeerId, pollMessage.MessageId,
                pollMessage.EncryptedData.Value);
        }

        var message = new TMessage
        {
            Id = pollMessage.MessageId,
            PeerId = new TPeerChannel { ChannelId = peer.PeerId },
            Message = messageText,
            Date = pollMessage.Date,
            Out = pollMessage.Out,
            Post = pollMessage.Post,
            Media = new TMessageMediaEmpty(),
            ReplyTo = null,
            Entities = new TVector<IMessageEntity>()
        };

        if (pollMessage.SenderUserId > 0)
        {
            message.FromId = new TPeerUser { UserId = pollMessage.SenderUserId };
        }

        if (!string.IsNullOrEmpty(pollMessage.PostAuthor))
        {
            message.PostAuthor = pollMessage.PostAuthor;
        }

        var action = new TChannelAdminLogEventActionStopPoll { Message = message };

        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        action.Serialize(buffer);
        var actionData = buffer.WrittenSpan.ToArray();

        var eventId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await adminLogCol.InsertOneAsync(new BsonDocument
        {
            ["_id"] = $"adminlog-{peer.PeerId}-{eventId}",
            ["channel_id"] = peer.PeerId,
            ["event_id"] = eventId,
            ["user_id"] = input.UserId,
            ["date"] = DateTime.UtcNow,
            ["action"] = new BsonDocument
            {
                ["type"] = "TChannelAdminLogEventActionStopPoll",
                ["data"] = actionData
            }
        });
    }
}