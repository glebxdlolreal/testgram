using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;

namespace MyTelegram.Messenger.Services.Impl;

public class MessageAppService(
    IQueryProcessor queryProcessor,
    ICommandBus commandBus,
    IObjectMapper objectMapper,
    IPeerHelper peerHelper,
    IPhotoAppService photoAppService,
    IChannelAppService channelAppService,
    IUserAppService userAppService,
    IPrivacyAppService privacyAppService,
    IContactAppService contactAppService,
    IOffsetHelper offsetHelper,
	IDataEncryptionHelper dataEncryptionHelper,
    IOptionsMonitor<MyTelegramMessengerServerOptions> options,
    IIdGenerator idGenerator,
    IMongoDatabase mongoDatabase,
    IObjectMessageSender objectMessageSender)
    : BaseAppService, IMessageAppService, ITransientDependency
{
    private const string HashtagPattern = "#(\\w+)";
    private const string UrlPattern = @"(?:^|\s)((https?:\/\/)?[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}(\/[^\s,.:;!?]*)?)";

    private static byte[]? _encryptionKey;
    private static KeyConfig? _keyConfig;

    public void CheckBotPermission(long requestUserId, Peer toPeer)
    {
        if (peerHelper.IsBotUser(requestUserId) && peerHelper.IsBotUser(toPeer.PeerId))
        {
            RpcErrors.RpcErrors400.UserIsBot.ThrowRpcError();
        }
    }

    public async Task<bool> CanSendAsPeerAsync(long channelId, long userId)
    {
        var channelReadModel = await channelAppService.GetAsync(channelId);
        var canSendAsPeer = false;

        // Channel: signature: true and hasAdminRights: true and canWriteToChat: true
        if (channelReadModel is { Broadcast: true, Signatures: true })
        {
            var channelAdmin = channelReadModel.AdminList.FirstOrDefault(p => p.UserId == userId);
            if (channelReadModel.CreatorId == userId || (channelAdmin?.AdminRights.PostMessages ?? false))
            {
                canSendAsPeer = true;
            }
        }

        if (!canSendAsPeer)
        {
            // Super group with linked channel/Public super group
            if (channelReadModel.MegaGroup && (!string.IsNullOrEmpty(channelReadModel.UserName) ||
                                               channelReadModel.LinkedChatId != null))
            {
                canSendAsPeer = true;
            }
        }

        return canSendAsPeer;
    }

    public async Task<bool> IsValidSendAsPeerAsync(long requestUserId, Peer toPeer, Peer? sendAsPeer)
    {
        if (sendAsPeer != null)
        {
            if (toPeer.PeerType != PeerType.Channel)
            {
                return false;
            }

            switch (sendAsPeer.PeerType)
            {
                case PeerType.User:
                case PeerType.Self:
                    if (sendAsPeer.PeerId != requestUserId)
                    {
                        return false;
                    }

                    break;

                case PeerType.Channel:
                    var canSendAsPeer = await CanSendAsPeerAsync(toPeer.PeerId, requestUserId);
                    if (!canSendAsPeer)
                    {
                        return false;
                    }

                    var sendAsChannelReadModel = await channelAppService.GetAsync(sendAsPeer.PeerId);

                    // We can only use the public channels created by the current user as SendAsPeer
                    if (sendAsChannelReadModel == null! ||
                        sendAsChannelReadModel.CreatorId != requestUserId ||
                        (string.IsNullOrEmpty(sendAsChannelReadModel.UserName) &&
                         sendAsChannelReadModel.LinkedChatId != toPeer.PeerId &&
                         sendAsChannelReadModel.ChannelId != toPeer.PeerId))
                    {
                        return false;
                    }

                    break;
            }
        }

        return true;
    }

    public async Task CheckSendAsAsync(long requestUserId, Peer toPeer, Peer? sendAsPeer)
    {
        var isValid = await IsValidSendAsPeerAsync(requestUserId, toPeer, sendAsPeer);
        if (!isValid)
        {
            RpcErrors.RpcErrors400.SendAsPeerInvalid.ThrowRpcError();
        }
    }

    public async Task<GetMessageOutput> GetChannelDifferenceAsync(GetDifferenceInput input)
    {
        return await GetMessagesInternalAsync(new GetMessagesQuery(input.OwnerPeerId,
            MessageType.Unknown,
            null,
            input.MessageIds,
            0,
            input.Limit,
            null,
            null,
            input.SelfUserId,
            input.Pts), input.Users, input.Chats);
    }

    public Task<GetMessageOutput> GetDifferenceAsync(GetDifferenceInput input)
    {
        return GetMessagesInternalAsync(new GetMessagesQuery(input.OwnerPeerId,
            MessageType.Unknown,
            null,
            null,
            0,
            input.Limit,
            null,
            null,
            input.SelfUserId,
            input.Pts), input.Users, input.Chats);
    }

    public Task<GetMessageOutput> GetHistoryAsync(GetHistoryInput input)
    {
        return GetMessagesCoreAsync(input);
    }

    public Task<GetMessageOutput> GetMessagesAsync(GetMessagesInput input)
    {
        return GetMessagesCoreAsync(input);
    }

    public Task<GetMessageOutput> GetRepliesAsync(GetRepliesInput input)
    {
        return GetMessagesCoreAsync(input);
    }

    public Task<GetMessageOutput> SearchAsync(SearchInput input)
    {
        return GetMessagesCoreAsync(input);
    }

    public Task<GetMessageOutput> SearchGlobalAsync(SearchGlobalInput input)
    {
        return GetMessagesCoreAsync(input);
    }
    public async Task SendMessageAsync(List<SendMessageInput> inputs)
    {
        if (inputs.Count == 0)
        {
            throw new ArgumentException();
        }

        List<SendMessageItem> sendMessageItems = [];
        List<SendMessageItem> scheduledItems = [];
        var firstInput = inputs.First();
        var requestInfo = firstInput.RequestInfo;

        foreach (var input in inputs)
        {
            CheckBotPermission(input.RequestInfo.UserId, input.ToPeer);
            var item = await CreateSendMessageItemAsync(input);

            // Separate scheduled messages from immediate messages
            if (item.MessageItem.ScheduleDate.HasValue)
            {
                scheduledItems.Add(item);
            }
            else
            {
                sendMessageItems.Add(item);
            }
        }

        // Save scheduled messages to MongoDB
        if (scheduledItems.Count > 0)
        {
            await SaveScheduledMessagesAsync(scheduledItems, requestInfo);
        }

        // Send immediate messages through command bus
        if (sendMessageItems.Count > 0)
        {
            var command = new StartSendMessageCommand(TempId.New, requestInfo,
                sendMessageItems,
                firstInput.ClearDraft,
                firstInput.IsSendGroupedMessage,
                firstInput.IsSendQuickReplyMessage);

            await commandBus.PublishAsync(command);
        }
    }

    public async Task<SearchPostsResult> SearchPostsAsync(long selfUserId, SearchPostsQuery searchPostsQuery)
    {
        var messageReadModels = await queryProcessor.ProcessAsync(searchPostsQuery);
        HashSet<long> userIds = [];
        HashSet<long> channelIds = [];
        AddExtraPeerIds(messageReadModels, userIds, channelIds);
        var userIdList = userIds.ToList();
        var userReadModels = await userAppService.GetListAsync(userIdList);
        var channelReadModels = channelIds.Count == 0
            ? []
            : await channelAppService.GetListAsync(channelIds);
        var channelMemberReadModels = channelReadModels.Count == 0
            ? []
            : await queryProcessor.ProcessAsync(
                new GetChannelMemberListByChannelIdListQuery(selfUserId, channelIds.ToList()));
        var photoReadModels = await photoAppService.GetPhotosAsync(channelReadModels);

        return new SearchPostsResult(messageReadModels, channelReadModels, channelMemberReadModels, photoReadModels, userReadModels);
    }

    private async Task<IChannelReadModel?> CheckChannelBannedRightsAsync(SendMessageInput input)
    {
        if (input.ToPeer.PeerType != PeerType.Channel)
        {
            return null;
        }

        var channelReadModel = await channelAppService.GetAsync(input.ToPeer.PeerId);
        if (channelReadModel!.Broadcast)
        {
            // Service messages (gifts etc.) bypass admin check
            if (input.SendMessageType != SendMessageType.MessageService)
            {
                var admin = channelReadModel.AdminList.FirstOrDefault(p => p.UserId == input.SenderUserId);
                if (admin == null || !admin.AdminRights.PostMessages)
                {
                    RpcErrors.RpcErrors403.ChatWriteForbidden.ThrowRpcError();
                }
            }
        }

        var bannedDefaultRights = channelReadModel.DefaultBannedRights ?? ChatBannedRights.CreateDefaultBannedRights();
        if (bannedDefaultRights.SendMessages)
        {
            RpcErrors.RpcErrors403.ChatWriteForbidden.ThrowRpcError();
        }

        // send_polls is a right of its own: a group can allow messages and media while
        // still forbidding polls. Checked for everyone via the chat default, and again
        // per-member below.
        if (bannedDefaultRights.SendPolls && IsPoll(input))
        {
            RpcErrors.RpcErrors403.ChatSendPollForbidden.ThrowRpcError();
        }

        var channelMemberReadModel =
            await queryProcessor.ProcessAsync(new GetChannelMemberByUserIdQuery(channelReadModel.ChannelId,
                input.SenderUserId));


        if (channelMemberReadModel == null && input.SendMessageType != SendMessageType.MessageService)
        {
            if (channelReadModel is { Broadcast: false, LinkedChatId: not null, JoinToSend: false } || channelReadModel.IsMonoforum)
            {
                // Auto-join the sender into the monoforum WITHOUT emitting a join service message.
                // We publish CreateChannelMemberCommand directly (bypassing JoinChannelSaga which
                // is what normally emits messageActionChatAddUser for non-broadcast channels).
                if (channelReadModel.IsMonoforum)
                {
                    await AutoJoinMonoforumSilentlyAsync(channelReadModel, input);
                }
            }
            else
            {
                RpcErrors.RpcErrors403.ChatGuestSendForbidden.ThrowRpcError();
            }
        }

        if (channelReadModel.IsMonoforum &&
            input.SavedPeerId != null &&
            input.SavedPeerId.PeerType == PeerType.User &&
            input.SavedPeerId.PeerId != input.SenderUserId)
        {
            var linkedChannelReadModel = channelReadModel.LinkedMonoforumId.HasValue
                ? await channelAppService.GetAsync(channelReadModel.LinkedMonoforumId.Value)
                : null;
            var admin = linkedChannelReadModel?.AdminList.FirstOrDefault(p => p.UserId == input.SenderUserId);
            if (admin == null || !admin.AdminRights.ManageDirectMessages)
            {
                RpcErrors.RpcErrors403.ChatAdminRequired.ThrowRpcError();
            }
        }

        if (channelMemberReadModel != null && channelMemberReadModel.BannedRights != 0)
        {
            var memberBannedRights =
                ChatBannedRights.FromValue(channelMemberReadModel.BannedRights, channelMemberReadModel.UntilDate);
            if (!string.IsNullOrEmpty(input.Message))
            {
                if (memberBannedRights.SendMessages)
                {
                    RpcErrors.RpcErrors400.UserBannedInChannel.ThrowRpcError();
                }
            }

            if (input.Media != null)
            {
                if (memberBannedRights.SendMedia)
                {
                    RpcErrors.RpcErrors400.UserBannedInChannel.ThrowRpcError();
                }
            }

            if (memberBannedRights.SendPolls && IsPoll(input))
            {
                RpcErrors.RpcErrors403.ChatSendPollForbidden.ThrowRpcError();
            }
        }

        //if (channelReadModel.SlowModeEnabled)
        //{

        //}

        return channelReadModel;
    }

    /// <summary>
    /// True when the message being sent is a poll, so the <c>send_polls</c> banned right
    /// applies. Covers both freshly created polls and forwarded ones.
    /// </summary>
    private static bool IsPoll(SendMessageInput input)
    {
        return input.MessageType == MessageType.Poll
               || input.PollId != null
               || input.Media is TMessageMediaPoll
               || input.InputMedia is TInputMediaPoll;
    }

    /// <summary>
    /// Adds the sender as a channel member of the monoforum WITHOUT emitting the
    /// <c>messageActionChatAddUser</c> service message. We publish
    /// <see cref="CreateChannelMemberCommand"/> directly so <see cref="MyTelegram.Domain.Sagas.JoinChannelSaga"/>
    /// (which is responsible for the join service message) never starts.
    /// </summary>
    private async Task AutoJoinMonoforumSilentlyAsync(IChannelReadModel channelReadModel, SendMessageInput input)
    {
        var channelId = channelReadModel.ChannelId;
        var userId = input.SenderUserId;
        var requestInfo = input.RequestInfo;
        var date = DateTime.UtcNow.ToTimestamp();

        try
        {
            await commandBus.PublishAsync(new CreateChannelMemberCommand(
                ChannelMemberId.Create(channelId, userId),
                requestInfo,
                channelId,
                userId,
                inviterId: userId,
                date,
                isBot: false,
                chatInviteId: null,
                isBroadcast: channelReadModel.Broadcast,
                ChatJoinType.BySelf));

            await commandBus.PublishAsync(new IncrementParticipantCountCommand(ChannelId.Create(channelId)));

            var toPeer = new Peer(PeerType.Channel, channelId);
            await commandBus.PublishAsync(new CreateDialogCommand(
                DialogId.Create(userId, toPeer),
                requestInfo,
                userId,
                toPeer,
                channelHistoryMinId: channelReadModel.HiddenPreHistory ? channelReadModel.TopMessageId : 0,
                channelReadModel.TopMessageId));
        }
        catch (Exception ex) when (ex.GetType().Name == "UserAlreadyParticipantException"
                                    || ex.GetType().Name == "DuplicateOperationException")
        {
            // Race with another concurrent send — membership already exists.
        }
    }

    private async Task<Peer?> GetDefaultSendAsAsync(SendMessageInput input)
    {
        // Get the default SendAsPeer, follow the following rules
        // 1.If the client passes sendAs, verify the client's sendAs, if valid, use the value passed by the client
        // 2.If the client does not pass sendAs, query whether the user has set the default sendAsPeer, if set, use the set value
        // 3.If the client does not pass a value, the default SendAsPeer is not set, and in the discussion group, use discussion group as SendAsPeer
        if (input.ToPeer.PeerType == PeerType.Channel)
        {
            var monoforumReadModel = await channelAppService.GetAsync(input.ToPeer.PeerId);
            if (monoforumReadModel?.IsMonoforum == true &&
                input.SavedPeerId != null &&
                input.SavedPeerId.PeerId != input.SenderUserId &&
                monoforumReadModel.LinkedMonoforumId.HasValue)
            {
                return monoforumReadModel.LinkedMonoforumId.Value.ToChannelPeer();
            }
        }

        if (input.SendAs != null)
        {
            if (await IsValidSendAsPeerAsync(input.RequestInfo.UserId, input.ToPeer, input.SendAs))
            {
                return input.SendAs;
            }
        }
        else if (input.ToPeer.PeerType == PeerType.Channel)
        {
            var channelReadModel = await channelAppService.GetAsync(input.ToPeer.PeerId);

            if (!await CanSendAsPeerAsync(input.ToPeer.PeerId, input.RequestInfo.UserId))
            {
                var admin = channelReadModel.AdminList.FirstOrDefault(p => p.UserId == input.SenderUserId);
                if (admin is { AdminRights.Anonymous: true })
                {
                    return channelReadModel.ChannelId.ToChannelPeer();
                }

                return null;
            }
            Peer? sendAsPeer;

            var userConfigReadModel = await queryProcessor.ProcessAsync(
                new GetUserConfigByKeyQuery(input.RequestInfo.UserId, ((int)UserConfigType.SendAsPeer).ToString()));
            if (userConfigReadModel != null)
            {
                if (long.TryParse(userConfigReadModel.Value, out var sendAsPeerId))
                {
                    sendAsPeer = peerHelper.GetPeer(sendAsPeerId);
                    if (await IsValidSendAsPeerAsync(input.RequestInfo.UserId, input.ToPeer, sendAsPeer))
                    {
                        return sendAsPeer;
                    }
                }
            }

            if (channelReadModel is { MegaGroup: true, LinkedChatId: not null })
            {
                sendAsPeer = channelReadModel.ChannelId.ToChannelPeer();
                if (await IsValidSendAsPeerAsync(input.RequestInfo.UserId, input.ToPeer, sendAsPeer))
                {
                    return sendAsPeer;
                }
            }
        }

        return null;
    }

    private async Task<SendMessageItem> CreateSendMessageItemAsync(SendMessageInput input)
    {
        //await CheckSendAsAsync(input);
        await CheckGlobalPrivacySettingsAsync(input);
        var channelReadModel = await CheckChannelBannedRightsAsync(input);
        var targetNoForwards = channelReadModel?.NoForwards == true;
        if (!targetNoForwards && input.ToPeer.PeerType == PeerType.Chat)
        {
            targetNoForwards = (await channelAppService.GetAsync(input.ToPeer.PeerId))?.NoForwards == true;
        }
        if (!targetNoForwards &&
            input.ToPeer.PeerType == PeerType.User &&
            input.ToPeer.PeerId != input.SenderUserId)
        {
            targetNoForwards = await IsPrivateChatNoForwardsEnabledAsync(input.SenderUserId, input.ToPeer.PeerId);
        }

        var entities = input.Entities ?? [];
        var mentionedUserIds = await ProcessMessageEntitiesAsync(input.Message, entities, input.ToPeer);
        if (entities.Count == 0)
        {
            entities = null;
        }
        var ownerPeerId = input.ToPeer.PeerType == PeerType.Channel ? input.ToPeer.PeerId : input.SenderUserId;
        var replyToMsgId = input.InputReplyTo.ToReplyToMsgId();

        // Reply to group: ToPeerId=input.ToPeerId,SenderUserId=input.UserId
        // Reply to user:  ToPeerId=Input.UserId,OwnerPeerId=input.ToPeerId,MessageId=replyToMsgId

        var replyToMsgItems =
            await queryProcessor.ProcessAsync(new GetReplyToMsgIdListQuery(input.ToPeer, input.SenderUserId,
                replyToMsgId));
        var idType = IdType.MessageId;
        var subType = input.MessageSubType != MessageSubType.Normal
            ? input.MessageSubType
            : input.MessageAction is TMessageActionStarGift
                ? MessageSubType.StarGift
                : MessageSubType.Normal;
        var messageActionType = input.MessageAction switch
        {
            TMessageActionPhoneCall => MessageActionType.PhoneCall,
            TMessageActionConferenceCall => MessageActionType.PhoneCall,
            TMessageActionPaidMessagesPrice => MessageActionType.PaidMessagesPriceUpdated,
            TMessageActionSuggestedPostApproval => MessageActionType.ToggleSuggestedPostApproval,
            TMessageActionGiftStars => MessageActionType.GiftStars,
            TMessageActionPaidMessage => MessageActionType.PaidMessage,
            TMessageActionGiftCode => MessageActionType.GiftCode,
            TMessageActionPrizeStars => MessageActionType.PrizeStars,
            _ => MessageActionType.None
        };
        var post = channelReadModel?.Broadcast ?? false;
        var linkedChannelId = channelReadModel?.Broadcast ?? false ? channelReadModel.LinkedChatId : null;
        var sendAs = await GetDefaultSendAsAsync(input);
        string? postAuthor = null;
        var isPublicPost = channelReadModel is { Broadcast: true, UserName: not null };
        int? views = null;
        if (channelReadModel?.Broadcast ?? false)
        {
            views = 1;
        }
        if (channelReadModel is { Signatures: true, Broadcast: true })
        {
            if (sendAs?.PeerType == PeerType.Channel)
            {
                var sendAsChannelReadModel = await channelAppService.GetAsync(sendAs.PeerId);
                postAuthor = sendAsChannelReadModel.Title;
            }
            else
            {
                var userReadModel = await userAppService.GetAsync(input.RequestInfo.UserId);
                postAuthor = $"{userReadModel.FirstName} {userReadModel.LastName}";
            }

            if (sendAs == null && channelReadModel.SignatureProfiles)
            {
                sendAs = input.RequestInfo.UserId.ToUserPeer();
            }
        }

        postAuthor = input.PostAuthor ?? postAuthor;
        views = input.Views ?? views;

        var scheduleDate = input.ScheduleDate;
        if (scheduleDate.HasValue)
        {
            // If the schedule_date is less than 20 seconds in the future, the message will be sent immediately,
            // generating a normal updateNewMessage/updateNewChannelMessage.
            if (scheduleDate.Value - CurrentDate < 20)
            {
                scheduleDate = null;
            }
            else
            {
                idType = IdType.ScheduleMessageId;
            }
        }

        var pts = 0;
        MessageReply? reply = null;
        if (post && linkedChannelId.HasValue)
        {
            reply = new MessageReply(linkedChannelId, 0, 0, 0, []);
        }

        var messageId = input.MessageId ?? await idGenerator.NextIdAsync(idType, ownerPeerId);
        //var messageId = 0;
        int? scheduleMessageId = null;
        if (idType == IdType.ScheduleMessageId)
        {
            scheduleMessageId = await idGenerator.NextIdAsync(IdType.ScheduleMessageId, ownerPeerId);
        }

        var date = CurrentDate;
        var hashtags = GetHashtags(input.Message);
        byte[]? encryptedData = null;
        byte[]? inboxMessageEncryptedData = null;
        if (!string.IsNullOrEmpty(input.Message) &&
            options.CurrentValue.EncryptionConfig is { Enabled: true, MessageKeys.Count: > 0 })
        {
            _keyConfig ??= options.CurrentValue.EncryptionConfig.MessageKeys[0];
            _encryptionKey ??= Encoding.UTF8.GetBytes(_keyConfig.Key);
            encryptedData = dataEncryptionHelper.Encrypt(_keyConfig.Id, _encryptionKey, ownerPeerId, input.Message);

            if (input.ToPeer.PeerType == PeerType.User && input.ToPeer.PeerId != input.SenderUserId)
            {
                inboxMessageEncryptedData =
                    dataEncryptionHelper.Encrypt(_keyConfig.Id, _encryptionKey, input.ToPeer.PeerId, input.Message);
            }
        }
        var messageItem = new MessageItem(
            input.ToPeer with { PeerId = ownerPeerId /*, AccessHash = 0 */ },
            input.ToPeer,
            new Peer(PeerType.User, input.SenderUserId),
            input.SenderUserId,
            messageId,
            input.Message,
            date,
            input.RandomId,
            true,
            input.SendMessageType,
            //(MessageType)input.SendMessageType,
            input.MessageType,
            subType,
            input.InputReplyTo,
            //input.MessageActionData,
            input.MessageAction,
            messageActionType,
            entities,
            input.Media,
            input.GroupId,
            FwdHeader: input.FwdHeader,
            PollId: input.PollId,
            Post: post,
            ReplyMarkup: input.ReplyMarkup,
            TopMsgId: input.TopMsgId,
            PostAuthor: postAuthor,
            SendAs: sendAs,
            Effect: input.Effect,
            ReplyToMsgItems: replyToMsgItems?.ToList(),
            LinkedChannelId: linkedChannelId,
            Pts: pts,
            Silent: input.Silent,
            ScheduleDate: scheduleDate,
            ScheduleMessageId: scheduleMessageId,
            Reply: reply,
            InvertMedia: input.InvertMedia,
            PublicPosts: isPublicPost,
            Hashtags: hashtags,
            MentionedUserIds: mentionedUserIds,
            Views: views,
			EncryptedData: encryptedData,
            InboxMessageEncryptedData: inboxMessageEncryptedData,
            SavedPeerId: input.SavedPeerId ?? (channelReadModel?.IsMonoforum == true ? new Peer(PeerType.User, input.SenderUserId) : null),
            PaidMessageStars: input.PaidMessageStars,
            SuggestedPost: input.SuggestedPost,
            PaidSuggestedPostStars: input.SuggestedPost?.Price is TStarsAmount,
            PaidSuggestedPostTon: input.SuggestedPost?.Price is TStarsTonAmount,
            TtlPeriod: input.TtlPeriod,
            NoForwards: input.NoForwards || targetNoForwards
        );

        var sendMessageItem = new SendMessageItem(messageItem, input.ClearDraft, mentionedUserIds, []);

        return sendMessageItem;
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

    public List<string> GetHashtags(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return [];
        }

        var matches = Regex.Matches(message, HashtagPattern);
        var hashtags = new List<string>();
        const int maxHashtags = 10;
        foreach (Match match in matches)
        {
            if (hashtags.Count > maxHashtags)
            {
                break;
            }

            var hashtag = match.Groups[1].Value;
            if (!hashtags.Contains(hashtag))
            {
                hashtags.Add(hashtag);
            }
        }

        return hashtags;
    }

    private async Task SaveScheduledMessagesAsync(List<SendMessageItem> scheduledItems, RequestInfo requestInfo)
    {
        var collection = mongoDatabase.GetCollection<BsonDocument>("scheduled_messages");

        foreach (var item in scheduledItems)
        {
            var messageItem = item.MessageItem;

            var doc = new BsonDocument
            {
                ["_id"] = $"scheduled-{messageItem.OwnerPeer.PeerId}-{messageItem.ScheduleMessageId ?? messageItem.MessageId}",
                ["ScheduledMessageId"] = messageItem.ScheduleMessageId ?? messageItem.MessageId,
                ["SenderUserId"] = messageItem.SenderPeer.PeerId,
                ["PeerId"] = messageItem.ToPeer.PeerId,
                ["PeerType"] = messageItem.ToPeer.PeerType.ToString(),
                ["Message"] = messageItem.Message,
                ["ScheduleDate"] = messageItem.ScheduleDate!.Value,
                ["RandomId"] = messageItem.RandomId,
                ["CreatedAt"] = DateTime.UtcNow
            };

            if (messageItem.InputReplyTo != null)
            {
                var replyToMsgId = messageItem.InputReplyTo.ToReplyToMsgId();
                if (replyToMsgId.HasValue)
                {
                    doc["ReplyToMsgId"] = replyToMsgId.Value;
                }
            }

            if (messageItem.Entities != null && messageItem.Entities.Count > 0)
            {
                doc["Entities"] = System.Text.Json.JsonSerializer.Serialize(messageItem.Entities);
            }

            if (messageItem.Media != null)
            {
                doc["MediaType"] = messageItem.Media.GetType().Name;
                doc["MediaData"] = System.Text.Json.JsonSerializer.Serialize(messageItem.Media);
            }

            if (messageItem.ReplyMarkup != null)
            {
                doc["ReplyMarkup"] = System.Text.Json.JsonSerializer.Serialize(messageItem.ReplyMarkup);
            }

            doc["Silent"] = messageItem.Silent;
            doc["NoForwards"] = messageItem.NoForwards;

            await collection.InsertOneAsync(doc);
        }

        // Send updateNewScheduledMessage to client
        foreach (var item in scheduledItems)
        {
            var messageItem = item.MessageItem;

            var message = new TMessage
            {
                Id = messageItem.ScheduleMessageId ?? messageItem.MessageId,
                Out = true,
                FromId = new TPeerUser { UserId = messageItem.SenderPeer.PeerId },
                PeerId = ConvertToPeer(messageItem.ToPeer),
                Date = messageItem.ScheduleDate!.Value,
                Message = messageItem.Message,
                Entities = messageItem.Entities ?? new TVector<IMessageEntity>(),
                Silent = messageItem.Silent,
                Noforwards = messageItem.NoForwards
            };

            if (messageItem.InputReplyTo != null)
            {
                var replyToMsgId = messageItem.InputReplyTo.ToReplyToMsgId();
                if (replyToMsgId.HasValue)
                {
                    message.ReplyTo = new TMessageReplyHeader { ReplyToMsgId = replyToMsgId.Value };
                }
            }

            var update = new TUpdateNewScheduledMessage
            {
                Message = message
            };

            var updates = new TUpdates
            {
                Updates = new TVector<IUpdate> { update },
                Users = new TVector<IUser>(),
                Chats = new TVector<IChat>(),
                Date = CurrentDate
            };

            await objectMessageSender.PushMessageToPeerAsync(
                new Peer(PeerType.User, messageItem.SenderPeer.PeerId),
                updates,
                pts: 0
            );
        }
    }

    private static IPeer ConvertToPeer(Peer peer)
    {
        return peer.PeerType switch
        {
            PeerType.User => new TPeerUser { UserId = peer.PeerId },
            PeerType.Chat => new TPeerChat { ChatId = peer.PeerId },
            PeerType.Channel => new TPeerChannel { ChannelId = peer.PeerId },
            _ => new TPeerUser { UserId = peer.PeerId }
        };
    }

    private async Task CheckGlobalPrivacySettingsAsync(SendMessageInput input)
    {
        if (input.ToPeer.PeerType == PeerType.User && input.RequestInfo.UserId != input.ToPeer.PeerId)
        {
            var globalPrivacySettings = await privacyAppService.GetGlobalPrivacySettingsAsync(input.ToPeer.PeerId);
            if (globalPrivacySettings?.NewNoncontactPeersRequirePremium ?? false)
            {
                var userReadModel = await userAppService.GetAsync(input.RequestInfo.UserId);
                if (userReadModel.UserId != MyTelegramConsts.NotificationServiceUserId && !userReadModel.Premium)
                {
                    var contactType =
                        await contactAppService.GetContactTypeAsync(input.RequestInfo.UserId, input.ToPeer.PeerId);
                    if (contactType != ContactType.Mutual && contactType != ContactType.ContactOfTargetUser)
                    {
                        RpcErrors.RpcErrors406.PrivacyPremiumRequired.ThrowRpcError();
                    }
                }
            }
        }
    }

    public async Task<List<long>> ProcessMessageEntitiesAsync(string? message, IList<IMessageEntity>? entities, Peer toPeer)
    {
        if (string.IsNullOrEmpty(message))
        {
            return [];
        }

        await ProcessMessageEntityCustomEmojiAsync(message, entities);
        ProcessMessageEntityHashtag(message, entities);
        ProcessMessageEntityUrlList(message, entities);
        return await ProcessMessageEntityMentionAsync(message, entities, toPeer);
    }

    private async Task ProcessMessageEntityCustomEmojiAsync(string message, IList<IMessageEntity>? entities)
    {
        if (entities == null || entities.Count == 0)
        {
            return;
        }

        var docCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");
        var customEmojiEntities = entities.OfType<TMessageEntityCustomEmoji>().ToList();
        if (customEmojiEntities.Count == 0)
        {
            return;
        }

        var documentIds = customEmojiEntities.Select(x => x.DocumentId).Distinct().ToList();
        var filter = Builders<BsonDocument>.Filter.In("DocumentId", documentIds.Select(x => (BsonValue)new BsonInt64(x)));
        var documents = await docCol.Find(filter).ToListAsync();
        var documentMap = documents.ToDictionary(x => x["DocumentId"].ToInt64());

        foreach (var entity in customEmojiEntities)
        {
            if (entity.Offset < 0 || entity.Length <= 0 || entity.Offset + entity.Length > message.Length)
            {
                RpcErrors.RpcErrors400.EntityBoundsInvalid.ThrowRpcError();
            }

            if (!documentMap.TryGetValue(entity.DocumentId, out var document))
            {
                RpcErrors.RpcErrors400.DocumentInvalid.ThrowRpcError();
            }

            if (!TryGetCustomEmojiAttributeForEntity(document!, out var attribute))
            {
                if (await TryDowngradeLegacyAnimatedEmojiEntityAsync(message, entities, entity))
                {
                    continue;
                }

                RpcErrors.RpcErrors400.DocumentInvalid.ThrowRpcError();
            }

            var text = message.Substring(entity.Offset, entity.Length);
            if (IsSameEmoji(text, attribute.Alt))
            {
                continue;
            }

            var replacement = await TryResolveReplacementCustomEmojiDocumentAsync(text, attribute);
            if (replacement != null)
            {
                entity.DocumentId = replacement.Value;
                continue;
            }

            RpcErrors.RpcErrors400.EmoticonInvalid.ThrowRpcError();
        }
    }

    private async Task<bool> TryDowngradeLegacyAnimatedEmojiEntityAsync(
        string message,
        IList<IMessageEntity> entities,
        TMessageEntityCustomEmoji entity)
    {
        var setCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-stickersetreadmodel");
        var setDoc = await setCol.Find(Builders<BsonDocument>.Filter.Eq("ShortName", "AnimatedEmojies")).FirstOrDefaultAsync();
        if (setDoc == null || !setDoc.Contains("Packs") || !setDoc["Packs"].IsBsonArray)
        {
            return false;
        }

        var text = message.Substring(entity.Offset, entity.Length);
        var belongsToLegacyAnimatedEmojiSet = setDoc["Packs"].AsBsonArray
            .Where(x => x.IsBsonDocument)
            .Select(x => x.AsBsonDocument)
            .Any(x =>
                x.Contains("Emoticon") &&
                x["Emoticon"].IsString &&
                IsSameEmoji(text, x["Emoticon"].AsString) &&
                x.Contains("Documents") &&
                x["Documents"].IsBsonArray &&
                x["Documents"].AsBsonArray.Any(documentId => documentId.ToInt64() == entity.DocumentId));

        if (!belongsToLegacyAnimatedEmojiSet)
        {
            return false;
        }

        entities.Remove(entity);
        return true;
    }

    private async Task<long?> TryResolveReplacementCustomEmojiDocumentAsync(string text, TDocumentAttributeCustomEmoji attribute)
    {
        if (attribute.Stickerset is not TInputStickerSetID stickerSetId || stickerSetId.Id == 0)
        {
            return null;
        }

        var setCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-stickersetreadmodel");
        var setDoc = await setCol.Find(Builders<BsonDocument>.Filter.Eq("StickerSetId", stickerSetId.Id)).FirstOrDefaultAsync();
        if (setDoc == null || !setDoc.Contains("Packs") || !setDoc["Packs"].IsBsonArray)
        {
            return null;
        }

        var candidateDocumentIds = setDoc["Packs"].AsBsonArray
            .Where(x => x.IsBsonDocument)
            .Select(x => x.AsBsonDocument)
            .Where(x => x.Contains("Emoticon") && x["Emoticon"].IsString && IsSameEmoji(text, x["Emoticon"].AsString))
            .Where(x => x.Contains("Documents") && x["Documents"].IsBsonArray)
            .SelectMany(x => x["Documents"].AsBsonArray)
            .Select(x => x.ToInt64())
            .Distinct()
            .ToList();

        if (candidateDocumentIds.Count == 0)
        {
            return null;
        }

        var docCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");
        var filter = Builders<BsonDocument>.Filter.In("DocumentId", candidateDocumentIds.Select(x => (BsonValue)new BsonInt64(x)));
        var docs = await docCol.Find(filter).ToListAsync();
        foreach (var candidateDocumentId in candidateDocumentIds)
        {
            var candidate = docs.FirstOrDefault(x => x["DocumentId"].ToInt64() == candidateDocumentId);
            if (candidate != null &&
                TryGetCustomEmojiAttributeForEntity(candidate, out var candidateAttribute) &&
                IsSameEmoji(text, candidateAttribute.Alt))
            {
                return candidateDocumentId;
            }
        }

        return null;
    }

    private static bool TryGetCustomEmojiAttributeForEntity(BsonDocument document, out TDocumentAttributeCustomEmoji attribute)
    {
        return CustomEmojiAttributeHelper.TryGetCustomEmojiAttribute(document, out attribute) ||
               CustomEmojiAttributeHelper.TryGetStickerAttributeAsCustomEmoji(document, out attribute);
    }

    private static bool IsSameEmoji(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
        {
            return false;
        }

        return string.Equals(NormalizeEmoji(left), NormalizeEmoji(right), StringComparison.Ordinal);
    }

    private static string NormalizeEmoji(string value)
    {
        return value.Replace("\uFE0F", string.Empty, StringComparison.Ordinal);
    }

    private async Task<List<long>> ProcessMessageEntityMentionAsync(string message, IList<IMessageEntity>? entities, Peer toPeer)
    {
        var mentionsAndUserNames = GetMentions(message);
        var mentions = mentionsAndUserNames.mentions;
        var mentionedUserNames = mentionsAndUserNames.userNameList;
        var mentionedUserIds = new List<long>();

        if (entities?.Count > 0)
        {
            foreach (var messageEntity in entities)
            {
                switch (messageEntity)
                {
                    case TInputMessageEntityMentionName inputMessageEntityMentionName:
                        var userPeer = peerHelper.GetPeer(inputMessageEntityMentionName.UserId);
                        mentionedUserIds.Add(userPeer.PeerId);
                        break;
                    case TMessageEntityMention messageEntityMention:
                        mentionedUserNames.Add(message.Substring(messageEntityMention.Offset + 1,
                            messageEntityMention.Length - 1));
                        break;
                    case TMessageEntityMentionName messageEntityMentionName:
                        mentionedUserIds.Add(messageEntityMentionName.UserId);
                        break;
                }
            }
        }

        if (mentionedUserNames.Count > 0)
        {
            entities ??= [];
            foreach (var messageEntityMention in mentions)
            {
                entities.Add(messageEntityMention);
            }
        }

        if (toPeer.PeerType == PeerType.Channel)
        {
            var mentionedUsers =
                await queryProcessor.ProcessAsync(new GetUserNameListByNamesQuery(mentionedUserNames, PeerType.User));
            mentionedUserIds.AddRange(mentionedUsers.Select(p => p.PeerId).Distinct().ToList());

            var memberUserIds =
                await queryProcessor.ProcessAsync(new GetChannelMemberIdListQuery(toPeer.PeerId, mentionedUserIds));

            mentionedUserIds = memberUserIds.ToList();
        }
        else
        {
            mentionedUserIds = [];
        }

        return mentionedUserIds;
    }

    private (List<TMessageEntityMention> mentions, List<string> userNameList) GetMentions(string message)
    {
        var pattern = "@(\\w{4,40})";
        var mentions = new List<TMessageEntityMention>();
        var matches = Regex.Matches(message, pattern);
        var userNameList = new List<string>();
        foreach (Match match in matches)
        {
            if (match.Success)
            {
                mentions.Add(new TMessageEntityMention
                {
                    Offset = match.Index,
                    Length = match.Length
                });
                userNameList.Add(match.Value[1..]);
            }
        }

        return (mentions, userNameList);
    }

    private void ProcessMessageEntityUrlList(string message, IList<IMessageEntity>? entities)
    {
        var matches = Regex.Matches(message, UrlPattern);
        foreach (Match match in matches)
        {
            if (match.Success)
            {
                var entity = new TMessageEntityUrl
                {
                    Offset = match.Index,
                    Length = match.Length
                };
                entities ??= [];
                entities.Add(entity);
            }
        }
    }

    private void ProcessMessageEntityHashtag(string message, IList<IMessageEntity>? entities)
    {
        var hashtagMatches = Regex.Matches(message, HashtagPattern);
        foreach (Match match in hashtagMatches)
        {
            if (match.Success)
            {
                var entity = new TMessageEntityHashtag
                {
                    Offset = match.Index,
                    Length = match.Length
                };
                entities ??= [];
                entities.Add(entity);
            }
        }
    }

    private Task<GetMessageOutput> GetMessagesCoreAsync<TRequest>(TRequest input)
        where TRequest : GetPagedListInput
    {
        var offset = offsetHelper.GetOffsetInfo(input);
        var query = objectMapper.Map<TRequest, GetMessagesQuery>(input);
        query.Offset = offset;

        return GetMessagesInternalAsync(query);
    }

    private async Task<GetMessageOutput> GetMessagesInternalAsync(GetMessagesQuery query,
        IReadOnlyCollection<long>? users = null,
        IReadOnlyCollection<long>? chats = null)
    {
        var messageList = await queryProcessor.ProcessAsync(query);
        HashSet<long> userIds = users?.ToHashSet() ?? [];
        HashSet<long> channelIds = chats?.ToHashSet() ?? [];
        userIds.Add(query.SelfUserId);

        AddExtraPeerIds(messageList, userIds, channelIds);
        var userIdList = userIds.ToList();

        var userList = await userAppService.GetListAsync(userIdList);

        var channelList = channelIds.Count == 0
            ? []
            : await channelAppService.GetListAsync(channelIds);

        var contactList = await queryProcessor
            .ProcessAsync(new GetContactListQuery(query.SelfUserId, userIdList));

        var photoIds = new List<long>();
        photoIds.AddRange(channelList.Select(p => p.PhotoId ?? 0));
        photoIds.AddRange(userList.Select(p => p.PersonalPhotoId ?? 0));
        photoIds.AddRange(userList.Select(p => p.ProfilePhotoId ?? 0));
        photoIds.AddRange(userList.Select(p => p.FallbackPhotoId ?? 0));
        photoIds.AddRange(contactList.Select(p => p.PhotoId ?? 0));
        photoIds.RemoveAll(p => p == 0);

        var photoList = await photoAppService.GetListAsync(photoIds);

        IReadOnlyCollection<long> joinedChannelIdList = new List<long>();
        if (channelIds.Count > 0)
        {
            joinedChannelIdList = await queryProcessor
                .ProcessAsync(new GetJoinedChannelIdListQuery(query.SelfUserId, [.. channelIds]));
        }

        var privacyList = await privacyAppService.GetPrivacyListAsync(userIdList);
        IReadOnlyCollection<IChannelMemberReadModel> channelMemberList = new List<IChannelMemberReadModel>();
        if (joinedChannelIdList.Count > 0)
        {
            channelMemberList = await queryProcessor
                .ProcessAsync(
                    new GetChannelMemberListByChannelIdListQuery(query.SelfUserId, joinedChannelIdList.ToList()));
        }

        var pts = query.Pts;
        if (pts == 0 && messageList.Count > 0)
        {
            pts = messageList.Max(p => p.Pts);
        }

        var pollIdList = messageList.Where(p => p.PollId.HasValue).Select(p => p.PollId!.Value).ToList();
        IReadOnlyCollection<IPollReadModel>? pollReadModels = null;
        IReadOnlyCollection<IPollAnswerVoterReadModel>? chosenOptions = null;

        if (pollIdList.Count > 0)
        {
            pollReadModels = await queryProcessor.ProcessAsync(new GetPollsQuery(pollIdList));
            chosenOptions = await queryProcessor
                .ProcessAsync(new GetChosenVoteAnswersQuery(pollIdList, query.SelfUserId));
        }

        return new GetMessageOutput(channelList,
            channelMemberList,
            [],
            contactList,
            joinedChannelIdList,
            messageList,
            privacyList,
            userList,
            photoList,
            pollReadModels,
            chosenOptions,
            [],
            query.Limit == messageList.Count,
            query.IsSearchGlobal,
            pts,
            query.SelfUserId,
            query.Limit,
            query.Offset
        );
    }

    public (HashSet<long> userIds, HashSet<long> channelIds) GetExtraPeerIds(
        IReadOnlyCollection<IMessageReadModel> messageReadModels)
    {
        var userIds = new HashSet<long>();
        var channelIds = new HashSet<long>();
        AddExtraPeerIds(messageReadModels, userIds, channelIds);

        return (userIds, channelIds);
    }

    private void AddExtraPeerIds(IReadOnlyCollection<IMessageReadModel> messageReadModels, HashSet<long> userIds,
        HashSet<long> channelIds)
    {
        void AddPeerIdIfNeeded(Peer? peer)
        {
            switch (peer?.PeerType)
            {
                case PeerType.Channel:
                    channelIds.Add(peer.PeerId);
                    break;

                case PeerType.User:
                    userIds.Add(peer.PeerId);
                    break;
            }
        }

        foreach (var messageReadModel in messageReadModels)
        {
            AddPeerIdIfNeeded(messageReadModel.SendAs);
            AddPeerIdIfNeeded(messageReadModel.SavedPeerId);
            AddPeerIdIfNeeded(messageReadModel.FwdHeader?.SavedFromPeer);

            var fwd = messageReadModel.FwdHeader;
            AddPeerIdIfNeeded(fwd?.FromId);
            AddPeerIdIfNeeded(fwd?.SavedFromId);
            AddPeerIdIfNeeded(fwd?.SavedFromPeer);
            AddPeerIdIfNeeded(messageReadModel.SendAs);
            var senderPeer = peerHelper.GetPeer(messageReadModel.SenderPeerId);
            AddPeerIdIfNeeded(senderPeer);

            switch (messageReadModel.ToPeerType)
            {
                case PeerType.Channel:
                    channelIds.Add(messageReadModel.ToPeerId);
                    break;

                case PeerType.User:
                    userIds.Add(messageReadModel.ToPeerId);
                    break;
            }

            switch (messageReadModel.MessageAction)
            {
                case TMessageActionChatAddUser messageActionChatAddUser:
                    foreach (var userId in messageActionChatAddUser.Users)
                    {
                        userIds.Add(userId);
                    }
                    break;

                case TMessageActionChatJoinedByLink messageActionChatJoinedByLink:
                    userIds.Add(messageActionChatJoinedByLink.InviterId);
                    break;

                case TMessageActionChatJoinedByRequest:

                    break;

                case TMessageActionChatDeleteUser messageActionChatDeleteUser:
                    userIds.Add(messageActionChatDeleteUser.UserId);
                    break;

                case TMessageActionStarGift { FromId: TPeerUser starGiftFromUser }:
                    userIds.Add(starGiftFromUser.UserId);
                    break;
            }
        }
    }
}
