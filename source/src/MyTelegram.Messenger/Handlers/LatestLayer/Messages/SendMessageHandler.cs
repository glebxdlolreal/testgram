using MyTelegram.Messenger.Services.Premium;
using MyTelegram.Messenger.Services.Bots;
using StackExchange.Redis;
using MongoDB.Driver;
using MongoDB.Bson;
using MyTelegram.Messenger.Handlers.LatestLayer.Payments;
using MyTelegram.Messenger.Helpers;
namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Sends a message to a chat
/// Possible errors
/// Code Type Description
/// 400 ADMIN_RIGHTS_EMPTY The chatAdminRights constructor passed in keyboardButtonRequestPeer.peer_type.user_admin_rights has no rights set (i.e. flags is 0).
/// 406 ALLOW_PAYMENT_REQUIRED This peer only accepts <a href="https://corefork.telegram.org/api/paid-messages">paid messages »</a>: this error is only emitted for older layers without paid messages support, so the client must be updated in order to use paid messages.  .
/// 403 ALLOW_PAYMENT_REQUIRED_%d This peer charges %d <a href="https://corefork.telegram.org/api/stars">Telegram Stars</a> per message, but the <code>allow_paid_stars</code> was not set or its value is smaller than %d.
/// 400 BALANCE_TOO_LOW The transaction cannot be completed because the current <a href="https://corefork.telegram.org/api/stars">Telegram Stars balance</a> is too low.
/// 400 BOT_DOMAIN_INVALID Bot domain invalid.
/// 400 BOT_INVALID This is not a valid bot.
/// 400 BUSINESS_CONNECTION_INVALID The <code>connection_id</code> passed to the wrapping <a href="https://corefork.telegram.org/api/business">invokeWithBusinessConnection</a> call is invalid.
/// 400 BUSINESS_PEER_INVALID Messages can't be set to the specified peer through the current <a href="https://corefork.telegram.org/api/business#connected-bots">business connection</a>.
/// 400 BUSINESS_PEER_USAGE_MISSING You cannot send a message to a user through a <a href="https://corefork.telegram.org/api/business#connected-bots">business connection</a> if the user hasn't recently contacted us.
/// 400 BUTTON_COPY_TEXT_INVALID The specified <a href="https://corefork.telegram.org/constructor/keyboardButtonCopy">keyboardButtonCopy</a>.<code>copy_text</code> is invalid.
/// 400 BUTTON_DATA_INVALID The data of one or more of the buttons you provided is invalid.
/// 400 BUTTON_ID_INVALID The specified button ID is invalid.
/// 400 BUTTON_TYPE_INVALID The type of one or more of the buttons you provided is invalid.
/// 400 BUTTON_URL_INVALID Button URL invalid.
/// 400 BUTTON_USER_INVALID The <code>user_id</code> passed to inputKeyboardButtonUserProfile is invalid!
/// 400 BUTTON_USER_PRIVACY_RESTRICTED The privacy setting of the user specified in a <a href="https://corefork.telegram.org/constructor/inputKeyboardButtonUserProfile">inputKeyboardButtonUserProfile</a> button do not allow creating such a button.
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHANNEL_MONOFORUM_UNSUPPORTED <a href="https://corefork.telegram.org/api/channel#monoforums">Monoforums</a> do not support this feature.
/// 406 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 403 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// 400 CHAT_FORWARDS_RESTRICTED You can't forward messages from a protected chat.
/// 403 CHAT_GUEST_SEND_FORBIDDEN You join the discussion group before commenting, see <a href="https://corefork.telegram.org/api/discussion#requiring-users-to-join-the-group">here »</a> for more info.
/// 400 CHAT_ID_INVALID The provided chat id is invalid.
/// 400 CHAT_RESTRICTED You can't send messages in this chat, you were restricted.
/// 403 CHAT_SEND_PLAIN_FORBIDDEN You can't send non-media (text) messages in this chat.
/// 403 CHAT_WRITE_FORBIDDEN You can't write in this chat.
/// 400 DOCUMENT_INVALID The specified document is invalid.
/// 400 EFFECT_CHAT_INVALID  
/// 400 ENCRYPTION_DECLINED The secret chat was declined.
/// 400 ENTITIES_TOO_LONG You provided too many styled message entities.
/// 400 ENTITY_BOUNDS_INVALID A specified <a href="https://corefork.telegram.org/api/entities#entity-length">entity offset or length</a> is invalid, see <a href="https://corefork.telegram.org/api/entities#entity-length">here »</a> for info on how to properly compute the entity offset/length.
/// 400 ENTITY_MENTION_USER_INVALID You mentioned an invalid user.
/// 400 FROM_MESSAGE_BOT_DISABLED Bots can't use fromMessage min constructors.
/// 400 INPUT_USER_DEACTIVATED The specified user was deleted.
/// 400 MESSAGE_EMPTY The provided message is empty.
/// 400 MESSAGE_TOO_LONG The provided message is too long.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// 500 MSG_WAIT_FAILED A waiting call returned an error.
/// 406 PAYMENT_UNSUPPORTED A detailed description of the error will be received separately as described <a href="https://corefork.telegram.org/api/errors#406-not-acceptable">here »</a>.
/// 404 PEER_ID_INVALID The provided peer id is invalid.
/// 400 PEER_TYPES_INVALID The passed <a href="https://corefork.telegram.org/constructor/keyboardButtonSwitchInline">keyboardButtonSwitchInline</a>.<code>peer_types</code> field is invalid.
/// 400 PINNED_DIALOGS_TOO_MUCH Too many pinned dialogs.
/// 400 POLL_OPTION_INVALID Invalid poll option provided.
/// 403 PREMIUM_ACCOUNT_REQUIRED A premium account is required to execute this action.
/// 406 PRIVACY_PREMIUM_REQUIRED You need a <a href="https://corefork.telegram.org/api/premium">Telegram Premium subscription</a> to send a message to this user.
/// 400 QUICK_REPLIES_BOT_NOT_ALLOWED <a href="https://corefork.telegram.org/api/business#quick-reply-shortcuts">Quick replies</a> cannot be used by bots.
/// 400 QUICK_REPLIES_TOO_MUCH A maximum of <a href="https://corefork.telegram.org/api/config#quick-replies-limit">appConfig.<code>quick_replies_limit</code></a> shortcuts may be created, the limit was reached.
/// 400 QUOTE_TEXT_INVALID The specified <code>reply_to</code>.<code>quote_text</code> field is invalid.
/// 500 RANDOM_ID_DUPLICATE You provided a random ID that was already used.
/// 400 REPLY_MARKUP_INVALID The provided reply markup is invalid.
/// 400 REPLY_MARKUP_TOO_LONG The specified reply_markup is too long.
/// 400 REPLY_MESSAGES_TOO_MUCH Each shortcut can contain a maximum of <a href="https://corefork.telegram.org/api/config#quick-reply-messages-limit">appConfig.<code>quick_reply_messages_limit</code></a> messages, the limit was reached.
/// 400 REPLY_MESSAGE_ID_INVALID The specified reply-to message ID is invalid.
/// 400 REPLY_TO_INVALID The specified <code>reply_to</code> field is invalid.
/// 400 REPLY_TO_MONOFORUM_PEER_INVALID The specified inputReplyToMonoForum.monoforum_peer_id is invalid.
/// 400 REPLY_TO_USER_INVALID The replied-to user is invalid.
/// 400 SCHEDULE_BOT_NOT_ALLOWED Bots cannot schedule messages.
/// 400 SCHEDULE_DATE_TOO_LATE You can't schedule a message this far in the future.
/// 400 SCHEDULE_STATUS_PRIVATE Can't schedule until user is online, if the user's last seen timestamp is hidden by their privacy settings.
/// 400 SCHEDULE_TOO_MUCH There are too many scheduled messages.
/// 400 SEND_AS_PEER_INVALID You can't send messages as the specified peer.
/// 420 SLOWMODE_WAIT_%d Slowmode is enabled in this chat: wait %d seconds before sending another message to this chat.
/// 400 STORIES_NEVER_CREATED This peer hasn't ever posted any stories.
/// 400 STORY_ID_INVALID The specified story ID is invalid.
/// 400 SUGGESTED_POST_AMOUNT_INVALID The specified price for the suggested post is invalid.
/// 400 SUGGESTED_POST_PEER_INVALID You cannot send suggested posts to non-<a href="https://corefork.telegram.org/api/monoforum">monoforum</a> peers.
/// 406 TOPIC_CLOSED This topic was closed, you can't send messages to it anymore.
/// 406 TOPIC_DELETED The specified topic was deleted.
/// 400 USER_BANNED_IN_CHANNEL You're banned from sending messages in supergroups/channels.
/// 403 USER_IS_BLOCKED You were blocked by this user.
/// 400 USER_IS_BOT Bots can't send messages to other bots.
/// 400 WC_CONVERT_URL_INVALID WC convert URL invalid.
/// 400 YOU_BLOCKED_USER You blocked this user.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.sendMessage"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class SendMessageHandler(IMessageAppService messageAppService, IPeerHelper peerHelper, IChannelAppService channelAppService, IOptions<MyTelegramMessengerServerOptions> options, IQueryProcessor queryProcessor, IConnectionMultiplexer redis, IMongoDatabase mongoDatabase, IBotFatherBotService botFatherBotService, IPremiumBotService premiumBotService, IObjectMessageSender objectMessageSender, IPrivacyAppService privacyAppService, IMessageEffectAppService messageEffectAppService, ILogger<SendMessageHandler> logger) : RpcResultObjectHandler<RequestSendMessage, IUpdates>
{
    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestSendMessage obj)
    {
        if (string.IsNullOrWhiteSpace(obj.Message))
            RpcErrors.RpcErrors400.MessageEmpty.ThrowRpcError();

        MessageLengthHelper.ValidateMessage(obj.Message);

        var isNewRandomId = await redis.GetDatabase().StringSetAsync(
            $"sendmsg:{input.UserId}:{obj.RandomId}",
            1,
            TimeSpan.FromMinutes(15),
            When.NotExists);
        if (!isNewRandomId) return null!;

        // Bot commands, like every other autogenerated entity, are produced by MessageEntityDetector
        // while the message is being sent: they are tokens inside the text, not the whole text.
        // See https://corefork.telegram.org/api/entities#autogenerated-entities
        var media = await ProcessJoinChatUrlAsync(obj);

        var sendAs = peerHelper.GetPeer(obj.SendAs, input.UserId);
        var toPeer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (toPeer == null)
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();

        // Item 22: enforce the persisted blocklist before doing any further work. We
        // intentionally check both directions: the recipient may have blocked us
        // (USER_IS_BLOCKED) or we may have blocked them and forgotten about it
        // (YOU_BLOCKED_USER). Without this guard messages happily flow through the
        // entire pipeline even after contacts.block has been called.
        if (toPeer.PeerType == PeerType.User && toPeer.PeerId != input.UserId)
        {
            var blocksCol = mongoDatabase.GetCollection<MongoDB.Bson.BsonDocument>("user-blocks");
            var blockedByThemFilter = MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("_id", $"{toPeer.PeerId}-{input.UserId}");
            var blockedByThem = await blocksCol.Find(blockedByThemFilter).Limit(1).AnyAsync();
            if (blockedByThem) RpcErrors.RpcErrors403.UserIsBlocked.ThrowRpcError();

            var blockedByUsFilter = MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("_id", $"{input.UserId}-{toPeer.PeerId}");
            var blockedByUs = await blocksCol.Find(blockedByUsFilter).Limit(1).AnyAsync();
            if (blockedByUs) RpcErrors.RpcErrors400.YouBlockedUser.ThrowRpcError();
        }

        var (topMsgId, savedPeerId) = await ResolveThreadRoutingAsync(input, toPeer, obj.ReplyTo);
        var channelForMonoforum = toPeer.PeerType == PeerType.Channel
            ? await queryProcessor.ProcessAsync(new GetChannelByIdQuery(toPeer.PeerId))
            : null;
        var isMonoforum = channelForMonoforum?.IsMonoforum == true;
        MonoforumCompatibilityHelper.ValidateSuggestedPostOrThrow(obj.SuggestedPost, isMonoforum);
        long? paidMessageStars = null;
        if (toPeer.PeerType == PeerType.User)
        {
            var targetGps = await privacyAppService.GetGlobalPrivacySettingsAsync(toPeer.PeerId);
            var requiredStars = targetGps?.NoncontactPeersPaidStars ?? 0;
            if (requiredStars > 0)
            {
                // Check if sender has exception (allowed to send without payment)
                var exceptionsCol = mongoDatabase.GetCollection<BsonDocument>("paid_messages_exceptions");
                var exceptionId = $"exception-{toPeer.PeerId}-{input.UserId}";
                var exception = await exceptionsCol.Find(
                    Builders<BsonDocument>.Filter.Eq("_id", exceptionId)
                ).FirstOrDefaultAsync();

                if (exception == null)
                {
                    // No exception - payment required
                    if ((obj.AllowPaidStars ?? 0) < requiredStars)
                        RpcErrors.RpcErrors403.AllowPaymentRequiredX.ThrowRpcError((int)requiredStars);
                    var balance = await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, input.UserId);
                    if (balance < requiredStars)
                        RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();
                    await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, input.UserId, -requiredStars);
                    await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, toPeer.PeerId, requiredStars);
                    // Tag both legs with paidMessages so starsTransaction shows the
                    // "paid message" row rather than a generic transfer.
                    var paidMsgCount = (int)Math.Max(1, requiredStars);
                    await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, input.UserId, -requiredStars, peerUserId: toPeer.PeerId, paidMessages: paidMsgCount);
                    await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, toPeer.PeerId, requiredStars, peerUserId: input.UserId, paidMessages: paidMsgCount);

                    // Save to paid_messages_revenue collection
                    var revenueCol = mongoDatabase.GetCollection<BsonDocument>("paid_messages_revenue");
                    await revenueCol.InsertOneAsync(new BsonDocument
                    {
                        ["ReceiverUserId"] = toPeer.PeerId,
                        ["SenderUserId"] = input.UserId,
                        ["StarsAmount"] = requiredStars,
                        ["MessageId"] = 0, // Will be updated after message is created
                        ["Date"] = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        ["Refunded"] = false,
                        ["ParentPeerId"] = BsonNull.Value
                    });

                    paidMessageStars = requiredStars;
                }
                // else: exception exists - allow free message
            }
        }

        if (isMonoforum && string.IsNullOrEmpty(obj.Message) && media == null)
        {
            RpcErrors.RpcErrors400.MessageEmpty.ThrowRpcError();
        }

        if (toPeer.PeerType == PeerType.Channel)
        {
            var (_, chargedStars) = await MonoforumCompatibilityHelper.TryChargeMonoforumMessageAsync(
                input, toPeer, savedPeerId, obj.AllowPaidStars, queryProcessor, mongoDatabase, objectMessageSender);
            paidMessageStars = chargedStars ?? paidMessageStars;
        }

        // Get TTL period from dialog
        int? ttlPeriod = null;
        var dialogId = DialogId.Create(input.UserId, toPeer.PeerType, toPeer.PeerId);
        var dialogCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-dialogreadmodel");
        var dialogFilter = Builders<BsonDocument>.Filter.Eq("_id", dialogId.Value);
        var dialog = await dialogCollection
            .Find(dialogFilter)
            .Project(Builders<BsonDocument>.Projection.Include("TtlPeriod"))
            .FirstOrDefaultAsync();
        if (dialog != null && dialog.Contains("TtlPeriod"))
        {
            var ttl = dialog["TtlPeriod"];
            if (!ttl.IsBsonNull && ttl.AsInt32 > 0)
            {
                ttlPeriod = ttl.AsInt32;
            }
        }

        var effect = await messageEffectAppService.ValidateEffectAsync(obj.Effect, input.UserId, toPeer.PeerType);

        var sendMessageInput = new SendMessageInput(input.ToRequestInfo(), input.UserId, toPeer, obj.Message, obj.RandomId, obj.Entities, obj.ReplyTo, obj.ClearDraft, media: media, replyMarkup: obj.ReplyMarkup, topMsgId: topMsgId, sendAs: sendAs, effect: effect, inputQuickReplyShortcut: obj.QuickReplyShortcut, silent: obj.Silent, scheduleDate: obj.ScheduleDate, scheduleRepeatPeriod: obj.ScheduleRepeatPeriod, invertMedia: obj.InvertMedia, paidMessageStars: paidMessageStars, ttlPeriod: ttlPeriod, savedPeerId: savedPeerId, suggestedPost: obj.SuggestedPost, noForwards: obj.Noforwards);
        await messageAppService.SendMessageAsync([sendMessageInput]);


        if (toPeer.PeerType == PeerType.User && toPeer.PeerId == BotFatherBotService.BotUserId)
            _ = Task.Run(() => botFatherBotService.HandleMessageAsync(input, input.UserId, obj.Message));
        if (toPeer.PeerType == PeerType.User && toPeer.PeerId == PremiumBotService.BotUserId)
            _ = Task.Run(() => premiumBotService.HandleMessageAsync(input, obj.Message));
        return null !;
    }

    private async Task<TMessageMediaWebPage?> ProcessJoinChatUrlAsync(RequestSendMessage obj)
    {
        var joinChatDomain = options.Value.JoinChatDomain;
        if (string.IsNullOrWhiteSpace(joinChatDomain) ||
            !obj.Message.Contains(joinChatDomain, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var pattern = @"(?:^|\s)(https?://[^\s]+)(?=\s|$)";
        var pattern2 = @$"{Regex.Escape(joinChatDomain)}/\+([\S]{{16}})";
        var matches = Regex.Matches(obj.Message, pattern);
        var isInviteUrlAdded = false;
        TMessageMediaWebPage? media = null;
        foreach (Match match in matches)
        {
            var url = match.Groups[1].Value;
            var m2 = Regex.Match(url, pattern2);
            if (m2.Success && !isInviteUrlAdded)
            {
                var link = m2.Groups[1].Value;
                var chatInvite = await queryProcessor.ProcessAsync(new GetChatInviteByLinkQuery(link));
                if (chatInvite != null)
                {
                    var channelReadModel = await channelAppService.GetAsync(chatInvite.PeerId);
                    // Super group/Public channel
                    if (!channelReadModel.Broadcast || (channelReadModel.Broadcast && !string.IsNullOrEmpty(channelReadModel.UserName)))
                    {
                        media = new TMessageMediaWebPage
                        {
                            Webpage = new Schema.TWebPage
                            {
                                Id = Random.Shared.NextInt64(),
                                Url = $"{joinChatDomain}/+{link}",
                                DisplayUrl = $"{joinChatDomain}/+{link}",
                                Type = channelReadModel.Broadcast ? "telegram_channel" : "telegram_megagroup",
                                SiteName = "MyTelegram",
                                Title = channelReadModel.Title,
                                Description = $"Join this group on MyTelegram.",
                            }
                        };
                    }

                    isInviteUrlAdded = true;
                }
            }
        }

        return media;
    }

    private async Task<(int? TopMsgId, Peer? SavedPeerId)> ResolveThreadRoutingAsync(IRequestInput input, Peer toPeer, IInputReplyTo? replyTo)
    {
        if (replyTo is TInputReplyToMonoForum monoForumReply)
        {
            var savedPeer = await ResolveMonoforumSavedPeerAsync(input, toPeer, monoForumReply.MonoforumPeerId);
            return (null, savedPeer);
        }

        if (replyTo is TInputReplyToMessage { MonoforumPeerId: not null } messageReply)
        {
            var savedPeer = await ResolveMonoforumSavedPeerAsync(input, toPeer, messageReply.MonoforumPeerId);
            return (null, savedPeer);
        }

        if (toPeer.PeerType != PeerType.Channel)
        {
            return (null, null);
        }

        var channelDoc = await GetChannelDocumentAsync(toPeer.PeerId);
        if (channelDoc == null)
        {
            return (null, null);
        }

        if (channelDoc.Contains("IsMonoforum") && channelDoc["IsMonoforum"].AsBoolean)
        {
            return (null, null);
        }

        if (!channelDoc.Contains("Forum") || !channelDoc["Forum"].AsBoolean)
        {
            return (null, null);
        }

        var topicId = ForumTopicHelper.GetRequestedTopicId(replyTo);
        if (!topicId.HasValue)
        {
            return (null, null);
        }

        await ForumTopicHelper.ValidateTopicForSendAsync(mongoDatabase, toPeer.PeerId, topicId.Value);
        if (topicId.Value != ForumTopicHelper.GeneralTopicId &&
            replyTo is TInputReplyToMessage topicReply &&
            topicReply.TopMsgId == null)
        {
            topicReply.TopMsgId = topicId.Value;
        }

        return (topicId.Value == ForumTopicHelper.GeneralTopicId ? null : topicId.Value, null);
    }

    private async Task<Peer> ResolveMonoforumSavedPeerAsync(IRequestInput input, Peer toPeer, IInputPeer monoforumPeerId)
    {
        if (toPeer.PeerType != PeerType.Channel)
        {
            RpcErrors.RpcErrors400.ReplyToMonoforumPeerInvalid.ThrowRpcError();
        }

        var channelDoc = await GetChannelDocumentAsync(toPeer.PeerId);
        if (channelDoc == null || !channelDoc.Contains("IsMonoforum") || !channelDoc["IsMonoforum"].AsBoolean)
        {
            RpcErrors.RpcErrors400.ReplyToMonoforumPeerInvalid.ThrowRpcError();
        }

        var savedPeer = peerHelper.GetPeer(monoforumPeerId, input.UserId);
        if (savedPeer == null || savedPeer.PeerType != PeerType.User)
        {
            RpcErrors.RpcErrors400.ReplyToMonoforumPeerInvalid.ThrowRpcError();
        }

        return savedPeer;
    }

    private async Task<BsonDocument?> GetChannelDocumentAsync(long channelId)
    {
        var collection = mongoDatabase.GetCollection<BsonDocument>("eventflow-channelreadmodel");
        return await collection.Find(Builders<BsonDocument>.Filter.Eq("ChannelId", channelId)).FirstOrDefaultAsync();
    }

}
