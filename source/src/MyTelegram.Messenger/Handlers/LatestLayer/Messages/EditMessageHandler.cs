using MyTelegram.Messenger.Services.Premium;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Domain.Shared;
using MyTelegram.Messenger.Helpers;
using MyTelegram.Messenger.Services.Dice;
using MyTelegram.Messenger.Services.GeoLive;
using MyTelegram.Messenger.Services.Scheduled;

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
    IOptionsMonitor<MyTelegramMessengerServerOptions> options, IQueryProcessor queryProcessor, IMongoDatabase mongoDatabase, IObjectMessageSender objectMessageSender, IMessageConverterService messageConverterService,
    IScheduledMessageStore scheduledMessageStore,
    IScheduledMessageDispatcher scheduledMessageDispatcher,
    IGeoProximityAlertService geoProximityAlertService,
    IUserAppService userAppService,
    IMessageEncryptionHelper messageEncryptionHelper, IPremiumLimitGuard premiumLimitGuard,
    MyTelegram.Messenger.Services.Bots.IUrlAuthService urlAuthService) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestEditMessage, MyTelegram.Schema.IUpdates>
{
    private static byte[]? _encryptionKey;
    private static KeyConfig? _keyConfig;
    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestEditMessage obj)
    {
        // The hard ceiling applies to every text; the tighter caption limit is applied further down,
        // once the stored message is known to carry media.
        MessageLengthHelper.ValidateMessage(obj.Message);

        // A bot's inputKeyboardButtonUrlAuth is input-only; clients get keyboardButtonUrlAuth.
        obj.ReplyMarkup = await urlAuthService.NormalizeAsync(input.UserId, obj.ReplyMarkup);

        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        var ownerPeerId = input.UserId;
        if (peer.PeerType == PeerType.Channel)
        {
            ownerPeerId = peer.PeerId;
        }

        IMessageMedia? media = null;
        if (obj.Media != null)
        {
            // A dice cannot be edited into a message: the value is the server's and a second conversion
            // would simply roll a new one. TDLib marks the content non-editable, so no client asks.
            DiceMediaGuard.ThrowIfDice(obj.Media);

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
            else if (obj.Media is TInputMediaTodo)
            {
                // Handled after the message is loaded: the previous completions have to be
                // carried over, so the new media can only be built once the old one is known.
            }
            else if (obj.Media is TInputMediaGeoLive)
            {
                // Handled after the message is loaded: a live-location edit only carries updated
                // coordinates, so the original validity period has to be read from the stored media
                // first. See https://corefork.telegram.org/api/live-location
            }
            else
            {
                // upload_max_fileparts_default / _premium: 2 GB, or 4 GB with Premium.
                // See https://corefork.telegram.org/api/premium#doubled-limits
                await premiumLimitGuard.EnsureUploadPartsAsync(input.UserId, obj.Media);
                media = await mediaHelper.SaveMediaAsync(obj.Media);
            }
        }

        // A quick reply message lives under its own aggregate id and must belong to the given shortcut.
        // See https://corefork.telegram.org/api/business#quick-reply-shortcuts
        var isQuickReply = obj.QuickReplyShortcutId.HasValue;
        var messageReadModel = await queryProcessor.ProcessAsync(new GetMessageByIdQuery(MessageId.Create(ownerPeerId, obj.Id, isQuickReply).Value));
        if (isQuickReply && (messageReadModel == null || messageReadModel.ShortcutId != obj.QuickReplyShortcutId ||
                             peer.PeerId != input.UserId))
        {
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
        }

        if (messageReadModel == null)
        {
            // The id may address the schedule queue of the peer instead of the history: clients edit and
            // reschedule queued messages with this very method.
            // See https://corefork.telegram.org/api/scheduled-messages
            var scheduledUpdates = await TryEditScheduledMessageAsync(input, obj, peer, media);
            if (scheduledUpdates != null)
            {
                return scheduledUpdates;
            }

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

        // Now that the stored message is known, a text attached to media is a caption and is bound by
        // the (tighter) caption limit instead of the 4096 already checked above.
        if (media is not null and not TMessageMediaEmpty ||
            messageReadModel.Media2 is not null and not TMessageMediaEmpty)
        {
            await ValidateEditedCaptionLengthAsync(input.UserId, obj.Message);
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

        // Editing a checklist replaces the item list, but the completion history of the items that
        // survive the edit (same ids) must be preserved — see https://corefork.telegram.org/api/todo.
        if (obj.Media is TInputMediaTodo editTodo)
        {
            if (messageReadModel!.Media2 is not TMessageMediaToDo oldTodoMedia)
            {
                // TDLib: "There is no checklist in the message to edit".
                RpcErrors.RpcErrors400.MediaInvalid.ThrowRpcError();
                return null!;
            }

            TodoListHelper.ValidateTodoList(editTodo.Todo);

            var remainingIds = editTodo.Todo.List.Select(p => p.Id).ToHashSet();
            var keptCompletions = TodoMediaFactory.ToCompletionItems(oldTodoMedia.Completions)
                .Where(p => remainingIds.Contains(p.Id))
                .ToList();

            media = TodoMediaFactory.Create(editTodo.Todo, keptCompletions);
        }

        // A live-location edit updates the coordinates of an existing messageMediaGeoLive (or stops
        // sharing when stopped=true, usually with inputGeoPointEmpty). The original validity period
        // and start date are preserved, so the location is only ever built from the stored media.
        // See https://corefork.telegram.org/api/live-location
        if (obj.Media is TInputMediaGeoLive editGeoLive)
        {
            if (messageReadModel!.Media2 is not TMessageMediaGeoLive oldGeoLive)
            {
                // TDLib: "There is no live location in the message to edit".
                RpcErrors.RpcErrors400.MediaPrevInvalid.ThrowRpcError();
                return null!;
            }

            GeoLiveHelper.Validate(editGeoLive, forEdit: true);
            media = GeoLiveHelper.BuildEditedMedia(editGeoLive, oldGeoLive, messageReadModel.Date, CurrentDate);
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
        // An edit that does not carry a text keeps the stored one, so it has to keep the stored
        // entities too — dropping them would strip the formatting off a keyboard-only edit.
        var entities = messageTextEdited || obj.Entities != null
            ? (await messageAppService.ProcessMessageEntitiesAsync(obj.Message, obj.Entities, peer)).Entities
            : messageReadModel.Entities2;

        // Hashtags are indexed from the text, so an edit that leaves the text alone must leave the
        // index alone as well.
        var hashtags = messageTextEdited
            ? messageAppService.GetHashtags(obj.Message)
            : messageReadModel.Hashtags;
        var command = new EditOutboxMessageCommand(MessageId.Create(ownerPeerId, obj.Id, obj.QuickReplyShortcutId.HasValue), input.ToRequestInfo(), obj.Id, message, CurrentDate, entities, media, obj.ReplyMarkup, obj.InvertMedia, hashtags, encryptedData, inboxMessageEncryptedData);
        await commandBus.PublishAsync(command);

        // Create admin log for channel message edit
        if (peer.PeerType == PeerType.Channel)
        {
            await CreateEditMessageAdminLogAsync(input, peer, messageReadModel, obj.Message ?? message, encryptedData);
        }


        // A live location that just moved may have entered another member's proximity radius. Runs
        // after the edit is published so the alert reflects the stored position, and only while the
        // location is still being shared — a stop must not raise an alert. See
        // https://corefork.telegram.org/api/live-location#proximity-alert
        if (media is TMessageMediaGeoLive movedGeoLive &&
            GeoLiveHelper.IsActive(movedGeoLive, messageReadModel.Date, CurrentDate))
        {
            // The location belongs to the message author, who is not necessarily the caller: a channel
            // admin holding EditMessages may edit somebody else's message. Attributing the move to the
            // editor would both name the wrong peer and stop the author's own message from being
            // excluded, alerting them about their own movement.
            await geoProximityAlertService.CheckAsync(input, peer, ownerPeerId, messageReadModel.SenderUserId,
                movedGeoLive);
        }

        return null !;
    }

    /// <summary>
    /// A caption is bound by <c>caption_length_limit_default</c> / <c>caption_length_limit_premium</c>
    /// rather than by the 4096 of a plain text message. The sender is only looked up when the text is
    /// long enough for the distinction to matter.
    /// </summary>
    private async Task ValidateEditedCaptionLengthAsync(long userId, string? message)
    {
        if (message == null || message.Length <= MessageLengthHelper.CaptionLengthLimitDefault)
        {
            return;
        }

        var sender = await userAppService.GetAsync(userId);
        MessageLengthHelper.ValidateCaption(message, sender?.Premium ?? false);
    }

    /// <summary>
    /// Edits an entry of the schedule queue: text, media, keyboard and the schedule date itself.
    /// Returns null when the id does not belong to the queue of this peer.
    /// See https://corefork.telegram.org/api/scheduled-messages
    /// </summary>
    private async Task<IUpdates?> TryEditScheduledMessageAsync(IRequestInput input, RequestEditMessage obj, Peer peer,
        IMessageMedia? media)
    {
        var sharedQueue = await scheduledMessageStore.CheckQueueAccessAsync(peer, input.UserId);
        var document = (await scheduledMessageStore.GetQueueAsync(peer, input.UserId, sharedQueue, [obj.Id]))
            .FirstOrDefault();
        if (document == null)
        {
            return null;
        }

        if (document.SenderUserId != input.UserId)
        {
            // In a channel every admin sees the shared queue, but only the author may rewrite a post.
            RpcErrors.RpcErrors403.MessageAuthorRequired.ThrowRpcError();
        }

        var item = document.Item;
        if (!string.IsNullOrEmpty(obj.Message))
        {
            // Keep the text encrypted at rest the same way CreateDocument does: store the ciphertext and
            // blank the plaintext when encryption is on, otherwise store the plaintext and drop any stale
            // ciphertext left over from before the edit.
            var ownerPeerId = document.OwnerPeerId;
            var encryptedData = messageEncryptionHelper.IsEnabled
                ? (ReadOnlyMemory<byte>?)messageEncryptionHelper.Encrypt(ownerPeerId, obj.Message)
                : null;

            var scheduledEntities =
                (await messageAppService.ProcessMessageEntitiesAsync(obj.Message, obj.Entities, peer)).Entities;

            item = item with
            {
                Message = encryptedData.HasValue ? string.Empty : obj.Message,
                Entities = scheduledEntities,
                EncryptedData = encryptedData
            };
        }

        if (media != null)
        {
            item = item with
            {
                Media = media,
                SendMessageType = SendMessageType.Media,
                MessageType = mediaHelper.GeMessageType(media)
            };
        }
        else if (obj.Media is TInputMediaGeoLive scheduledGeoLive)
        {
            // The live-location branch of the history path is skipped for a queued message, so the
            // media has to be rebuilt here from the item still sitting in the queue. Without this the
            // edit would silently keep the old coordinates.
            if (item.Media is not TMessageMediaGeoLive oldScheduledGeoLive)
            {
                RpcErrors.RpcErrors400.MediaPrevInvalid.ThrowRpcError();
                return null;
            }

            GeoLiveHelper.Validate(scheduledGeoLive, forEdit: true);
            // A queued message has not been sent yet, so its validity window has not started; the
            // item's own date is what a stop would be measured against.
            var newScheduledGeoLive = GeoLiveHelper.BuildEditedMedia(scheduledGeoLive, oldScheduledGeoLive,
                item.Date, CurrentDate);

            item = item with
            {
                Media = newScheduledGeoLive,
                SendMessageType = SendMessageType.Media,
                MessageType = mediaHelper.GeMessageType(newScheduledGeoLive)
            };
        }

        if (obj.ReplyMarkup != null)
        {
            item = item with { ReplyMarkup = obj.ReplyMarkup };
        }

        document.Item = item with { InvertMedia = obj.InvertMedia };
        document.EditDate = CurrentDate;

        if (obj.ScheduleRepeatPeriod.HasValue)
        {
            document.RepeatPeriod = obj.ScheduleRepeatPeriod;
        }

        if (obj.ScheduleDate.HasValue)
        {
            await scheduledMessageStore.ValidateAsync(document.SenderUserId, peer, obj.ScheduleDate.Value,
                document.RepeatPeriod, 0);
            document.ScheduleDate = obj.ScheduleDate.Value;
        }

        // Moving a queued message to (almost) now means sending it, exactly like
        // messages.sendScheduledMessages would.
        if (ScheduledMessageRules.ShouldSendImmediately(document.ScheduleDate, CurrentDate))
        {
            return await scheduledMessageDispatcher.FlushAsync([document], input.ToRequestInfo());
        }

        await scheduledMessageStore.ReplaceAsync(document);

        var updates = scheduledMessageStore.BuildNewScheduledUpdates([document], input.UserId, input.Layer);

        // A shared broadcast-channel queue is edited in every admin's scheduled view, not only the
        // author's other sessions. See https://corefork.telegram.org/api/scheduled-messages
        var audience = await scheduledMessageStore.GetQueueAudienceAsync(peer, document.SenderUserId);
        foreach (var userId in audience)
        {
            await objectMessageSender.PushMessageToPeerAsync(new Peer(PeerType.User, userId), updates,
                excludeAuthKeyId: userId == input.UserId ? input.AuthKeyId : null);
        }

        return updates;
    }

    private async Task CreateEditMessageAdminLogAsync(IRequestInput input, Peer peer, IMessageReadModel oldMessage, string newMessageText, byte[]? newEncryptedData)
    {
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

        await AdminLogHelper.LogEditMessage(mongoDatabase, peer.PeerId, input.UserId, prevMessage, newMessage);
    }

    /// <summary>
    /// Records a <c>channelAdminLogEventActionStopPoll</c> entry when a poll is stopped in a channel.
    /// </summary>
    private async Task CreateStopPollAdminLogAsync(IRequestInput input, Peer peer, IMessageReadModel pollMessage)
    {
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

        await AdminLogHelper.LogStopPoll(mongoDatabase, peer.PeerId, input.UserId, message);
    }
}