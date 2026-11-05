using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;
using MyTelegram.Messenger.Services.Business;
using MyTelegram.Messenger.Services.Entities;
using MyTelegram.Messenger.Services.Scheduled;
using MyTelegram.Messenger.Services.VideoProcessing;

namespace MyTelegram.Messenger.Services.Impl;

public class MessageAppService(
    MyTelegram.Messenger.Services.Bots.IUrlAuthService urlAuthService,
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
    IObjectMessageSender objectMessageSender,
    IScheduledMessageStore scheduledMessageStore,
    IVideoProcessingService videoProcessingService,
    IMessageEntityService messageEntityService,
    IQuickReplyService quickReplyService)
    : BaseAppService, IMessageAppService, ITransientDependency
{
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

    public async Task<Peer?> GetAnonymousSendAsPeerAsync(long channelId, long userId)
    {
        var channelReadModel = await channelAppService.GetAsync(channelId);
        var admin = channelReadModel?.AdminList.FirstOrDefault(p => p.UserId == userId);

        return admin is { AdminRights.Anonymous: true }
            ? channelId.ToChannelPeer()
            : null;
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

    public async Task<GetMessageOutput> SearchAsync(SearchInput input)
    {
        var output = await GetMessagesCoreAsync(input);
        if (input.MyMentionsOnly)
        {
            output = await FilterMyMentionsAsync(output, input.SelfUserId);
        }

        if (input.SavedReaction?.Count > 0)
        {
            output = FilterBySavedReaction(output, input.SavedReaction);
        }

        return output;
    }

    /// <summary>
    /// Applies saved_reaction (saved messages tags). Reactions are stored as a nested polymorphic
    /// list, which cannot be matched inside the read model query, so the tag filter runs after the
    /// query. As with mentions, a page can come back shorter than the requested limit.
    /// See https://corefork.telegram.org/api/saved-messages#tags
    /// </summary>
    private static GetMessageOutput FilterBySavedReaction(GetMessageOutput output,
        TVector<IReaction> savedReactions)
    {
        if (output.MessageList.Count == 0)
        {
            return output;
        }

        var emoticons = savedReactions.OfType<TReactionEmoji>().Select(p => p.Emoticon).ToHashSet(StringComparer.Ordinal);
        var customEmojiIds = savedReactions.OfType<TReactionCustomEmoji>().Select(p => p.DocumentId).ToHashSet();

        output.MessageList = output.MessageList
            .Where(p => p.Reactions?.Any(r =>
                (r.Emoticon != null && emoticons.Contains(r.Emoticon)) ||
                (r.CustomEmojiDocumentId.HasValue && customEmojiIds.Contains(r.CustomEmojiDocumentId.Value))) == true)
            .ToList();

        return output;
    }

    public async Task<GetMessageOutput> SearchGlobalAsync(SearchGlobalInput input)
    {
        var output = await GetMessagesCoreAsync(input);
        return input.MyMentionsOnly ? await FilterMyMentionsAsync(output, input.SelfUserId) : output;
    }

    /// <summary>
    /// Applies inputMessagesFilterMyMentions. There is no indexed "mentions me" flag on the read
    /// model, so mentions are resolved from message entities after the query runs. Consequence: a
    /// page can come back shorter than the requested limit (the client keeps paging by offset_id).
    /// </summary>
    private async Task<GetMessageOutput> FilterMyMentionsAsync(GetMessageOutput output, long selfUserId)
    {
        if (output.MessageList.Count == 0)
        {
            return output;
        }

        var selfUser = await userAppService.GetAsync(selfUserId);
        var userName = selfUser?.UserName;
        var activeUserNames = selfUser?.Usernames?.Where(p => p.Active).Select(p => p.Username).ToList() ?? [];

        var mentioned = output.MessageList
            .Where(p => MentionsUser(p, selfUserId, userName, activeUserNames))
            .ToList();

        output.MessageList = mentioned;
        return output;
    }

    private static bool MentionsUser(IMessageReadModel message, long selfUserId, string? userName,
        List<string> activeUserNames)
    {
        if (message.Entities2 != null)
        {
            foreach (var entity in message.Entities2)
            {
                switch (entity)
                {
                    case TMessageEntityMentionName mentionName when mentionName.UserId == selfUserId:
                        return true;

                    // messageEntityMention only carries offset/length, so the username has to be
                    // read back out of the message text.
                    case TMessageEntityMention mention:
                        if (ContainsOwnUserName(message.Message, mention.Offset, mention.Length, userName,
                                activeUserNames))
                        {
                            return true;
                        }

                        break;
                }
            }
        }

        return false;
    }

    private static bool ContainsOwnUserName(string? text, int offset, int length, string? userName,
        List<string> activeUserNames)
    {
        if (string.IsNullOrEmpty(text) || offset < 0 || length <= 0 || offset + length > text.Length)
        {
            return false;
        }

        var mentioned = text.Substring(offset, length).TrimStart('@');
        if (mentioned.Length == 0)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(userName) && mentioned.Equals(userName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return activeUserNames.Any(p => mentioned.Equals(p, StringComparison.OrdinalIgnoreCase));
    }
    public async Task SendMessageAsync(List<SendMessageInput> inputs)
    {
        if (inputs.Count == 0)
        {
            throw new ArgumentException();
        }

        List<SendMessageItem> sendMessageItems = [];
        List<ScheduledQueueItem> scheduledItems = [];
        var firstInput = inputs.First();
        var requestInfo = firstInput.RequestInfo;

        await ValidateScheduledMessagesAsync(inputs);
        var quickReplies = new Dictionary<string, QuickReplyItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var input in inputs)
        {
            CheckBotPermission(input.RequestInfo.UserId, input.ToPeer);

            // A video posted to a big channel is converted server side before it is delivered, so it
            // goes through the schedule queue even though the client never asked for a schedule date.
            // See https://corefork.telegram.org/api/scheduled-messages#automatic-video-processing
            var videoProcessingPending = !input.ScheduleDate.HasValue &&
                                         await videoProcessingService.ShouldProcessAsync(input.Media, input.ToPeer);
            var conversionDate = videoProcessingPending
                ? CurrentDate + videoProcessingService.EstimateConversionSeconds(input.Media)
                : (int?)null;

            var item = await CreateSendMessageItemAsync(input, conversionDate, quickReplies);

            // Separate scheduled messages from immediate messages
            if (item.MessageItem.ScheduleDate.HasValue)
            {
                scheduledItems.Add(new ScheduledQueueItem(item.MessageItem, input.ScheduleRepeatPeriod,
                    input.MessageId, videoProcessingPending));
            }
            else
            {
                sendMessageItems.Add(item);
            }
        }

        // Save scheduled messages to the schedule queue
        if (scheduledItems.Count > 0)
        {
            await SaveScheduledMessagesAsync(scheduledItems, requestInfo);
        }

        // Send immediate messages through command bus
        if (sendMessageItems.Count > 0)
        {
            var command = new StartSendMessageCommand(TempId.New, requestInfo,
                sendMessageItems,
                isSendQuickReplyMessages: firstInput.IsSendQuickReplyMessage,
                isSendGroupedMessages: firstInput.IsSendGroupedMessage,
                clearDraft: firstInput.ClearDraft);

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

        var channelMemberReadModel =
            await queryProcessor.ProcessAsync(new GetChannelMemberByUserIdQuery(channelReadModel.ChannelId,
                input.SenderUserId));


        // The chat importer account authors an imported history without ever joining the group, the
        // same way service messages are written by a peer that is not a member.
        // See https://corefork.telegram.org/api/import
        var isChatImporter = input.SenderUserId == MyTelegramConsts.ChatImporterBotUserId;

        if (channelMemberReadModel == null && input.SendMessageType != SendMessageType.MessageService &&
            !isChatImporter)
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

        // Admins and the owner are never subject to the chat defaults or to a leftover restriction,
        // and service messages (gifts, joins, …) are not sent on behalf of a restricted user.
        var isPrivilegedSender = input.SendMessageType == SendMessageType.MessageService
                                 || channelReadModel.CreatorId == input.SenderUserId
                                 || channelReadModel.AdminList.Any(p => p.UserId == input.SenderUserId);

        if (!isPrivilegedSender)
        {
            var bannedDefaultRights =
                channelReadModel.DefaultBannedRights ?? ChatBannedRights.CreateDefaultBannedRights();
            if (bannedDefaultRights.SendMessages)
            {
                RpcErrors.RpcErrors403.ChatWriteForbidden.ThrowRpcError();
            }

            // A member restriction only applies while its until_date has not passed yet.
            var memberBannedRights = BannedRightsHelper.GetEffectiveBannedRights(channelMemberReadModel,
                DateTime.UtcNow.ToTimestamp());
            if (memberBannedRights?.SendMessages == true)
            {
                RpcErrors.RpcErrors400.UserBannedInChannel.ThrowRpcError();
            }

            // The chat defaults and the per-member restriction stack: a right is denied when either
            // of them denies it.
            var effectiveBannedRights = ChatBannedRights.FromValue(
                bannedDefaultRights.ToIntValue() | (memberBannedRights?.ToIntValue() ?? 0),
                memberBannedRights?.UntilDate ?? bannedDefaultRights.UntilDate);

            CheckContentBannedRights(input, effectiveBannedRights);
        }

        //if (channelReadModel.SlowModeEnabled)
        //{

        //}

        return channelReadModel;
    }

    /// <summary>
    /// Applies the granular banned rights to the content of the message being sent.
    /// Every media type has a right of its own, so a group that allows photos but not voice
    /// messages has to be enforced here and not only in the client UI.
    /// See https://corefork.telegram.org/api/rights
    /// </summary>
    private static void CheckContentBannedRights(SendMessageInput input, ChatBannedRights bannedRights)
    {
        if (IsPoll(input))
        {
            if (bannedRights.SendPolls)
            {
                RpcErrors.RpcErrors403.ChatSendPollForbidden.ThrowRpcError();
            }

            return;
        }

        if (input.Media is not null and not TMessageMediaEmpty)
        {
            // send_media is the umbrella right: when it is denied no media of any kind may be sent.
            if (bannedRights.SendMedia)
            {
                RpcErrors.RpcErrors403.ChatSendMediaForbidden.ThrowRpcError();
            }

            GetMediaBannedRightsError(input.Media, bannedRights)?.ThrowRpcError();
        }
        else if (bannedRights.SendPlain)
        {
            // send_plain covers text-only messages; media captions are governed by the media rights.
            RpcErrors.RpcErrors403.ChatSendPlainForbidden.ThrowRpcError();
        }

        if (bannedRights.EmbedLinks && HasLink(input))
        {
            RpcErrors.RpcErrors403.ChatSendWebpageForbidden.ThrowRpcError();
        }
    }

    /// <summary>
    /// The error for the media-specific right that forbids this media, or <c>null</c> when the media
    /// is allowed. Media without a right of its own (contacts, geo, venues, web pages) is only
    /// governed by <c>send_media</c>, which the caller has already checked.
    /// </summary>
    internal static RpcError? GetMediaBannedRightsError(IMessageMedia media, ChatBannedRights bannedRights)
    {
        return media switch
        {
            TMessageMediaPhoto or TMessageMediaPaidMedia =>
                bannedRights.SendPhotos ? RpcErrors.RpcErrors403.ChatSendPhotosForbidden : null,
            TMessageMediaDocument document => GetDocumentBannedRightsError(document, bannedRights),
            TMessageMediaGame =>
                bannedRights.SendGames ? RpcErrors.RpcErrors403.ChatSendGameForbidden : null,
            // A dice is governed by send_stickers, not send_games, however much it looks like a game:
            // TDLib refuses it with "Not enough rights to send dice to the chat" under can_send_stickers
            // (MessageContent.cpp), and Android gates the send on canSendStickers (SendMessagesHelper).
            // Under send_games a client would offer a dice the server then refuses, and vice versa.
            TMessageMediaDice =>
                bannedRights.SendStickers ? RpcErrors.RpcErrors403.ChatSendStickersForbidden : null,
            _ => null
        };
    }

    /// <summary>
    /// Documents carry the actual kind in their attributes: stickers, GIFs, round video messages,
    /// voice notes, videos and music each map to a different banned right. The order follows the
    /// client's own classification (sticker → animation → round → voice → video → audio → file).
    /// </summary>
    private static RpcError? GetDocumentBannedRightsError(TMessageMediaDocument media,
        ChatBannedRights bannedRights)
    {
        var attributes = (media.Document as TDocument)?.Attributes;
        var video = attributes?.OfType<TDocumentAttributeVideo>().FirstOrDefault();
        var audio = attributes?.OfType<TDocumentAttributeAudio>().FirstOrDefault();

        if (attributes?.Any(p => p is TDocumentAttributeSticker or TDocumentAttributeCustomEmoji) == true)
        {
            return bannedRights.SendStickers ? RpcErrors.RpcErrors403.ChatSendStickersForbidden : null;
        }

        if (attributes?.Any(p => p is TDocumentAttributeAnimated) == true)
        {
            return bannedRights.SendGifs ? RpcErrors.RpcErrors403.ChatSendGifsForbidden : null;
        }

        if (media.Round || video?.RoundMessage == true)
        {
            return bannedRights.SendRoundVideos ? RpcErrors.RpcErrors403.ChatSendRoundvideosForbidden : null;
        }

        if (media.Voice || audio?.Voice == true)
        {
            return bannedRights.SendVoices ? RpcErrors.RpcErrors403.ChatSendVoicesForbidden : null;
        }

        if (media.Video || video != null)
        {
            return bannedRights.SendVideos ? RpcErrors.RpcErrors403.ChatSendVideosForbidden : null;
        }

        if (audio != null)
        {
            return bannedRights.SendAudios ? RpcErrors.RpcErrors403.ChatSendAudiosForbidden : null;
        }

        return bannedRights.SendDocs ? RpcErrors.RpcErrors403.ChatSendDocsForbidden : null;
    }

    /// <summary>
    /// True when the message contains a link, either as a text entity or as an attached web page
    /// preview, so the <c>embed_links</c> right applies.
    /// </summary>
    private static bool HasLink(SendMessageInput input)
    {
        if (input.Media is TMessageMediaWebPage)
        {
            return true;
        }

        return input.Entities?.Any(p => p is TMessageEntityUrl or TMessageEntityTextUrl) == true;
    }

    /// <summary>
    /// True when the message being sent is a poll, so the <c>send_polls</c> banned right
    /// applies. Covers both freshly created polls and forwarded ones.
    /// Checklists are governed by the same right — see TDLib
    /// <c>MessageContent.cpp</c>: <c>MessageContentType::ToDoList</c> requires
    /// <c>permissions.can_send_polls()</c>.
    /// </summary>
    private static bool IsPoll(SendMessageInput input)
    {
        return input.MessageType == MessageType.Poll
               || input.PollId != null
               || input.Media is TMessageMediaPoll or TMessageMediaToDo
               || input.InputMedia is TInputMediaPoll or TInputMediaTodo;
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
                subscriptionUntilDate: null,
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

    /// <summary>
    /// A reply may target a single checklist item via <c>todo_item_id</c>. If the message being
    /// replied to is not a checklist, the field is meaningless and is dropped, so clients never
    /// receive a task reference pointing at a message without tasks — matching TDLib, which resets
    /// <c>checklist_task_id</c> in the same situation.
    /// See https://corefork.telegram.org/api/todo
    /// </summary>
    private async Task ClearTodoItemIdIfNotChecklistAsync(SendMessageInput input, long ownerPeerId, int? replyToMsgId)
    {
        if (input.InputReplyTo is not TInputReplyToMessage { TodoItemId: > 0 } replyToMessage)
        {
            return;
        }

        if (replyToMsgId is not > 0)
        {
            replyToMessage.TodoItemId = null;
            return;
        }

        var repliedMessage = await queryProcessor.ProcessAsync(
            new GetMessageByPeerIdAndMessageIdQuery(ownerPeerId, replyToMsgId.Value));
        if (repliedMessage?.Media2 is not TMessageMediaToDo todoMedia ||
            todoMedia.Todo.List.All(p => p.Id != replyToMessage.TodoItemId))
        {
            replyToMessage.TodoItemId = null;
        }
    }

    /// <param name="scheduleDateOverride">
    /// Set when the server itself queues the message (video processing), so it is queued at the
    /// estimated conversion date even though the client asked for an immediate send.
    /// </param>
    private async Task<SendMessageItem> CreateSendMessageItemAsync(SendMessageInput input,
        int? scheduleDateOverride = null,
        Dictionary<string, QuickReplyItem>? quickReplies = null)
    {
        // A message sent with quick_reply_shortcut is not delivered anywhere: it is stored as a message of
        // that shortcut. Clients address it to inputPeerSelf.
        // See https://corefork.telegram.org/api/business#quick-reply-shortcuts
        QuickReplyItem? quickReplyItem = null;
        if (input.InputQuickReplyShortcut != null)
        {
            if (input.ToPeer.PeerType is not (PeerType.Self or PeerType.User) ||
                input.ToPeer.PeerId != input.SenderUserId)
            {
                RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
            }

            quickReplyItem = await quickReplyService.ResolveForSendAsync(input.SenderUserId,
                input.InputQuickReplyShortcut, quickReplies ?? new(StringComparer.OrdinalIgnoreCase));
        }

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

        var entityResult = await ProcessMessageEntitiesAsync(input.Message, input.Entities, input.ToPeer);
        var entities = entityResult.Entities;
        var mentionedUserIds = entityResult.MentionedUserIds;
        var ownerPeerId = input.ToPeer.PeerType == PeerType.Channel ? input.ToPeer.PeerId : input.SenderUserId;
        var replyToMsgId = input.InputReplyTo.ToReplyToMsgId();
        await ClearTodoItemIdIfNotChecklistAsync(input, ownerPeerId, replyToMsgId);
        var topMsgId = await ResolveThreadRootAsync(input, ownerPeerId);

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
            TMessageActionPaymentSentMe => MessageActionType.PaymentSentMe,
            TMessageActionPaymentSent => MessageActionType.PaymentSent,
            TMessageActionPaymentRefunded => MessageActionType.PaymentRefunded,
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

        var scheduleDate = quickReplyItem != null ? null : scheduleDateOverride ?? input.ScheduleDate;
        if (scheduleDate.HasValue && !scheduleDateOverride.HasValue &&
            ScheduledMessageRules.ShouldSendImmediately(scheduleDate.Value, CurrentDate))
        {
            // "If the schedule_date is less than 10 seconds in the future, the message will be sent
            // immediately, generating a normal updateNewMessage/updateNewChannelMessage."
            // See https://corefork.telegram.org/api/scheduled-messages
            scheduleDate = null;
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
        if (scheduleDate.HasValue)
        {
            // The id inside the schedule queue comes from the same per peer sequence as real message ids:
            // clients address a queued message with a plain message id (messages.editMessage,
            // messages.deleteScheduledMessages), so the two id spaces must never overlap.
            scheduleMessageId = input.MessageId.HasValue
                ? await idGenerator.NextIdAsync(IdType.MessageId, ownerPeerId)
                : messageId;
        }

        // An imported message keeps the timestamp it had in the chat it came from, so the history
        // lands under its own day separators instead of all arriving "today".
        // See https://corefork.telegram.org/api/import
        var date = input.Date ?? CurrentDate;
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
            // A bot's inputKeyboardButtonUrlAuth is input-only; clients get keyboardButtonUrlAuth.
            ReplyMarkup: await urlAuthService.NormalizeAsync(input.SenderUserId, input.ReplyMarkup),
            TopMsgId: topMsgId,
            ForumTopic: topMsgId.HasValue && channelReadModel?.Forum == true,
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
            TtlPeriod: quickReplyItem != null ? null : input.TtlPeriod,
            NoForwards: quickReplyItem == null && (input.NoForwards || targetNoForwards),
            QuickReplyItem: quickReplyItem,
            IsBusinessAutoReply: input.IsBusinessAutoReply,
            ViaBusinessBotId: input.ViaBusinessBotId ?? input.RequestInfo.BusinessBotId
        );

        var sendMessageItem = new SendMessageItem(messageItem, input.ClearDraft, mentionedUserIds, []);

        return sendMessageItem;
    }

    /// <summary>
    /// Resolves the thread root a message belongs to. Clients normally send it as
    /// <c>inputReplyToMessage.top_msg_id</c>, but a reply posted from a plain message view carries only
    /// <c>reply_to_msg_id</c>; without the root, a reply to a comment would be invisible in the thread.
    /// The resolved value is also written back onto the request so the message keeps
    /// <c>reply_to_top_id</c>. See https://corefork.telegram.org/api/threads
    /// </summary>
    private async Task<int?> ResolveThreadRootAsync(SendMessageInput input, long ownerPeerId)
    {
        var inputReplyToMessage = input.InputReplyTo as TInputReplyToMessage;
        var topMsgId = input.TopMsgId ?? inputReplyToMessage?.TopMsgId;
        if (topMsgId.HasValue || inputReplyToMessage == null || input.ToPeer.PeerType != PeerType.Channel)
        {
            return topMsgId;
        }

        var repliedTo = await queryProcessor.ProcessAsync(
            new GetMessageByIdQuery(MessageId.Create(ownerPeerId, inputReplyToMessage.ReplyToMsgId).Value));

        // Only a reply to a message that is itself inside a thread inherits a root. A reply to a
        // top-level message keeps reply_to_msg_id alone, which already identifies the thread.
        topMsgId = repliedTo?.TopMsgId ?? (repliedTo?.ReplyToMsgId > 0 ? repliedTo.ReplyToMsgId : null);
        if (topMsgId.HasValue)
        {
            inputReplyToMessage.TopMsgId = topMsgId;
        }

        return topMsgId;
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
        return messageEntityService.GetHashtags(message);
    }

    /// <summary>
    /// Puts the messages into the schedule queue of their peer and answers the client with the
    /// <c>updateNewScheduledMessage</c> updates it expects.
    /// See https://corefork.telegram.org/api/scheduled-messages
    /// </summary>
    private async Task SaveScheduledMessagesAsync(List<ScheduledQueueItem> scheduledItems, RequestInfo requestInfo)
    {
        var documents = await scheduledMessageStore.SaveAsync(scheduledItems, requestInfo);
        var updates = scheduledMessageStore.BuildNewScheduledUpdates(documents, requestInfo.UserId, requestInfo.Layer);

        // Nothing else answers the request: a scheduled message never reaches the send saga, so the
        // domain event handler that normally produces the rpc result never runs.
        if (requestInfo.ReqMsgId != 0)
        {
            await objectMessageSender.SendRpcMessageToClientAsync(requestInfo, updates);
        }

        // In a broadcast channel the queue is shared, so every admin allowed to post is notified; the
        // clients key scheduled messages by peer, so the post drops straight into their scheduled view.
        // See https://corefork.telegram.org/api/scheduled-messages
        var toPeer = scheduledItems[0].Item.ToPeer;
        var audience = await scheduledMessageStore.GetQueueAudienceAsync(toPeer, requestInfo.UserId);
        foreach (var userId in audience)
        {
            await objectMessageSender.PushMessageToPeerAsync(new Peer(PeerType.User, userId), updates,
                excludeAuthKeyId: userId == requestInfo.UserId ? requestInfo.AuthKeyId : null);
        }
    }

    /// <summary>
    /// Validates every scheduling request of the batch before anything is written.
    /// </summary>
    private async Task ValidateScheduledMessagesAsync(List<SendMessageInput> inputs)
    {
        var scheduledInputs = inputs.Where(p => p.ScheduleDate.HasValue).ToList();
        if (scheduledInputs.Count == 0)
        {
            return;
        }

        foreach (var input in scheduledInputs)
        {
            if (ScheduledMessageRules.ShouldSendImmediately(input.ScheduleDate!.Value, CurrentDate))
            {
                continue;
            }

            await scheduledMessageStore.ValidateAsync(input.SenderUserId, input.ToPeer, input.ScheduleDate.Value,
                input.ScheduleRepeatPeriod, scheduledInputs.Count);
        }
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

    public Task<MessageEntityProcessingResult> ProcessMessageEntitiesAsync(
        string? message,
        IEnumerable<IMessageEntity>? entities,
        Peer toPeer)
    {
        return messageEntityService.ProcessAsync(message, entities, toPeer);
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
