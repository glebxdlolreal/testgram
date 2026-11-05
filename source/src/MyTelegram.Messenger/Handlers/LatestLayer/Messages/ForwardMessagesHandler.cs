using MongoDB.Driver;
using MyTelegram.Domain.Shared;
using MyTelegram.Messenger.Handlers.LatestLayer.Payments;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using StackExchange.Redis;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Forwards messages by their IDs.
/// Possible errors
/// Code Type Description
/// 406 ALLOW_PAYMENT_REQUIRED This peer only accepts <a href="https://corefork.telegram.org/api/paid-messages">paid messages »</a>: this error is only emitted for older layers without paid messages support, so the client must be updated in order to use paid messages.  .
/// 403 ALLOW_PAYMENT_REQUIRED_%d This peer charges %d <a href="https://corefork.telegram.org/api/stars">Telegram Stars</a> per message, but the <code>allow_paid_stars</code> was not set or its value is smaller than %d.
/// 400 BROADCAST_PUBLIC_VOTERS_FORBIDDEN You can't forward polls with public voters.
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 406 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 403 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// 406 CHAT_FORWARDS_RESTRICTED You can't forward messages from a protected chat.
/// 403 CHAT_GUEST_SEND_FORBIDDEN You join the discussion group before commenting, see <a href="https://corefork.telegram.org/api/discussion#requiring-users-to-join-the-group">here »</a> for more info.
/// 400 CHAT_ID_INVALID The provided chat id is invalid.
/// 400 CHAT_RESTRICTED You can't send messages in this chat, you were restricted.
/// 403 CHAT_SEND_AUDIOS_FORBIDDEN You can't send audio messages in this chat.
/// 403 CHAT_SEND_DOCS_FORBIDDEN You can't send documents in this chat.
/// 403 CHAT_SEND_GAME_FORBIDDEN You can't send a game to this chat.
/// 403 CHAT_SEND_GIFS_FORBIDDEN You can't send gifs in this chat.
/// 403 CHAT_SEND_INLINE_FORBIDDEN You can't send inline messages in this group.
/// 403 CHAT_SEND_MEDIA_FORBIDDEN You can't send media in this chat.
/// 403 CHAT_SEND_PHOTOS_FORBIDDEN You can't send photos in this chat.
/// 403 CHAT_SEND_PLAIN_FORBIDDEN You can't send non-media (text) messages in this chat.
/// 403 CHAT_SEND_POLL_FORBIDDEN You can't send polls in this chat.
/// 403 CHAT_SEND_STICKERS_FORBIDDEN You can't send stickers in this chat.
/// 403 CHAT_SEND_VIDEOS_FORBIDDEN You can't send videos in this chat.
/// 403 CHAT_SEND_VOICES_FORBIDDEN You can't send voice recordings in this chat.
/// 403 CHAT_SEND_WEBPAGE_FORBIDDEN You can't send webpage previews to this chat.
/// 403 CHAT_WRITE_FORBIDDEN You can't write in this chat.
/// 400 GROUPED_MEDIA_INVALID Invalid grouped media.
/// 400 INPUT_USER_DEACTIVATED The specified user was deleted.
/// 400 MEDIA_EMPTY The provided media object is invalid.
/// 400 MESSAGE_IDS_EMPTY No message ids were provided.
/// 400 MESSAGE_ID_INVALID The provided message id is invalid.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// 500 NEED_DOC_INVALID  
/// 406 PAYMENT_UNSUPPORTED A detailed description of the error will be received separately as described <a href="https://corefork.telegram.org/api/errors#406-not-acceptable">here »</a>.
/// 406 PEER_ID_INVALID The provided peer id is invalid.
/// 403 PREMIUM_ACCOUNT_REQUIRED A premium account is required to execute this action.
/// 406 PRIVACY_PREMIUM_REQUIRED You need a <a href="https://corefork.telegram.org/api/premium">Telegram Premium subscription</a> to send a message to this user.
/// 400 QUICK_REPLIES_BOT_NOT_ALLOWED <a href="https://corefork.telegram.org/api/business#quick-reply-shortcuts">Quick replies</a> cannot be used by bots.
/// 400 QUICK_REPLIES_TOO_MUCH A maximum of <a href="https://corefork.telegram.org/api/config#quick-replies-limit">appConfig.<code>quick_replies_limit</code></a> shortcuts may be created, the limit was reached.
/// 400 QUIZ_ANSWER_MISSING You can forward a quiz while hiding the original author only after choosing an option in the quiz.
/// 500 RANDOM_ID_DUPLICATE You provided a random ID that was already used.
/// 400 RANDOM_ID_INVALID A provided random ID is invalid.
/// 400 REPLY_MESSAGES_TOO_MUCH Each shortcut can contain a maximum of <a href="https://corefork.telegram.org/api/config#quick-reply-messages-limit">appConfig.<code>quick_reply_messages_limit</code></a> messages, the limit was reached.
/// 400 REPLY_TO_MONOFORUM_PEER_INVALID The specified inputReplyToMonoForum.monoforum_peer_id is invalid.
/// 400 SCHEDULE_BOT_NOT_ALLOWED Bots cannot schedule messages.
/// 400 SCHEDULE_DATE_TOO_LATE You can't schedule a message this far in the future.
/// 400 SCHEDULE_TOO_MUCH There are too many scheduled messages.
/// 400 SEND_AS_PEER_INVALID You can't send messages as the specified peer.
/// 400 SLOWMODE_MULTI_MSGS_DISABLED Slowmode is enabled, you cannot forward multiple messages to this group.
/// 420 SLOWMODE_WAIT_%d Slowmode is enabled in this chat: wait %d seconds before sending another message to this chat.
/// 400 SUGGESTED_POST_PEER_INVALID You cannot send suggested posts to non-<a href="https://corefork.telegram.org/api/monoforum">monoforum</a> peers.
/// 406 TOPIC_CLOSED This topic was closed, you can't send messages to it anymore.
/// 406 TOPIC_DELETED The specified topic was deleted.
/// 400 USER_BANNED_IN_CHANNEL You're banned from sending messages in supergroups/channels.
/// 403 USER_IS_BLOCKED You were blocked by this user.
/// 400 USER_IS_BOT Bots can't send messages to other bots.
/// 403 VOICE_MESSAGES_FORBIDDEN This user's privacy settings forbid you from sending voice messages.
/// 400 YOU_BLOCKED_USER You blocked this user.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.forwardMessages"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
public sealed class ForwardMessagesHandler(ICommandBus commandBus, IPeerHelper peerHelper, IChannelAppService channelAppService, IMessageAppService messageAppService, IQueryProcessor queryProcessor, IMongoDatabase mongoDatabase, IMessageEncryptionHelper messageEncryptionHelper, IBlockCacheAppService blockCacheAppService, IPrivacyAppService privacyAppService, IUserAppService userAppService, IMessageEffectAppService messageEffectAppService, ITopPeerUsageRecorder topPeerUsageRecorder, MyTelegram.Messenger.Services.Bots.IBotBroadcastLimiter botBroadcastLimiter) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestForwardMessages, MyTelegram.Schema.IUpdates>
{
    public async Task<IUpdates> HandleAsync(IRequestInput input, RequestForwardMessages obj)
    {
        return await HandleCoreAsync(input, obj);
    }

    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestForwardMessages obj)
    {

        var fromPeer = peerHelper.GetPeer(obj.FromPeer, input.UserId);
        var toPeer = peerHelper.GetPeer(obj.ToPeer, input.UserId);
        var sendAs = peerHelper.GetPeer(obj.SendAs, input.UserId);
        messageAppService.CheckBotPermission(input.UserId, toPeer);

        // The documented USER_IS_BLOCKED / YOU_BLOCKED_USER errors above were unreachable:
        // forwarding was the one send path that never consulted the blocklist, so a block
        // could be bypassed simply by forwarding instead of sending. Checked in both
        // directions, matching SendMessageHandler / SendMediaHandler.
        if (toPeer.PeerType == PeerType.User && toPeer.PeerId != input.UserId)
        {
            if (await blockCacheAppService.IsBlockedAsync(toPeer.PeerId, input.UserId))
                RpcErrors.RpcErrors403.UserIsBlocked.ThrowRpcError();
            if (await blockCacheAppService.IsBlockedAsync(input.UserId, toPeer.PeerId))
                RpcErrors.RpcErrors400.YouBlockedUser.ThrowRpcError();
        }

        var post = false;
        IChannelReadModel? targetChannelReadModel = null;
        var isMonoforum = false;

        if (toPeer.PeerType == PeerType.Channel)
        {
            targetChannelReadModel = await channelAppService.GetAsync(toPeer.PeerId);
            isMonoforum = targetChannelReadModel?.IsMonoforum == true;

            if (!isMonoforum)
            {
                await messageAppService.CheckSendAsAsync(input.UserId, toPeer, sendAs);
                post = targetChannelReadModel!.Broadcast;
                if (targetChannelReadModel.Broadcast || targetChannelReadModel.LinkedChatId != null && sendAs == null)
                {
                    if (await messageAppService.CanSendAsPeerAsync(targetChannelReadModel.ChannelId, input.UserId))
                    {
                        var defaultSendAs = await queryProcessor.ProcessAsync(new GetUserConfigByKeyQuery(input.UserId, ((int)UserConfigType.SendAsPeer).ToString()));
                        if (defaultSendAs != null && long.TryParse(defaultSendAs.Value, out var sendAsPeerId))
                        {
                            var tempSendAs = peerHelper.GetPeer(sendAsPeerId);
                            if (await messageAppService.IsValidSendAsPeerAsync(input.UserId, toPeer, tempSendAs))
                            {
                                sendAs = tempSendAs;
                            }
                        }

                        sendAs ??= toPeer;
                    }
                }

                if (targetChannelReadModel.CreatorId != input.UserId)
                {
                    var admin = targetChannelReadModel.AdminList.FirstOrDefault(p => p.UserId == input.UserId);
                    if (post)
                    {
                        if (admin == null || !admin.AdminRights.PostMessages)
                        {
                            RpcErrors.RpcErrors403.ChatWriteForbidden.ThrowRpcError();
                        }
                    }
                    else if (admin == null)
                    {
                        // Admins are not subject to the chat defaults; the granular rights of a
                        // plain member are enforced per message in MessageAppService.
                        var bannedDefaultRights = targetChannelReadModel.DefaultBannedRights ?? ChatBannedRights.CreateDefaultBannedRights();
                        if (bannedDefaultRights.SendMessages)
                        {
                            RpcErrors.RpcErrors403.ChatWriteForbidden.ThrowRpcError();
                        }
                    }
                }
            }
            else
            {
                sendAs = null;
            }
        }

        MonoforumCompatibilityHelper.ValidateSuggestedPostOrThrow(obj.SuggestedPost, isMonoforum);
        var targetNoForwards = targetChannelReadModel?.NoForwards == true;
        if (!targetNoForwards && toPeer.PeerType == PeerType.Chat)
        {
            targetNoForwards = (await channelAppService.GetAsync(toPeer.PeerId))?.NoForwards == true;
        }
        if (!targetNoForwards && toPeer.PeerType == PeerType.User)
        {
            targetNoForwards = await IsPrivateChatNoForwardsEnabledAsync(input.UserId, toPeer.PeerId);
        }

        // Loaded before charging for paid messages: the voice-note privacy check below can
        // reject the whole request, and doing that after stars have been deducted would
        // leave the sender paying for a forward that never happened.
        var ownerPeerId = fromPeer.PeerType == PeerType.User ? input.UserId : fromPeer.PeerId;
        var messagesToCheck = await queryProcessor.ProcessAsync(new GetMessagesQuery(
            ownerPeerId,
            MessageType.Unknown,
            null,
            obj.Id.ToList(),
            0,
            0,
            null,
            null,
            0,
            0,
            0,
            null,
            null,
            false,
            false,
            false,
            null,
            0
        ));

        // privacyKeyVoiceMessages was enforced on the send paths only, so forwarding was a
        // way to deliver a voice note to someone who disallowed them.
        if (toPeer.PeerType == PeerType.User
            && toPeer.PeerId != input.UserId
            && messagesToCheck.Any(VoiceMessageHelper.IsVoiceMessage))
        {
            await privacyAppService.ApplyPrivacyAsync(input.UserId, toPeer.PeerId,
                _ => RpcErrors.RpcErrors403.VoiceMessagesForbidden.ThrowRpcError(),
                [PrivacyType.VoiceMessages]);
        }

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


        foreach (var msg in messagesToCheck)
        {
            if (msg.SendMessageType == SendMessageType.MessageService)
            {
                RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
            }
        }

        await ThrowIfForwardingRestrictedAsync(input.UserId, fromPeer, messagesToCheck);

        var fromNames = await BuildAnonymousFromNamesAsync(input.UserId, messagesToCheck);

        // Bots: the broadcast limit, and the paid messages over it (allow_paid_floodskip). Checked before
        // the paths split: the saga path returns without reaching the code below.
        await botBroadcastLimiter.CheckAsync(input.UserId, obj.Id.Count, obj.AllowPaidFloodskip);

        // The forward saga has no notion of a schedule queue, so a scheduled forward is built here and
        // handed to the same path a scheduled sendMessage takes.
        // See https://corefork.telegram.org/api/scheduled-messages
        if (!isMonoforum && !obj.ScheduleDate.HasValue)
        {
            var command = new StartForwardMessagesCommand(TempId.New, input.ToRequestInfo(), obj.Silent, obj.Background, obj.WithMyScore, obj.DropAuthor, obj.DropMediaCaptions, obj.Noforwards || targetNoForwards, fromPeer, toPeer, obj.Id.ToList(), obj.RandomId.ToList(), obj.ScheduleDate, sendAs, false, post, fromNames);
            await commandBus.PublishAsync(command);
            await RecordForwardUsageAsync(input, toPeer);

            return null!;
        }

        Peer? savedPeerId = null;
        if (isMonoforum)
        {
            savedPeerId = await ResolveMonoforumSavedPeerAsync(toPeer, obj.ReplyTo, input.UserId);
            var (_, chargedStars) = await MonoforumCompatibilityHelper.TryChargeMonoforumMessageAsync(
                input,
                toPeer,
                savedPeerId,
                obj.AllowPaidStars,
                queryProcessor,
                mongoDatabase);
            paidMessageStars = chargedStars ?? paidMessageStars;
        }

        var messageMap = messagesToCheck.ToDictionary(m => m.SenderMessageId == 0 ? int.Parse(m.Id.Split('-', StringSplitOptions.RemoveEmptyEntries).Last()) : m.SenderMessageId);
        var inputs = new List<SendMessageInput>();
        var effect = await messageEffectAppService.ValidateEffectAsync(obj.Effect, input.UserId, toPeer.PeerType);
        var seenMessageIds = new HashSet<int>();
        for (var i = 0; i < obj.Id.Count; i++)
        {
            var sourceMessageId = obj.Id[i];
            if (!seenMessageIds.Add(sourceMessageId))
            {
                continue;
            }

            if (!messageMap.TryGetValue(sourceMessageId, out var sourceMessage))
            {
                RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
            }

            if (sourceMessage.MessageType == MessageType.Poll || sourceMessage.Media2 is TMessageMediaPoll)
            {
                RpcErrors.RpcErrors403.ChatSendPollForbidden.ThrowRpcError();
            }

            var media = sourceMessage.Media2 ?? sourceMessage.Media?.ToTObject<IMessageMedia>();
            var messageText = obj.DropMediaCaptions ? string.Empty : sourceMessage.Message;
            if (!obj.DropMediaCaptions &&
                string.IsNullOrEmpty(messageText) &&
                sourceMessage.EncryptedData?.Length > 0)
            {
                messageText = messageEncryptionHelper.Decrypt(
                    sourceMessage.OwnerPeerId,
                    sourceMessage.MessageId,
                    sourceMessage.EncryptedData.Value);
            }

            if (string.IsNullOrEmpty(messageText) && media == null)
            {
                continue;
            }

            var fwdHeader = obj.DropAuthor ? null : BuildForwardHeader(fromPeer, sourceMessage, sourceMessageId);
            var sendInput = new SendMessageInput(
                input.ToRequestInfo(),
                input.UserId,
                toPeer,
                messageText,
                obj.RandomId[i],
                sourceMessage.Entities2,
                obj.ReplyTo,
                media: media,
                sendMessageType: media == null ? SendMessageType.Text : SendMessageType.Media,
                messageType: sourceMessage.MessageType,
                topMsgId: obj.TopMsgId,
                sendAs: isMonoforum ? null : sendAs,
                inputQuickReplyShortcut: obj.QuickReplyShortcut,
                effect: effect,
                silent: obj.Silent,
                scheduleDate: obj.ScheduleDate,
                // "schedule_repeat_period can only be used when sending/forwarding a single message"
                scheduleRepeatPeriod: obj.Id.Count == 1 ? obj.ScheduleRepeatPeriod : null,
                paidMessageStars: paidMessageStars,
                savedPeerId: savedPeerId,
                suggestedPost: inputs.Count == 0 ? obj.SuggestedPost : null,
                fwdHeader: fwdHeader,
                messageSubType: MessageSubType.ForwardMessage,
                postAuthor: sourceMessage.PostAuthor,
                views: MessageForwardViewsHelper.ResolveForwardedViews(post, fwdHeader),
                pollId: sourceMessage.PollId,
                invertMedia: sourceMessage.InvertMedia,
                noForwards: obj.Noforwards || targetNoForwards
            );
            inputs.Add(sendInput);
        }

        if (inputs.Count == 0)
        {
            return null!;
        }

        await messageAppService.SendMessageAsync(inputs);
        await RecordForwardUsageAsync(input, toPeer);

        return null!;
    }

    /// <summary>
    /// One use per forward action, not per message: tdlib counts the action too, and a 100-message
    /// forward is one decision about where to send it. Recorded here rather than inferred from
    /// <c>messageReadModel.FwdHeader</c>, which <c>drop_author</c> nulls out. Both dispatch paths have to
    /// call it — the ordinary forward leaves through the saga command above and never reaches the
    /// scheduled/monoforum tail.
    /// See https://corefork.telegram.org/api/top-rating
    /// </summary>
    private Task RecordForwardUsageAsync(IRequestInput input, Peer toPeer)
    {
        return topPeerUsageRecorder.RecordForwardAsync(input.UserId, toPeer.PeerType, toPeer.PeerId);
    }

    /// <summary>
    /// Original senders whose <c>privacyKeyForwards</c> rule disallows the forwarder from
    /// linking back to them, mapped to the plain name to show instead.
    /// See https://corefork.telegram.org/api/privacy
    /// </summary>
    /// <remarks>
    /// The forward saga already knows how to anonymise an author: when a name is present it
    /// clears <c>fwd_from.from_id</c> and attributes the message to the anonymous peer. It
    /// just never received any names, so the rule had no effect anywhere.
    /// </remarks>
    private async Task<Dictionary<long, string>?> BuildAnonymousFromNamesAsync(
        long selfUserId,
        IReadOnlyCollection<IMessageReadModel> messages)
    {
        var senderUserIds = messages
            .Select(p => p.SenderUserId)
            .Where(p => p > 0 && p != selfUserId)
            .Distinct()
            .ToList();

        if (senderUserIds.Count == 0)
        {
            return null;
        }

        var restrictedUserIds = new List<long>();
        await privacyAppService.ApplyPrivacyListAsync(selfUserId, senderUserIds,
            (_, restrictedUserId) => restrictedUserIds.Add(restrictedUserId),
            [PrivacyType.Forwards]);

        if (restrictedUserIds.Count == 0)
        {
            return null;
        }

        var fromNames = new Dictionary<long, string>();
        foreach (var userReadModel in await userAppService.GetListAsync(restrictedUserIds.Distinct().ToList()))
        {
            var name = $"{userReadModel.FirstName} {userReadModel.LastName}".Trim();
            if (name.Length > 0)
            {
                fromNames[userReadModel.UserId] = name;
            }
        }

        return fromNames.Count == 0 ? null : fromNames;
    }

    private async Task ThrowIfForwardingRestrictedAsync(
        long selfUserId,
        Peer fromPeer,
        IReadOnlyCollection<IMessageReadModel> messages)
    {
        if (messages.Any(m => m.NoForwards))
        {
            RpcErrors.RpcErrors406.ChatForwardsRestricted.ThrowRpcError();
        }

        if (fromPeer.PeerType == PeerType.User &&
            await IsPrivateChatForwardingRestrictedAsync(selfUserId, fromPeer.PeerId, messages))
        {
            RpcErrors.RpcErrors406.ChatForwardsRestricted.ThrowRpcError();
        }

        if (fromPeer.PeerType is not (PeerType.Channel or PeerType.Chat))
        {
            return;
        }

        var sourceChannel = await channelAppService.GetAsync(fromPeer.PeerId);
        if (sourceChannel?.NoForwards == true)
        {
            RpcErrors.RpcErrors406.ChatForwardsRestricted.ThrowRpcError();
        }
    }

    private async Task<bool> IsPrivateChatForwardingRestrictedAsync(
        long selfUserId,
        long otherUserId,
        IReadOnlyCollection<IMessageReadModel> messages)
    {
        foreach (var senderUserId in messages.Select(x => x.SenderPeerId).Distinct())
        {
            var peerUserId = senderUserId == selfUserId ? otherUserId : selfUserId;
            if (await IsPrivateChatNoForwardsEnabledAsync(senderUserId, peerUserId))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> IsPrivateChatNoForwardsEnabledAsync(long ownerUserId, long peerUserId)
    {
        var collection = mongoDatabase.GetCollection<PrivateChatNoForwardsDocument>("private_chat_noforwards");
        var filter = Builders<PrivateChatNoForwardsDocument>.Filter.And(
            Builders<PrivateChatNoForwardsDocument>.Filter.Eq(x => x.OwnerUserId, ownerUserId),
            Builders<PrivateChatNoForwardsDocument>.Filter.Eq(x => x.PeerUserId, peerUserId),
            Builders<PrivateChatNoForwardsDocument>.Filter.Eq(x => x.Enabled, true));

        return await collection.Find(filter).AnyAsync();
    }

    private async Task<Peer?> ResolveMonoforumSavedPeerAsync(Peer toPeer, IInputReplyTo? replyTo, long selfUserId)
    {
        if (toPeer.PeerType != PeerType.Channel)
            return null;

        var monoforum = await queryProcessor.ProcessAsync(new GetChannelByIdQuery(toPeer.PeerId));
        if (monoforum == null || !monoforum.IsMonoforum)
            return null;

        IInputPeer? targetPeer = replyTo switch
        {
            TInputReplyToMonoForum monoForumReply => monoForumReply.MonoforumPeerId,
            TInputReplyToMessage { MonoforumPeerId: not null } replyToMessage => replyToMessage.MonoforumPeerId,
            _ => null
        };

        if (targetPeer == null)
            return null;

        var savedPeer = peerHelper.GetPeer(targetPeer, selfUserId);
        if (savedPeer is not { PeerType: PeerType.User })
            RpcErrors.RpcErrors400.ReplyToMonoforumPeerInvalid.ThrowRpcError();

        return savedPeer;
    }

    private static MessageFwdHeader BuildForwardHeader(Peer fromPeer, IMessageReadModel message, int messageId)
    {
        if (message.FwdHeader is { Imported: true } importedHeader)
        {
            return BuildImportedForwardHeader(fromPeer, message, messageId, importedHeader);
        }

        if (message.FwdHeader != null)
        {
            return CloneForwardHeader(message.FwdHeader);
        }

        var sourcePeer = fromPeer.PeerType == PeerType.Self ? new Peer(PeerType.User, fromPeer.PeerId) : fromPeer;
        var fromId = new Peer(PeerType.User, message.SenderPeerId);
        var channelPost = default(int?);
        if (message.ToPeerType == PeerType.Channel)
        {
            var channelPeer = new Peer(PeerType.Channel, message.ToPeerId);
            fromId = message.Post || message.SendAs != null ? channelPeer : fromId;
            channelPost = message.Post ? messageId : null;
        }

        return new MessageFwdHeader
        {
            Imported = false,
            SavedOut = false,
            FromId = fromId,
            FromName = null,
            ChannelPost = channelPost,
            PostAuthor = message.PostAuthor,
            Date = message.Date,
            SavedFromPeer = sourcePeer,
            SavedFromMsgId = messageId,
            SavedFromId = null,
            SavedFromName = null,
            SavedDate = null,
            PsaType = null,
            ForwardFromLinkedChannel = false
        };
    }

    /// <summary>
    /// Forward of a message that was imported from a foreign chat app. The copy keeps the original
    /// author name and the original date, but it is a plain forward from a hidden sender: only the
    /// imported message itself carries the <c>imported</c> flag, and a client that sees it there stops
    /// drawing the forward header entirely. The sender of the source message — the chat importer
    /// account — is reported in <c>saved_from_id</c>, as the official server does.
    /// See https://corefork.telegram.org/api/import
    /// </summary>
    private static MessageFwdHeader BuildImportedForwardHeader(Peer fromPeer, IMessageReadModel message,
        int messageId, MessageFwdHeader importedHeader)
    {
        var sourcePeer = fromPeer.PeerType == PeerType.Self ? new Peer(PeerType.User, fromPeer.PeerId) : fromPeer;

        var header = new MessageFwdHeader
        {
            SavedOut = false,
            ChannelPost = null,
            PostAuthor = null,
            SavedFromPeer = sourcePeer,
            SavedFromMsgId = messageId,
            SavedFromName = null,
            SavedDate = message.Date,
            PsaType = null,
            ForwardFromLinkedChannel = false
        };

        MessageFwdHeaderRules.TryApplyImportedOrigin(header, importedHeader,
            new Peer(PeerType.User, message.SenderPeerId));

        return header;
    }

    private static MessageFwdHeader CloneForwardHeader(MessageFwdHeader source)
    {
        return new MessageFwdHeader
        {
            Imported = source.Imported,
            SavedOut = source.SavedOut,
            FromId = source.FromId,
            FromName = source.FromName,
            ChannelPost = source.ChannelPost,
            PostAuthor = source.PostAuthor,
            Date = source.Date,
            SavedFromPeer = source.SavedFromPeer,
            SavedFromMsgId = source.SavedFromMsgId,
            SavedFromId = source.SavedFromId,
            SavedFromName = source.SavedFromName,
            SavedDate = source.SavedDate,
            PsaType = source.PsaType,
            ForwardFromLinkedChannel = source.ForwardFromLinkedChannel
        };
    }
}
