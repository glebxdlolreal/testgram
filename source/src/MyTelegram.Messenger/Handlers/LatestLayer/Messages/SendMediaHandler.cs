using StackExchange.Redis;
using MongoDB.Driver;
using MyTelegram.Messenger.Handlers.LatestLayer.Payments;
using MyTelegram.Messenger.Services.PaidMedia;
namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Send a media
/// Possible errors
/// Code Type Description
/// 406 ALLOW_PAYMENT_REQUIRED This peer only accepts <a href="https://corefork.telegram.org/api/paid-messages">paid messages »</a>: this error is only emitted for older layers without paid messages support, so the client must be updated in order to use paid messages.  .
/// 403 ALLOW_PAYMENT_REQUIRED_%d This peer charges %d <a href="https://corefork.telegram.org/api/stars">Telegram Stars</a> per message, but the <code>allow_paid_stars</code> was not set or its value is smaller than %d.
/// 400 BOT_GAMES_DISABLED Games can't be sent to channels.
/// 400 BOT_PAYMENTS_DISABLED Please enable bot payments in botfather before calling this method.
/// 400 BROADCAST_PUBLIC_VOTERS_FORBIDDEN You can't forward polls with public voters.
/// 400 BUSINESS_CONNECTION_INVALID The <code>connection_id</code> passed to the wrapping <a href="https://corefork.telegram.org/api/business">invokeWithBusinessConnection</a> call is invalid.
/// 400 BUSINESS_PEER_INVALID Messages can't be set to the specified peer through the current <a href="https://corefork.telegram.org/api/business#connected-bots">business connection</a>.
/// 400 BUSINESS_PEER_USAGE_MISSING You cannot send a message to a user through a <a href="https://corefork.telegram.org/api/business#connected-bots">business connection</a> if the user hasn't recently contacted us.
/// 400 BUTTON_COPY_TEXT_INVALID The specified <a href="https://corefork.telegram.org/constructor/keyboardButtonCopy">keyboardButtonCopy</a>.<code>copy_text</code> is invalid.
/// 400 BUTTON_DATA_INVALID The data of one or more of the buttons you provided is invalid.
/// 400 BUTTON_POS_INVALID The position of one of the keyboard buttons is invalid (i.e. a Game or Pay button not in the first position, and so on...).
/// 400 BUTTON_TYPE_INVALID The type of one or more of the buttons you provided is invalid.
/// 400 BUTTON_URL_INVALID Button URL invalid.
/// 400 BUTTON_USER_PRIVACY_RESTRICTED The privacy setting of the user specified in a <a href="https://corefork.telegram.org/constructor/inputKeyboardButtonUserProfile">inputKeyboardButtonUserProfile</a> button do not allow creating such a button.
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 406 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 403 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// 400 CHAT_FORWARDS_RESTRICTED You can't forward messages from a protected chat.
/// 403 CHAT_GUEST_SEND_FORBIDDEN You join the discussion group before commenting, see <a href="https://corefork.telegram.org/api/discussion#requiring-users-to-join-the-group">here »</a> for more info.
/// 400 CHAT_RESTRICTED You can't send messages in this chat, you were restricted.
/// 403 CHAT_SEND_AUDIOS_FORBIDDEN You can't send audio messages in this chat.
/// 403 CHAT_SEND_DOCS_FORBIDDEN You can't send documents in this chat.
/// 403 CHAT_SEND_GIFS_FORBIDDEN You can't send gifs in this chat.
/// 403 CHAT_SEND_MEDIA_FORBIDDEN You can't send media in this chat.
/// 403 CHAT_SEND_PHOTOS_FORBIDDEN You can't send photos in this chat.
/// 403 CHAT_SEND_PLAIN_FORBIDDEN You can't send non-media (text) messages in this chat.
/// 403 CHAT_SEND_POLL_FORBIDDEN You can't send polls in this chat.
/// 403 CHAT_SEND_ROUNDVIDEOS_FORBIDDEN You can't send round videos to this chat.
/// 403 CHAT_SEND_STICKERS_FORBIDDEN You can't send stickers in this chat.
/// 403 CHAT_SEND_VIDEOS_FORBIDDEN You can't send videos in this chat.
/// 403 CHAT_SEND_VOICES_FORBIDDEN You can't send voice recordings in this chat.
/// 403 CHAT_WRITE_FORBIDDEN You can't write in this chat.
/// 400 CURRENCY_TOTAL_AMOUNT_INVALID The total amount of all prices is invalid.
/// 400 DOCUMENT_INVALID The specified document is invalid.
/// 400 EFFECT_CHAT_INVALID  
/// 400 EFFECT_ID_INVALID The specified effect ID is invalid.
/// 400 EMOTICON_INVALID The specified emoji is invalid.
/// 400 ENTITY_BOUNDS_INVALID A specified <a href="https://corefork.telegram.org/api/entities#entity-length">entity offset or length</a> is invalid, see <a href="https://corefork.telegram.org/api/entities#entity-length">here »</a> for info on how to properly compute the entity offset/length.
/// 400 EXTENDED_MEDIA_AMOUNT_INVALID The specified <code>stars_amount</code> of the passed <a href="https://corefork.telegram.org/constructor/inputMediaPaidMedia">inputMediaPaidMedia</a> is invalid.
/// 400 EXTENDED_MEDIA_EMPTY  
/// 400 EXTENDED_MEDIA_INVALID The specified paid media is invalid.
/// 400 EXTERNAL_URL_INVALID External URL invalid.
/// 400 FILE_PARTS_INVALID The number of file parts is invalid.
/// 400 FILE_PART_LENGTH_INVALID The length of a file part is invalid.
/// 400 FILE_REFERENCE_EMPTY An empty <a href="https://corefork.telegram.org/api/file-references">file reference</a> was specified.
/// 400 FILE_REFERENCE_EXPIRED File reference expired, it must be refetched as described in <a href="https://corefork.telegram.org/api/file-references">the documentation</a>.
/// 400 GAME_BOT_INVALID Bots can't send another bot's game.
/// 400 IMAGE_PROCESS_FAILED Failure while processing image.
/// 400 INPUT_FILE_INVALID The specified <a href="https://corefork.telegram.org/type/InputFile">InputFile</a> is invalid.
/// 400 INPUT_USER_DEACTIVATED The specified user was deleted.
/// 400 INVOICE_PAYLOAD_INVALID The specified invoice payload is invalid.
/// 400 MD5_CHECKSUM_INVALID The MD5 checksums do not match.
/// 400 MEDIA_CAPTION_TOO_LONG The caption is too long.
/// 400 MEDIA_EMPTY The provided media object is invalid.
/// 400 MEDIA_INVALID Media invalid.
/// 400 MESSAGE_EMPTY The provided message is empty.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// 400 PAYMENT_PROVIDER_INVALID The specified payment provider is invalid.
/// 406 PEER_ID_INVALID The provided peer id is invalid.
/// 400 PHOTO_EXT_INVALID The extension of the photo is invalid.
/// 400 PHOTO_INVALID_DIMENSIONS The photo dimensions are invalid.
/// 400 PHOTO_SAVE_FILE_INVALID Internal issues, try again later.
/// 400 POLL_ANSWERS_INVALID Invalid poll answers were provided.
/// 400 POLL_ANSWER_INVALID One of the poll answers is not acceptable.
/// 400 POLL_OPTION_DUPLICATE Duplicate poll options provided.
/// 400 POLL_OPTION_INVALID Invalid poll option provided.
/// 400 POLL_QUESTION_INVALID One of the poll questions is not acceptable.
/// 403 PREMIUM_ACCOUNT_REQUIRED A premium account is required to execute this action.
/// 403 PRIVACY_PREMIUM_REQUIRED You need a <a href="https://corefork.telegram.org/api/premium">Telegram Premium subscription</a> to send a message to this user.
/// 400 QUICK_REPLIES_BOT_NOT_ALLOWED <a href="https://corefork.telegram.org/api/business#quick-reply-shortcuts">Quick replies</a> cannot be used by bots.
/// 400 QUICK_REPLIES_TOO_MUCH A maximum of <a href="https://corefork.telegram.org/api/config#quick-replies-limit">appConfig.<code>quick_replies_limit</code></a> shortcuts may be created, the limit was reached.
/// 400 QUIZ_CORRECT_ANSWERS_EMPTY No correct quiz answer was specified.
/// 400 QUIZ_CORRECT_ANSWERS_TOO_MUCH You specified too many correct answers in a quiz, quizzes can only have one right answer!
/// 400 QUIZ_CORRECT_ANSWER_INVALID An invalid value was provided to the correct_answers field.
/// 400 QUIZ_MULTIPLE_INVALID Quizzes can't have the multiple_choice flag set!
/// 500 RANDOM_ID_DUPLICATE You provided a random ID that was already used.
/// 400 REPLY_MARKUP_BUY_EMPTY Reply markup for buy button empty.
/// 400 REPLY_MARKUP_GAME_EMPTY A game message is being edited, but the newly provided keyboard doesn't have a keyboardButtonGame button.
/// 400 REPLY_MARKUP_INVALID The provided reply markup is invalid.
/// 400 REPLY_MARKUP_TOO_LONG The specified reply_markup is too long.
/// 400 REPLY_MESSAGES_TOO_MUCH Each shortcut can contain a maximum of <a href="https://corefork.telegram.org/api/config#quick-reply-messages-limit">appConfig.<code>quick_reply_messages_limit</code></a> messages, the limit was reached.
/// 400 REPLY_MESSAGE_ID_INVALID The specified reply-to message ID is invalid.
/// 400 REPLY_TO_MONOFORUM_PEER_INVALID The specified inputReplyToMonoForum.monoforum_peer_id is invalid.
/// 400 SCHEDULE_BOT_NOT_ALLOWED Bots cannot schedule messages.
/// 400 SCHEDULE_DATE_TOO_LATE You can't schedule a message this far in the future.
/// 400 SCHEDULE_TOO_MUCH There are too many scheduled messages.
/// 400 SEND_AS_PEER_INVALID You can't send messages as the specified peer.
/// 420 SLOWMODE_WAIT_%d Slowmode is enabled in this chat: wait %d seconds before sending another message to this chat.
/// 400 STARS_INVOICE_INVALID The specified Telegram Star invoice is invalid.
/// 400 STORY_ID_INVALID The specified story ID is invalid.
/// 400 SUBSCRIPTION_EXPORT_MISSING You cannot send a <a href="https://corefork.telegram.org/api/subscriptions#bot-subscriptions">bot subscription invoice</a> directly, you may only create invoice links using <a href="https://corefork.telegram.org/method/payments.exportInvoice">payments.exportInvoice</a>.
/// 400 SUGGESTED_POST_PEER_INVALID You cannot send suggested posts to non-<a href="https://corefork.telegram.org/api/monoforum">monoforum</a> peers.
/// 400 TERMS_URL_INVALID The specified <a href="https://corefork.telegram.org/constructor/invoice">invoice</a>.<code>terms_url</code> is invalid.
/// 400 TODO_ITEMS_EMPTY A checklist was specified, but no <a href="https://corefork.telegram.org/api/todo">checklist items</a> were passed.
/// 400 TODO_ITEM_DUPLICATE Duplicate <a href="https://corefork.telegram.org/api/todo">checklist items</a> detected.
/// 406 TOPIC_CLOSED This topic was closed, you can't send messages to it anymore.
/// 406 TOPIC_DELETED The specified topic was deleted.
/// 400 TTL_MEDIA_INVALID Invalid media Time To Live was provided.
/// 400 USER_BANNED_IN_CHANNEL You're banned from sending messages in supergroups/channels.
/// 403 USER_IS_BLOCKED You were blocked by this user.
/// 400 USER_IS_BOT Bots can't send messages to other bots.
/// 400 VIDEO_CONTENT_TYPE_INVALID The video's content type is invalid.
/// 400 VOICE_MESSAGES_FORBIDDEN This user's privacy settings forbid you from sending voice messages.
/// 400 WEBDOCUMENT_MIME_INVALID Invalid webdocument mime type provided.
/// 400 WEBPAGE_CURL_FAILED Failure while fetching the webpage with cURL.
/// 400 WEBPAGE_MEDIA_EMPTY Webpage media empty.
/// 400 WEBPAGE_NOT_FOUND A preview for the specified webpage <code>url</code> could not be generated.
/// 400 WEBPAGE_URL_INVALID The specified webpage <code>url</code> is invalid.
/// 400 YOU_BLOCKED_USER You blocked this user.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.sendMedia"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class SendMediaHandler(IMediaHelper mediaHelper, IMessageAppService messageAppService, IPeerHelper peerHelper, IRandomHelper randomHelper, ICommandBus commandBus, IPrivacyAppService privacyAppService, IQueryProcessor queryProcessor, IMongoDatabase mongoDatabase, IIdGenerator idGenerator) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestSendMedia, MyTelegram.Schema.IUpdates>
{
    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestSendMedia obj)
    {
        var needCheckAudioMessagePrivacy = false;
        switch (obj.Media)
        {
            case Schema.TInputMediaUploadedDocument inputMediaUploadedDocument:
                if (inputMediaUploadedDocument.Attributes.Any(p => p is TDocumentAttributeAudio))
                {
                    needCheckAudioMessagePrivacy = true;
                }

                break;
                //case Schema.Layer197.TInputMediaUploadedDocument inputMediaUploadedDocumentLayer197:
                //    if (inputMediaUploadedDocumentLayer197.Attributes.Any(p => p is TDocumentAttributeAudio))
                //    {
                //        needCheckAudioMessagePrivacy = true;
                //    }
                //    break;
        }

        if (needCheckAudioMessagePrivacy && obj.Peer is TInputPeerUser inputPeerUser)
        {
            await privacyAppService.ApplyPrivacyAsync(input.UserId, inputPeerUser.UserId, (_) =>
            {
                //ThrowHelper.ThrowUserFriendlyException(RpcErrorMessages.ChatSendVoicesForbidden);
                RpcErrors.RpcErrors403.ChatSendVoicesForbidden.ThrowRpcError();
            }, [PrivacyType.VoiceMessages]);
        }

        var toPeer = peerHelper.GetPeer(obj.Peer, input.UserId);
        // Item 22: same blocklist guard as SendMessageHandler. Without this, media
        // (photos / docs / voice / paid media) bypassed the block and still reached
        // the recipient. Throw matches the documented errors above (403 / 400).
        if (toPeer.PeerType == PeerType.User && toPeer.PeerId != input.UserId)
        {
            var blocksCol = mongoDatabase.GetCollection<MongoDB.Bson.BsonDocument>("user-blocks");
            var blockedByThemFilter = MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("_id", $"{toPeer.PeerId}-{input.UserId}");
            if (await blocksCol.Find(blockedByThemFilter).Limit(1).AnyAsync())
                RpcErrors.RpcErrors403.UserIsBlocked.ThrowRpcError();
            var blockedByUsFilter = MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("_id", $"{input.UserId}-{toPeer.PeerId}");
            if (await blocksCol.Find(blockedByUsFilter).Limit(1).AnyAsync())
                RpcErrors.RpcErrors400.YouBlockedUser.ThrowRpcError();
        }

        var channelForMonoforum = toPeer.PeerType == PeerType.Channel
            ? await queryProcessor.ProcessAsync(new GetChannelByIdQuery(toPeer.PeerId))
            : null;
        var isMonoforum = channelForMonoforum?.IsMonoforum == true;
        MonoforumCompatibilityHelper.ValidateSuggestedPostOrThrow(obj.SuggestedPost, isMonoforum);
        long? paidMessageStars = null;
        if (toPeer.PeerType == PeerType.User)
        {
            var targetGps = await queryProcessor.ProcessAsync(new GetGlobalPrivacySettingsQuery(toPeer.PeerId));
            var requiredStars = targetGps?.NoncontactPeersPaidStars ?? 0;
            if (requiredStars > 0)
            {
                if ((obj.AllowPaidStars ?? 0) < requiredStars)
                    RpcErrors.RpcErrors403.AllowPaymentRequiredX.ThrowRpcError((int)requiredStars);
                var balance = await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, input.UserId);
                if (balance < requiredStars)
                    RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();
                await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, input.UserId, -requiredStars);
                await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, toPeer.PeerId, requiredStars);
                var paidMsgCount = (int)Math.Max(1, requiredStars);
                await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, input.UserId, -requiredStars, peerUserId: toPeer.PeerId, paidMessages: paidMsgCount);
                await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, toPeer.PeerId, requiredStars, peerUserId: input.UserId, paidMessages: paidMsgCount);
                paidMessageStars = requiredStars;
            }
        }
        long? pollId = null;
        if (obj.Media is TInputMediaPoll inputMediaPoll)
        {
            if (isMonoforum)
            {
                RpcErrors.RpcErrors403.ChatSendPollForbidden.ThrowRpcError();
            }
            pollId = randomHelper.NextInt64();
            inputMediaPoll.Poll.Id = pollId.Value;
            await CreatePollAsync(input.UserId, toPeer, inputMediaPoll);
        }

        if (obj.Media is TInputMediaTodo inputMediaTodo)
        {
            ValidateTodoList(inputMediaTodo.Todo);
        }

        var ownerPeerId = toPeer.PeerType == PeerType.Channel ? toPeer.PeerId : input.UserId;
        int? preallocatedMessageId = null;
        List<IMessageMedia>? paidMediaItems = null;
        IMessageMedia? media;
        if (obj.Media is TInputMediaPaidMedia inputPaidMedia)
        {
            var paidMedia = await CreatePaidMediaAsync(inputPaidMedia, toPeer, channelForMonoforum, input.UserId);
            media = paidMedia.MessageMedia;
            paidMediaItems = paidMedia.RevealedMedia;
            preallocatedMessageId = (int)await idGenerator.NextIdAsync(IdType.MessageId, ownerPeerId);
        }
        else
        {
            media = await mediaHelper.SaveMediaAsync(obj.Media);
        }
        int? topMsgId = null;
        Peer? savedPeerId = null;
        if (obj.ReplyTo is TInputReplyToMessage replyToMessage)
        {
            topMsgId = replyToMessage.TopMsgId;
            if (replyToMessage.MonoforumPeerId != null)
            {
                savedPeerId = await ResolveMonoforumSavedPeerAsync(toPeer, replyToMessage.MonoforumPeerId);
                topMsgId = null;
            }
        }
        else if (obj.ReplyTo is TInputReplyToMonoForum monoForumReply)
        {
            savedPeerId = await ResolveMonoforumSavedPeerAsync(toPeer, monoForumReply.MonoforumPeerId);
        }

        if (toPeer.PeerType == PeerType.Channel)
        {
            var (_, chargedStars) = await MonoforumCompatibilityHelper.TryChargeMonoforumMessageAsync(
                input, toPeer, savedPeerId, obj.AllowPaidStars, queryProcessor, mongoDatabase);
            paidMessageStars = chargedStars ?? paidMessageStars;
        }

        var sendMessageInput = new SendMessageInput(input.ToRequestInfo(), input.UserId, peerHelper.GetPeer(obj.Peer, input.UserId), obj.Message, obj.RandomId, clearDraft: obj.ClearDraft, entities: obj.Entities, media: media, //replyToMsgId: replyToMsgId,
 inputReplyTo: obj.ReplyTo, sendMessageType: SendMessageType.Media, messageType: mediaHelper.GeMessageType(media), pollId: pollId, topMsgId: topMsgId, sendAs: peerHelper.GetPeer(obj.SendAs, input.UserId), effect: obj.Effect, inputQuickReplyShortcut: obj.QuickReplyShortcut, replyMarkup: obj.ReplyMarkup, silent: obj.Silent, scheduleDate: obj.ScheduleDate, invertMedia: obj.InvertMedia, paidMessageStars: paidMessageStars, savedPeerId: savedPeerId, messageId: preallocatedMessageId, suggestedPost: obj.SuggestedPost, noForwards: obj.Noforwards);
        await messageAppService.SendMessageAsync([sendMessageInput]);

        if (obj.Media is TInputMediaPaidMedia paidInput && paidMediaItems != null && preallocatedMessageId.HasValue)
        {
            await mongoDatabase.GetCollection<PaidMediaDocument>(PaidMediaHelper.CollectionName).ReplaceOneAsync(
                x => x.Id == PaidMediaHelper.MakeId(ownerPeerId, preallocatedMessageId.Value),
                new PaidMediaDocument
                {
                    Id = PaidMediaHelper.MakeId(ownerPeerId, preallocatedMessageId.Value),
                    OwnerPeerId = ownerPeerId,
                    MsgId = preallocatedMessageId.Value,
                    ChannelId = toPeer.PeerId,
                    SellerUserId = input.UserId,
                    SenderUserId = input.UserId,
                    StarsAmount = paidInput.StarsAmount,
                    Payload = paidInput.Payload,
                    ExtendedMedia = paidMediaItems.Select(x => x.ToBytes()).ToList(),
                    Date = DateTime.UtcNow.ToTimestamp()
                },
                new ReplaceOptions { IsUpsert = true });
        }

        return null!;
    }

    private async Task<(IMessageMedia MessageMedia, List<IMessageMedia> RevealedMedia)> CreatePaidMediaAsync(
        TInputMediaPaidMedia inputPaidMedia,
        Peer toPeer,
        IChannelReadModel? channelReadModel,
        long userId)
    {
        if (toPeer.PeerType != PeerType.Channel || channelReadModel?.Broadcast != true)
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();

        if (inputPaidMedia.StarsAmount <= 0 || inputPaidMedia.StarsAmount > PaidMediaHelper.MaxStarsAmount)
            RpcErrors.RpcErrors400.ExtendedMediaAmountInvalid.ThrowRpcError();

        if (inputPaidMedia.ExtendedMedia == null || inputPaidMedia.ExtendedMedia.Count == 0)
            RpcErrors.RpcErrors400.ExtendedMediaInvalid.ThrowRpcError();

        if (inputPaidMedia.ExtendedMedia.Any(x => !PaidMediaHelper.IsAllowedInputMedia(x)))
            RpcErrors.RpcErrors400.ExtendedMediaInvalid.ThrowRpcError();

        var revealed = new List<IMessageMedia>(inputPaidMedia.ExtendedMedia.Count);
        var previews = new TVector<IMessageExtendedMedia>();
        foreach (var item in inputPaidMedia.ExtendedMedia)
        {
            // Android paid-media previews render only stripped/path thumbs here; regular
            // photoSize points to a downloadable file and is ignored while media is locked.
            var strippedThumb = await PaidMediaHelper.TryCreatePreviewThumbFromUploadAsync(mongoDatabase, userId, item);
            var saved = await mediaHelper.SaveMediaAsync(item);
            if (saved is not TMessageMediaPhoto and not TMessageMediaDocument)
                RpcErrors.RpcErrors400.ExtendedMediaInvalid.ThrowRpcError();

            strippedThumb ??= await PaidMediaHelper.TryCreatePreviewThumbFromStoredMediaAsync(mongoDatabase, saved);
            revealed.Add(saved);
            previews.Add(PaidMediaHelper.ToPreview(saved, strippedThumb));
        }

        return (new TMessageMediaPaidMedia
        {
            StarsAmount = inputPaidMedia.StarsAmount,
            ExtendedMedia = previews
        }, revealed);
    }

    private async Task<Peer> ResolveMonoforumSavedPeerAsync(Peer toPeer, IInputPeer monoforumPeerId)
    {
        if (toPeer.PeerType != PeerType.Channel)
            RpcErrors.RpcErrors400.ReplyToMonoforumPeerInvalid.ThrowRpcError();

        var monoforum = await queryProcessor.ProcessAsync(new GetChannelByIdQuery(toPeer.PeerId));
        if (monoforum == null || !monoforum.IsMonoforum)
            RpcErrors.RpcErrors400.ReplyToMonoforumPeerInvalid.ThrowRpcError();

        var savedPeer = peerHelper.GetPeer(monoforumPeerId);
        if (savedPeer is not { PeerType: PeerType.User })
            RpcErrors.RpcErrors400.ReplyToMonoforumPeerInvalid.ThrowRpcError();

        return savedPeer;
    }

    private async Task CreatePollAsync(long creatorUserId, Peer toPeer, TInputMediaPoll inputMediaPoll)
    {
        var poll = inputMediaPoll.Poll;
        var solutionEntities = inputMediaPoll.SolutionEntities;
        if (solutionEntities == null && !string.IsNullOrEmpty(inputMediaPoll.Solution))
        {
            solutionEntities = [];
        }

        // close_period is relative ("close 30s from now"), close_date is absolute. Store an
        // absolute deadline either way so the auto-close service has a single field to scan.
        var closeDate = poll.CloseDate;
        if (poll.ClosePeriod.HasValue && closeDate == null)
        {
            closeDate = CurrentDate + poll.ClosePeriod.Value;
        }

        var command = new CreatePollCommand(PollId.Create(poll.Id), toPeer, poll.Id, poll.MultipleChoice, poll.Quiz, inputMediaPoll.Poll.PublicVoters, poll.Question.Text, poll.Answers.Select((p, index) => new PollAnswer(p.Text.Text, index.ToString(), p.Text.Entities.ToBytes())).ToList(), inputMediaPoll.CorrectAnswers?.Select(p => p.ToString()).ToList(), inputMediaPoll.Solution, solutionEntities, poll.Question.Entities, creatorUserId,
            poll.ClosePeriod,
            closeDate,
            poll.OpenAnswers,
            poll.RevotingDisabled,
            poll.ShuffleAnswers,
            poll.HideResultsUntilClose);
        await commandBus.PublishAsync(command);
    }

    private void ValidateTodoList(ITodoList todoList)
    {
        const int TODO_TITLE_LENGTH_MAX = 255;
        const int TODO_ITEM_LENGTH_MAX = 200;
        const int TODO_ITEMS_MAX = 30;

        // Validate title length
        if (todoList.Title.Text.Length > TODO_TITLE_LENGTH_MAX)
        {
            RpcErrors.RpcErrors400.MessageTooLong.ThrowRpcError();
        }

        // Validate items count
        if (todoList.List.Count == 0)
        {
            RpcErrors.RpcErrors400.TodoItemsEmpty.ThrowRpcError();
        }

        if (todoList.List.Count > TODO_ITEMS_MAX)
        {
            RpcErrors.RpcErrors400.MessageTooLong.ThrowRpcError();
        }

        // Validate item IDs are unique and positive
        var ids = new HashSet<int>();
        foreach (var item in todoList.List)
        {
            if (item.Id <= 0)
            {
                RpcErrors.RpcErrors400.TodoItemDuplicate.ThrowRpcError();
            }

            if (!ids.Add(item.Id))
            {
                RpcErrors.RpcErrors400.TodoItemDuplicate.ThrowRpcError();
            }

            // Validate item title length
            if (item.Title.Text.Length > TODO_ITEM_LENGTH_MAX)
            {
                RpcErrors.RpcErrors400.MessageTooLong.ThrowRpcError();
            }
        }
    }
}
