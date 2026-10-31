using MyTelegram.Messenger.Services.Premium;
using StackExchange.Redis;
using MongoDB.Driver;
using MyTelegram.Messenger.Handlers.LatestLayer.Payments;
using MyTelegram.Messenger.Services.Dice;
namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Send an <a href="https://corefork.telegram.org/api/files#albums-grouped-media">album or grouped media</a>
/// Possible errors
/// Code Type Description
/// 403 ALLOW_PAYMENT_REQUIRED_%d This peer charges %d <a href="https://corefork.telegram.org/api/stars">Telegram Stars</a> per message, but the <code>allow_paid_stars</code> was not set or its value is smaller than %d.
/// 400 BUSINESS_CONNECTION_INVALID The <code>connection_id</code> passed to the wrapping <a href="https://corefork.telegram.org/api/business">invokeWithBusinessConnection</a> call is invalid.
/// 400 BUSINESS_PEER_INVALID Messages can't be set to the specified peer through the current <a href="https://corefork.telegram.org/api/business#connected-bots">business connection</a>.
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 400 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// 400 CHAT_FORWARDS_RESTRICTED You can't forward messages from a protected chat.
/// 403 CHAT_SEND_MEDIA_FORBIDDEN You can't send media in this chat.
/// 403 CHAT_SEND_PHOTOS_FORBIDDEN You can't send photos in this chat.
/// 403 CHAT_SEND_VIDEOS_FORBIDDEN You can't send videos in this chat.
/// 403 CHAT_WRITE_FORBIDDEN You can't write in this chat.
/// 400 EFFECT_ID_INVALID The specified effect ID is invalid.
/// 400 ENTITY_BOUNDS_INVALID A specified <a href="https://corefork.telegram.org/api/entities#entity-length">entity offset or length</a> is invalid, see <a href="https://corefork.telegram.org/api/entities#entity-length">here »</a> for info on how to properly compute the entity offset/length.
/// 400 FILE_REFERENCE_%d_EMPTY  
/// 400 FILE_REFERENCE_%d_EXPIRED The file reference of the media file at index %d in the passed media array expired, it <a href="https://corefork.telegram.org/api/file-references">must be refreshed as specified in the documentation</a>. .
/// 400 FILE_REFERENCE_%d_INVALID The <a href="https://corefork.telegram.org/api/file-references">file reference</a> of the media file at index %d in the passed media array is invalid.
/// 400 MEDIA_CAPTION_TOO_LONG The caption is too long.
/// 400 MEDIA_EMPTY The provided media object is invalid.
/// 400 MEDIA_INVALID Media invalid.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// 400 MULTI_MEDIA_TOO_LONG Too many media files for album.
/// 406 PEER_ID_INVALID The provided peer id is invalid.
/// 400 QUICK_REPLIES_BOT_NOT_ALLOWED <a href="https://corefork.telegram.org/api/business#quick-reply-shortcuts">Quick replies</a> cannot be used by bots.
/// 400 QUICK_REPLIES_TOO_MUCH A maximum of <a href="https://corefork.telegram.org/api/config#quick-replies-limit">appConfig.<code>quick_replies_limit</code></a> shortcuts may be created, the limit was reached.
/// 500 RANDOM_ID_DUPLICATE You provided a random ID that was already used.
/// 400 RANDOM_ID_EMPTY Random ID empty.
/// 400 REPLY_MESSAGES_TOO_MUCH Each shortcut can contain a maximum of <a href="https://corefork.telegram.org/api/config#quick-reply-messages-limit">appConfig.<code>quick_reply_messages_limit</code></a> messages, the limit was reached.
/// 400 REPLY_TO_INVALID The specified <code>reply_to</code> field is invalid.
/// 400 SCHEDULE_DATE_TOO_LATE You can't schedule a message this far in the future.
/// 400 SCHEDULE_TOO_MUCH There are too many scheduled messages.
/// 400 SEND_AS_PEER_INVALID You can't send messages as the specified peer.
/// 420 SLOWMODE_WAIT_%d Slowmode is enabled in this chat: wait %d seconds before sending another message to this chat.
/// 400 TOPIC_CLOSED This topic was closed, you can't send messages to it anymore.
/// 400 TOPIC_DELETED The specified topic was deleted.
/// 400 USER_BANNED_IN_CHANNEL You're banned from sending messages in supergroups/channels.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.sendMultiMedia"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class SendMultiMediaHandler(IMessageAppService messageAppService, IMediaHelper mediaHelper, //IRequestCacheAppService requestCacheAppService,
 IPeerHelper peerHelper, IRandomHelper randomHelper, IQueryProcessor queryProcessor, IMongoDatabase mongoDatabase, IPrivacyAppService privacyAppService, IMessageEffectAppService messageEffectAppService, IUserAppService userAppService, MyTelegram.Messenger.Services.Gifs.ISentGifProcessor sentGifProcessor, MyTelegram.Messenger.Services.Stickers.ISentStickerProcessor sentStickerProcessor, MyTelegram.Messenger.Services.Stickers.IAttachedStickerRecorder attachedStickerRecorder, IFileReferenceHelper fileReferenceHelper, IPremiumLimitGuard premiumLimitGuard) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestSendMultiMedia, MyTelegram.Schema.IUpdates>
{
    //private readonly IRequestCacheAppService _requestCacheAppService;
    //_requestCacheAppService = requestCacheAppService;
    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestSendMultiMedia obj)
    {
        // The index in FILE_REFERENCE_%d_EXPIRED is the position in this vector, which is how the client
        // knows which album entry to repair. See https://corefork.telegram.org/api/file-references
        for (var i = 0; i < obj.MultiMedia.Count; i++)
        {
            InputMediaFileReferenceChecker.Check(fileReferenceHelper, obj.MultiMedia[i].Media, i);
        }

        // A dice cannot be part of an album: TDLib's is_allowed_media_group_content is false for it, so no
        // client groups one. Checked before anything is charged or written.
        // See https://corefork.telegram.org/api/dice
        foreach (var singleMedia in obj.MultiMedia)
        {
            DiceMediaGuard.ThrowIfDice(singleMedia.Media);
        }

        var toPeerForPaid = peerHelper.GetPeer(obj.Peer, input.UserId);
        // Item 22: enforce blocklist for album/multimedia sends too. Without this guard
        // a blocked user could spam media albums while their text messages 403'd.
        if (toPeerForPaid.PeerType == PeerType.User && toPeerForPaid.PeerId != input.UserId)
        {
            var blocksCol = mongoDatabase.GetCollection<MongoDB.Bson.BsonDocument>("user-blocks");
            var blockedByThemFilter = MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("_id", $"{toPeerForPaid.PeerId}-{input.UserId}");
            if (await blocksCol.Find(blockedByThemFilter).Limit(1).AnyAsync())
                RpcErrors.RpcErrors403.UserIsBlocked.ThrowRpcError();
            var blockedByUsFilter = MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("_id", $"{input.UserId}-{toPeerForPaid.PeerId}");
            if (await blocksCol.Find(blockedByUsFilter).Limit(1).AnyAsync())
                RpcErrors.RpcErrors400.YouBlockedUser.ThrowRpcError();
        }

        // Albums bypassed privacyKeyVoiceMessages: only sendMedia checked it, so bundling a
        // voice note into a multi-media send delivered it regardless of the recipient's rule.
        if (toPeerForPaid.PeerType == PeerType.User
            && toPeerForPaid.PeerId != input.UserId
            && VoiceMessageHelper.ContainsVoiceMedia(obj.MultiMedia))
        {
            await privacyAppService.ApplyPrivacyAsync(input.UserId, toPeerForPaid.PeerId,
                _ => RpcErrors.RpcErrors403.ChatSendVoicesForbidden.ThrowRpcError(),
                [PrivacyType.VoiceMessages]);
        }


        var channelForMonoforum = toPeerForPaid.PeerType == PeerType.Channel
            ? await queryProcessor.ProcessAsync(new GetChannelByIdQuery(toPeerForPaid.PeerId))
            : null;
        var isMonoforum = channelForMonoforum?.IsMonoforum == true;
        long? paidMessageStars = null;
        if (toPeerForPaid.PeerType == PeerType.User)
        {
            var targetGps = await queryProcessor.ProcessAsync(new GetGlobalPrivacySettingsQuery(toPeerForPaid.PeerId));
            var requiredStars = targetGps?.NoncontactPeersPaidStars ?? 0;
            if (requiredStars > 0)
            {
                if ((obj.AllowPaidStars ?? 0) < requiredStars)
                    RpcErrors.RpcErrors403.AllowPaymentRequiredX.ThrowRpcError((int)requiredStars);
                var balance = await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, input.UserId);
                if (balance < requiredStars)
                    RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();
                await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, input.UserId, -requiredStars);
                await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, toPeerForPaid.PeerId, requiredStars);
                var paidMsgCount = (int)Math.Max(1, requiredStars);
                await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, input.UserId, -requiredStars, peerUserId: toPeerForPaid.PeerId, paidMessages: paidMsgCount);
                await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, toPeerForPaid.PeerId, requiredStars, peerUserId: input.UserId, paidMessages: paidMsgCount);
                paidMessageStars = requiredStars;
            }
        }
        var groupId = randomHelper.NextInt64();
        var groupItemCount = obj.MultiMedia.Count;
        var requestInfo = input.ToRequestInfo();
        int? replyToMsgId = null;
        int? topMsgId = null;
        Peer? savedPeerId = null;
        if (obj.ReplyTo is TInputReplyToMessage replyToMessage)
        {
            replyToMsgId = replyToMessage.ReplyToMsgId;
            topMsgId = replyToMessage.TopMsgId;
            if (replyToMessage.MonoforumPeerId != null)
            {
                savedPeerId = peerHelper.GetPeer(replyToMessage.MonoforumPeerId, input.UserId);
                topMsgId = null;
            }
        }
        else if (obj.ReplyTo is TInputReplyToMonoForum monoForumReply)
        {
            savedPeerId = peerHelper.GetPeer(monoForumReply.MonoforumPeerId, input.UserId);
        }

        if (isMonoforum)
        {
            foreach (var media in obj.MultiMedia)
            {
                if (media.Media is TInputMediaPoll)
                {
                    RpcErrors.RpcErrors403.ChatSendPollForbidden.ThrowRpcError();
                }
            }
        }
        if (toPeerForPaid.PeerType == PeerType.Channel)
        {
            var (_, chargedStars) = await MonoforumCompatibilityHelper.TryChargeMonoforumMessageAsync(
                input, toPeerForPaid, savedPeerId, obj.AllowPaidStars, queryProcessor, mongoDatabase);
            paidMessageStars = chargedStars ?? paidMessageStars;
        }

        var sendAs = peerHelper.GetPeer(obj.SendAs, input.UserId);
        var effect = await messageEffectAppService.ValidateEffectAsync(obj.Effect, input.UserId, toPeerForPaid.PeerType);

        if (obj.MultiMedia.Any(p => p.Message?.Length > MessageLengthHelper.CaptionLengthLimitDefault))
        {
            var sender = await userAppService.GetAsync(input.UserId);
            foreach (var item in obj.MultiMedia)
            {
                MessageLengthHelper.ValidateCaption(item.Message, sender?.Premium ?? false);
            }
        }

        var inputs = new List<SendMessageInput>();
        foreach (var inputSingleMedia in obj.MultiMedia)
        {
            // upload_max_fileparts_default / _premium: 2 GB, or 4 GB with Premium.
            // See https://corefork.telegram.org/api/premium#doubled-limits
            await premiumLimitGuard.EnsureUploadPartsAsync(input.UserId, inputSingleMedia.Media);
            var media = await mediaHelper.SaveMediaAsync(inputSingleMedia.Media);

            // Same GIF handling as the single-media path: convert to MPEG4 when needed and add the
            // result to the sender's saved GIFs.
            media = await sentGifProcessor.ProcessAsync(input, media);
            media = await attachedStickerRecorder.ProcessAsync(inputSingleMedia.Media, media);
            await sentStickerProcessor.ProcessAsync(input, media, obj.UpdateStickersetsOrder);
            var sendMessageInput = new SendMessageInput(requestInfo, input.UserId, peerHelper.GetPeer(obj.Peer, input.UserId), inputSingleMedia.Message, inputSingleMedia.RandomId, clearDraft: obj.ClearDraft, entities: inputSingleMedia.Entities, media: media, //replyToMsgId: replyToMsgId,
 inputReplyTo: obj.ReplyTo, sendMessageType: SendMessageType.Media, messageType: mediaHelper.GeMessageType(media), groupId: groupId, groupItemCount: groupItemCount, topMsgId: topMsgId, sendAs: sendAs, effect: effect, inputQuickReplyShortcut: obj.QuickReplyShortcut, isSendGroupedMessage: true, silent: obj.Silent, scheduleDate: obj.ScheduleDate, invertMedia: obj.InvertMedia, paidMessageStars: paidMessageStars, savedPeerId: savedPeerId, noForwards: obj.Noforwards);
            inputs.Add(sendMessageInput);
        //await _messageAppService.SendMessageAsync(sendMessageInput);
        //_requestCacheAppService.AddRequest(input.ReqMsgId, input.AuthKeyId, input.RequestSessionId, input.SeqNumber);
        }

        await messageAppService.SendMessageAsync(inputs);
        return null !;
    }
}
