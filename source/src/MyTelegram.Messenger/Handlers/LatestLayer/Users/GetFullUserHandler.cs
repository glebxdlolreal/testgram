using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Bots;
using MyTelegram.Messenger.Services.Business;
using MyTelegram.Messenger.Services.Impl;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Messenger.Services.StarGifts;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;
using TUserFull = MyTelegram.Schema.Users.TUserFull;
using Microsoft.Extensions.Logging;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Users;
/// <summary>
/// Returns extended user info by ID.
/// <para><c>See <a href="https://corefork.telegram.org/method/users.getFullUser"/> </c></para>
/// </summary>
internal sealed class GetFullUserHandler(IPeerHelper peerHelper, IQueryProcessor queryProcessor, IUserConverterService userConverterService, ILayeredService<IPeerSettingsConverter> peerSettingsLayeredService, ILayeredService<IPeerNotifySettingsConverter> peerNotifySettingsLayeredService, IBlockCacheAppService blockCacheAppService, IContactHelper contactHelper, IPeerSettingsAppService peerSettingsAppService, IPhotoAppService photoAppService, IUserAppService userAppService, IPrivacyAppService privacyAppService, IMongoDatabase mongoDatabase, IBotVerificationStore botVerificationStore, IPinnedMessageResolver pinnedMessageResolver, IAccessHashHelper2 accessHashHelper, IFromMessagePeerResolver fromMessagePeerResolver, IChatWallPaperService chatWallPaperService, IPeerTranslationSettingsStore peerTranslationSettings, IFileReferenceHelper fileReferenceHelper, IChatConverterService chatConverterService, IBusinessProfileService businessProfileService, IBusinessBotNotifier businessBotNotifier, ILogger<GetFullUserHandler> logger) : RpcResultObjectHandler<MyTelegram.Schema.Users.RequestGetFullUser, MyTelegram.Schema.Users.IUserFull>
{
    protected override async Task<MyTelegram.Schema.Users.IUserFull> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Users.RequestGetFullUser obj)
    {
        var selfUserId = input.UserId;

        // inputUserFromMessage carries no access hash — only the context the user was seen in — so
        // it has to be validated against that context before the profile is handed out, otherwise
        // the caller picks any user id they like. See https://corefork.telegram.org/api/min
        if (obj.Id is TInputUserFromMessage inputUserFromMessage)
        {
            await fromMessagePeerResolver.ResolveUserIdAsync(input, inputUserFromMessage.Peer,
                inputUserFromMessage.MsgId, inputUserFromMessage.UserId);
        }
        else
        {
            await accessHashHelper.CheckAccessHashAsync(input, obj.Id);
        }

        var targetPeer = peerHelper.GetPeer(obj.Id, input.UserId);
        var targetUserId = targetPeer.PeerId;
        var userReadModel = await userAppService.GetAsync(targetPeer.PeerId);
        if (userReadModel == null)
        {
            throw new RpcException(RpcErrors.RpcErrors400.UserIdInvalid);
        }

        var photoReadModels = await photoAppService.GetPhotosAsync(userReadModel);
        var privacyReadModels = await privacyAppService.GetPrivacyListAsync(targetUserId);
        var contactReadModels = await queryProcessor.ProcessAsync(new GetContactListBySelfIdAndTargetUserIdQuery(input.UserId, targetUserId));
        var myContactReadModel = contactReadModels?.FirstOrDefault(p => p.SelfUserId == selfUserId && p.TargetUserId == targetUserId);
        var targetUserContactReadModel = contactReadModels?.FirstOrDefault(p => p.SelfUserId == targetUserId && p.TargetUserId == selfUserId);
        var peerNotifySettingsId = PeerNotifySettingsId.Create(selfUserId, targetPeer.PeerType, targetPeer.PeerId);
        var peerNotifySettingReadModel = await queryProcessor.ProcessAsync(new GetPeerNotifySettingsByIdQuery(peerNotifySettingsId.Value));
        var peerSettingReadModel = await peerSettingsAppService.GetPeerSettingsAsync(input.UserId, targetPeer.PeerId);
        var contactType = contactHelper.GetContactType(myContactReadModel, targetUserContactReadModel);
        var peerSettings = peerSettingsLayeredService.GetConverter(input.Layer).ToPeerSettings(input.UserId, targetPeer.PeerId, peerSettingReadModel, contactType);

        // Add business bot fields to peer settings
        await SetBusinessBotFieldsAsync(peerSettings, selfUserId, targetUserId);

        var peerNotifySettings = peerNotifySettingsLayeredService.GetConverter(input.Layer).ToPeerNotifySettings(peerNotifySettingReadModel?.NotifySettings ?? PeerNotifySettings.DefaultSettings);
        var userFull = userConverterService.ToUserFull(input, userReadModel, photoReadModels, contactReadModels, privacyReadModels, input.Layer);
        userFull.Settings = peerSettings;
        userFull.NotifySettings = peerNotifySettings;
        userFull.Blocked = await blockCacheAppService.IsBlockedAsync(input.UserId, targetPeer.PeerId);
        var user = userConverterService.ToUser(input, userReadModel, photoReadModels, myContactReadModel, targetUserContactReadModel, privacyReadModels, input.Layer);
        await businessProfileService.FillUserFullAsync(userReadModel, userFull, input.UserId == targetUserId);
        var personalChannel = await SetPersonalChannelAsync(input, userReadModel, userFull);
        await SetCommonChatCountAsync(input, userReadModel, userFull);
        await SetStarGiftsCountAsync(targetUserId, userFull, input.UserId);
        await SetStarsRatingAsync(targetUserId, userFull, isSelf: input.UserId == targetUserId);
        await SetDisallowedGiftsAsync(targetUserId, userFull);
        await SetBotVerificationAsync(targetUserId, userFull, user);
        await SetBotInfoAsync(targetUserId, userFull);
        await SetUserStoriesAsync(targetUserId, userFull, input.UserId);
        await SetSavedMusicAsync(targetUserId, userFull, input.UserId);
        await SetChatThemeAsync(input.UserId, targetUserId, userFull);
        await SetNoForwardsAsync(input.UserId, targetUserId, userFull);
        await SetStarRefProgramAsync(targetUserId, userFull);
        await SetPinnedMsgIdAsync(input.UserId, targetUserId, userFull);
        await SetBotCanManageEmojiStatusAsync(input.UserId, targetUserId, userReadModel, userFull);
        await SetWallPaperAsync(input.UserId, targetUserId, userFull);

        // Whether this caller dismissed the translation popup for this chat. Per account, per peer, so
        // it belongs here rather than in UserFullMapper, which never sees who is asking.
        // See https://corefork.telegram.org/api/translation
        userFull.TranslationsDisabled =
            await peerTranslationSettings.IsDisabledAsync(input.UserId, targetPeer);

        // CRITICAL: Cast to concrete TUser for proper serialization
        // ILayeredUser interface doesn't serialize correctly in TVector
        var tUser = user as TUser;
        var usersList = new List<IUser>();
        if (tUser != null)
        {
            usersList.Add(tUser);
        }

        return new TUserFull
        {
            Chats = personalChannel == null ? new TVector<IChat>() : new TVector<IChat>(personalChannel),
            FullUser = userFull,
            Users = new TVector<IUser>(usersList)
        };
    }
    
    private async Task SetUserStoriesAsync(long targetUserId, IUserFull userFull, long requesterId)
    {
        var storyCollection = mongoDatabase.GetCollection<StoryDocument>("stories");
        var currentTime = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        
        logger.LogDebug("SetUserStoriesAsync: targetUserId={TargetUserId}, requesterId={RequesterId}, currentTime={CurrentTime}", targetUserId, requesterId, currentTime);
        
        var filter = Builders<StoryDocument>.Filter.And(
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, targetUserId),
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, 0),
            Builders<StoryDocument>.Filter.Eq(s => s.Deleted, false),
            Builders<StoryDocument>.Filter.Eq(s => s.Archived, false),
            Builders<StoryDocument>.Filter.Lte(s => s.Date, currentTime),
            Builders<StoryDocument>.Filter.Gte(s => s.ExpireDate, currentTime)
        );
        
        var stories = await storyCollection.Find(filter)
            .SortByDescending(s => s.StoryId)
            .ToListAsync();
        
        logger.LogDebug("SetUserStoriesAsync: Found {Count} stories for targetUserId={TargetUserId}", stories.Count, targetUserId);
        
        if (stories.Count > 0)
        {
            foreach (var story in stories)
            {
                logger.LogDebug("  Story: StoryId={StoryId}, Date={Date}, ExpireDate={ExpireDate}, OwnerPeerId={OwnerPeerId}, OwnerPeerType={OwnerPeerType}, Deleted={Deleted}, Archived={Archived}",
                    story.StoryId, story.Date, story.ExpireDate, story.OwnerPeerId, story.OwnerPeerType, story.Deleted, story.Archived);
            }
            
            var storyItems = new TVector<MyTelegram.Schema.IStoryItem>();
            foreach (var story in stories)
            {
                storyItems.Add(StoryHelper.ConvertToStoryItem(fileReferenceHelper, story, requesterId));
            }
            
            var peerStories = new MyTelegram.Schema.TPeerStories
            {
                Peer = new MyTelegram.Schema.TPeerUser { UserId = targetUserId },
                Stories = storyItems
            };
            
            userFull.Stories = peerStories;
            userFull.StoriesPinnedAvailable = stories.Any(s => s.Pinned);
            logger.LogDebug("SetUserStoriesAsync: Set Stories on userFull with {Count} story items", storyItems.Count);
        }
    }

    private async Task SetStarGiftsCountAsync(long userId, IUserFull userFull, long requesterId)
    {
        var collection = mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
        var filter = Builders<SavedStarGiftDocument>.Filter.Eq(d => d.OwnerUserId, userId);
        if (userId != requesterId)
            filter &= Builders<SavedStarGiftDocument>.Filter.Eq(d => d.Saved, true);
        var count = (int)await collection.CountDocumentsAsync(filter);
        if (count > 0)
            userFull.StargiftsCount = count;
    }

    // Levels calibrated for server scale: 1k → ~1B stars across levels 1-20
    private static readonly long[] RatingLevels = [0, 1_000, 3_000, 10_000, 30_000, 100_000, 300_000, 1_000_000, 3_000_000, 10_000_000, 30_000_000, 100_000_000, 300_000_000, 1_000_000_000];

    private static (int level, long current, long? next) CalcRatingLevel(long totalStars)
    {
        // Beyond the fixed table: each level multiplies threshold by 3
        long threshold = RatingLevels[^1];
        int levelIndex = RatingLevels.Length - 1;
        while (totalStars >= threshold * 3)
        {
            threshold *= 3;
            levelIndex++;
        }
        if (totalStars < RatingLevels[^1])
        {
            for (int i = RatingLevels.Length - 1; i >= 0; i--)
                if (totalStars >= RatingLevels[i]) { levelIndex = i; break; }
            long current = RatingLevels[levelIndex];
            long? next = levelIndex + 1 < RatingLevels.Length ? RatingLevels[levelIndex + 1] : null;
            return (levelIndex + 1, current, next);
        }
        return (levelIndex + 1, threshold, threshold * 3);
    }

    private async Task SetStarsRatingAsync(long userId, IUserFull userFull, bool isSelf = false)
    {
        var userReadModel = await userAppService.GetAsync(userId);
        if (userReadModel?.Bot == true) return;
        // Check for rating override
        var overrideCollection = mongoDatabase.GetCollection<BsonDocument>("user-rating-overrides");
        var overrideDoc = await (await overrideCollection.FindAsync(new BsonDocument("UserId", userId))).FirstOrDefaultAsync();
        if (overrideDoc != null)
        {
            var overrideLevel = overrideDoc["Level"].ToInt32();
            userFull.StarsRating = new TStarsRating { Level = overrideLevel, CurrentLevelStars = 0, Stars = 0, NextLevelStars = null };
            return;
        }

        var collection = mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
        var pipeline = new[]
        {
            new BsonDocument("$match", new BsonDocument
            {
                { "FromUserId", userId },
                { "IsAuction", new BsonDocument("$ne", true) }
            }),
            new BsonDocument("$group", new BsonDocument { { "_id", BsonNull.Value }, { "total", new BsonDocument("$sum", "$Stars") } })
        };
        var result = await collection.AggregateAsync<BsonDocument>(pipeline);
        var doc = await result.FirstOrDefaultAsync();
        var totalStars = doc != null && doc.Contains("total") ? doc["total"].ToInt64() : 0;

        // Telegram omits stars_rating entirely until there is actual rating
        // activity. Returning an explicit level 0 / 0 stars object makes every
        // freshly registered account look like it already participates in the
        // public stars ladder.
        if (totalStars <= 0)
        {
            return;
        }

        var (level, currentLevelStars, nextLevelStars) = CalcRatingLevel(totalStars);

        userFull.StarsRating = new TStarsRating
        {
            Level = level,
            CurrentLevelStars = currentLevelStars,
            Stars = totalStars,
            NextLevelStars = nextLevelStars ?? currentLevelStars * 3,
        };

        // Pending rating only visible to self
        if (isSelf)
        {
            var nextThreshold = nextLevelStars ?? currentLevelStars * 3;
            var (nextLevel, nextCurrent, nextNext) = CalcRatingLevel(nextThreshold);
            userFull.StarsMyPendingRating = new TStarsRating
            {
                Level = nextLevel,
                CurrentLevelStars = nextCurrent,
                Stars = nextThreshold,
                NextLevelStars = nextNext ?? nextCurrent * 3,
            };
            userFull.StarsMyPendingRatingDate = (int)DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        }
    }

    private async Task SetDisallowedGiftsAsync(long userId, IUserFull userFull)
    {
        var gps = await queryProcessor.ProcessAsync(new GetGlobalPrivacySettingsQuery(userId));
        if (gps == null) return;
        if (gps.DisallowUnlimitedStargifts || gps.DisallowLimitedStargifts || gps.DisallowUniqueStargifts || gps.DisallowPremiumGifts)
        {
            userFull.DisallowedGifts = new TDisallowedGiftsSettings
            {
                DisallowUnlimitedStargifts = gps.DisallowUnlimitedStargifts,
                DisallowLimitedStargifts = gps.DisallowLimitedStargifts,
                DisallowUniqueStargifts = gps.DisallowUniqueStargifts,
                DisallowPremiumGifts = gps.DisallowPremiumGifts,
            };
        }
    }

    private async Task SetCommonChatCountAsync(IRequestInput input, IUserReadModel userReadModel, IUserFull userFull)
    {
        var count = await queryProcessor.ProcessAsync(new GetCommonChatCountQuery(input.UserId, userReadModel.UserId));
        userFull.CommonChatsCount = count;
    }

    /// <summary>
    /// Fills <c>personal_channel_id</c>/<c>personal_channel_message</c> and returns the channel itself:
    /// clients resolve the id against <c>users.userFull.chats</c>, so the field alone shows nothing.
    /// </summary>
    private async Task<IChat?> SetPersonalChannelAsync(IRequestInput input, IUserReadModel userReadModel, IUserFull userFull)
    {
        if (!userReadModel.PersonalChannelId.HasValue)
        {
            return null;
        }

        var channelId = userReadModel.PersonalChannelId.Value;
        var channelTopMessageId = await queryProcessor.ProcessAsync(new GetChannelTopMessageIdQuery(channelId));
        if (!channelTopMessageId.HasValue)
        {
            return null;
        }

        var channel = await chatConverterService.GetChannelAsync(input, channelId, true, null, input.Layer, throwIfNotFound: false);
        if (channel == null)
        {
            return null;
        }

        userFull.PersonalChannelId = channelId;
        userFull.PersonalChannelMessage = channelTopMessageId.Value;
        return channel;
    }

    /// <summary>
    /// The <a href="https://corefork.telegram.org/api/bots/verification">third-party verification</a>
    /// badge of the profile being opened: the full constructor on <c>userFull</c>, the bare icon on
    /// the <c>user</c> that travels with it.
    /// </summary>
    private async Task SetBotVerificationAsync(long userId, IUserFull? userFull, ILayeredUser? user)
    {
        var doc = await botVerificationStore.GetForUserAsync(userId);
        if (doc == null || doc.Icon == 0) return;

        var bv = new TBotVerification { BotId = doc.BotId, Icon = doc.Icon, Description = doc.Description };
        if (userFull != null) userFull.BotVerification = bv;
        if (user is TUser tUser) tUser.BotVerificationIcon = doc.Icon;
    }

    /// <summary>
    /// <c>botInfo</c> of a bot profile. <c>verifier_settings</c> lives in here, and it is the flag
    /// official clients gate the whole verification UI on, so it must not replace the rest of the
    /// bot info the owner set through <c>bots.setBotInfo</c>.
    /// </summary>
    private async Task SetBotInfoAsync(long botUserId, IUserFull userFull)
    {
        var botUser = await userAppService.GetAsync(botUserId);
        if (botUser == null || !botUser.Bot)
            return;

        var botInfo = new TBotInfo
        {
            UserId = botUserId,
            Commands = await GetBotCommandsAsync(botUserId),
            MenuButton = await GetBotMenuButtonAsync(botUserId)
        };

        var infoDoc = await mongoDatabase.GetCollection<BsonDocument>("bot_info")
            .Find(Builders<BsonDocument>.Filter.Eq("BotId", botUserId) &
                  Builders<BsonDocument>.Filter.Eq("LangCode", ""))
            .FirstOrDefaultAsync();
        if (infoDoc != null && infoDoc.TryGetValue("Description", out var description) && description.IsString)
        {
            botInfo.Description = description.AsString;
        }

        // The "What can this bot do?" picture from @BotFather's /setdescriptionpic, stored as the
        // messageMedia it was sent as: a photo, or an animation document.
        if (infoDoc != null && infoDoc.TryGetValue("DescriptionMedia", out var descriptionMedia) &&
            descriptionMedia.BsonType == BsonType.Binary)
        {
            var mediaBuffer = new ReadOnlyMemory<byte>(descriptionMedia.AsByteArray);
            switch (mediaBuffer.Read<IMessageMedia>())
            {
                case TMessageMediaPhoto { Photo: { } photo }:
                    botInfo.DescriptionPhoto = photo;
                    break;
                case TMessageMediaDocument { Document: { } document }:
                    botInfo.DescriptionDocument = document;
                    break;
            }
        }

        userFull.BotGroupAdminRights = await MyTelegram.Messenger.Services.Bots.BotDefaultAdminRights.GetAsync(
            mongoDatabase, botUserId, MyTelegram.Messenger.Services.Bots.BotDefaultAdminRights.Group);
        userFull.BotBroadcastAdminRights = await MyTelegram.Messenger.Services.Bots.BotDefaultAdminRights.GetAsync(
            mongoDatabase, botUserId, MyTelegram.Messenger.Services.Bots.BotDefaultAdminRights.Broadcast);

        // Set with @BotFather's /setprivacypolicy; clients link it from the bot's profile.
        if (infoDoc != null && infoDoc.TryGetValue("PrivacyPolicyUrl", out var privacyPolicyUrl) &&
            privacyPolicyUrl.IsString)
        {
            botInfo.PrivacyPolicyUrl = privacyPolicyUrl.AsString;
        }

        var settings = await botVerificationStore.GetVerifierSettingsAsync(botUserId);
        if (settings != null)
        {
            botInfo.VerifierSettings = new TBotVerifierSettings
            {
                Icon = settings.Icon,
                Company = settings.Company,
                CanModifyCustomDescription = settings.CanModifyCustomDescription,
                CustomDescription = settings.CustomDescription
            };
        }

        userFull.BotInfo = botInfo;
    }

    /// <summary>
    /// The default-scope commands written by <c>bots.setBotCommands</c>. They travel inside
    /// <c>botInfo</c>, so leaving them out would empty the "/" menu of every bot that has some.
    /// </summary>
    private async Task<TVector<IBotCommand>> GetBotCommandsAsync(long botUserId)
    {
        var commands = new TVector<IBotCommand>();

        var doc = await mongoDatabase.GetCollection<BsonDocument>("bot_commands")
            .Find(Builders<BsonDocument>.Filter.Eq("BotId", botUserId) &
                  Builders<BsonDocument>.Filter.Eq("ScopeType", nameof(TBotCommandScopeDefault)) &
                  Builders<BsonDocument>.Filter.Eq("PeerId", BsonNull.Value) &
                  Builders<BsonDocument>.Filter.Eq("LangCode", ""))
            .FirstOrDefaultAsync();

        if (doc == null || !doc.TryGetValue("Commands", out var value) || !value.IsBsonArray)
        {
            return commands;
        }

        foreach (var command in value.AsBsonArray.Where(p => p.IsBsonDocument).Select(p => p.AsBsonDocument))
        {
            commands.Add(new TBotCommand
            {
                Command = command.GetValue("Command", string.Empty).AsString,
                Description = command.GetValue("Description", string.Empty).AsString
            });
        }

        return commands;
    }

    /// <summary>The menu button set by <c>bots.setBotMenuButton</c> for all users of the bot.</summary>
    private async Task<IBotMenuButton> GetBotMenuButtonAsync(long botUserId)
    {
        var doc = await mongoDatabase.GetCollection<BsonDocument>("bot_menu_buttons")
            .Find(Builders<BsonDocument>.Filter.Eq("BotId", botUserId) &
                  Builders<BsonDocument>.Filter.Eq("UserId", BsonNull.Value))
            .FirstOrDefaultAsync();

        if (doc == null || !doc.TryGetValue("Button", out var value) || !value.IsBsonDocument)
        {
            return new TBotMenuButtonDefault();
        }

        var button = value.AsBsonDocument;

        return button.GetValue("Type", string.Empty).AsString switch
        {
            nameof(TBotMenuButton) => new TBotMenuButton
            {
                Text = button.GetValue("Text", string.Empty).AsString,
                Url = button.GetValue("Url", string.Empty).AsString
            },
            nameof(TBotMenuButtonCommands) => new TBotMenuButtonCommands(),
            _ => new TBotMenuButtonDefault()
        };
    }

    private Task SetBusinessBotFieldsAsync(Schema.IPeerSettings settings, long selfUserId, long targetUserId)
    {
        return businessBotNotifier.FillPeerSettingsAsync(settings, selfUserId, targetUserId);
    }

    private async Task SetSavedMusicAsync(long targetUserId, IUserFull userFull, long selfUserId)
    {
        try
        {
            // privacyKeySavedMusic: leave userFull.saved_music unset for disallowed viewers,
            // otherwise the pinned song leaks through the profile even though users.getSavedMusic
            // would refuse to list it.
            if (targetUserId != selfUserId)
            {
                var allowed = true;
                await privacyAppService.ApplyPrivacyAsync(selfUserId, targetUserId,
                    _ => allowed = false,
                    [PrivacyType.SavedMusic]);

                if (!allowed)
                {
                    return;
                }
            }

            // Query saved music from MongoDB
            var collection = mongoDatabase.GetCollection<BsonDocument>("saved_music");
            var filter = Builders<BsonDocument>.Filter.Eq("UserId", targetUserId);
            var sort = Builders<BsonDocument>.Sort.Ascending("Order"); // Sort by Order field

            // Get the first (lowest order = most recent) saved music document
            var savedMusicDoc = await collection.Find(filter)
                .Sort(sort)
                .Limit(1)
                .FirstOrDefaultAsync();

            if (savedMusicDoc != null)
            {
                var documentId = savedMusicDoc["DocumentId"].AsInt64;

                // Load document from eventflow-documentreadmodel
                var docCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");
                var docFilter = Builders<BsonDocument>.Filter.Eq("DocumentId", documentId);
                var docDoc = await docCollection.Find(docFilter).FirstOrDefaultAsync();

                if (docDoc != null)
                {
                    userFull.SavedMusic = ConvertToDocument(docDoc);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load saved music for user {UserId}", targetUserId);
        }
    }

    private IDocument ConvertToDocument(BsonDocument doc)
    {
        var attributes = new TVector<IDocumentAttribute>();

        // Use Attributes2 if available (proper serialization)
        if (doc.Contains("Attributes2") && !doc["Attributes2"].IsBsonNull)
        {
            var attrs2 = doc["Attributes2"].AsBsonArray;
            foreach (var attrBson in attrs2)
            {
                if (attrBson.IsBsonDocument)
                {
                    var attrDoc = attrBson.AsBsonDocument;
                    var typeName = attrDoc["_t"].AsString;

                    // Handle audio attributes
                    if (typeName.EndsWith("TDocumentAttributeAudio"))
                    {
                        var audioAttr = new TDocumentAttributeAudio
                        {
                            Voice = attrDoc.Contains("Voice") && attrDoc["Voice"].AsBoolean,
                            Duration = attrDoc.Contains("Duration") ? attrDoc["Duration"].AsInt32 : 0,
                            Title = attrDoc.Contains("Title") ? attrDoc["Title"].AsString : null,
                            Performer = attrDoc.Contains("Performer") ? attrDoc["Performer"].AsString : null,
                            Waveform = attrDoc.Contains("Waveform") && !attrDoc["Waveform"].IsBsonNull
                                ? attrDoc["Waveform"].AsByteArray : null
                        };
                        attributes.Add(audioAttr);
                    }
                    // Handle filename attributes
                    else if (typeName.EndsWith("TDocumentAttributeFilename"))
                    {
                        attributes.Add(new TDocumentAttributeFilename
                        {
                            FileName = attrDoc.Contains("FileName") ? attrDoc["FileName"].AsString : ""
                        });
                    }
                }
            }
        }

        var documentId = doc["DocumentId"].AsInt64;

        return new TDocument
        {
            Id = documentId,
            AccessHash = doc["AccessHash"].AsInt64,
            // See https://corefork.telegram.org/api/file-references
            FileReference = fileReferenceHelper.Create(AccessHashType.Document, documentId),
            Date = doc["Date"].AsInt32,
            MimeType = doc.Contains("MimeType") ? doc["MimeType"].AsString : "audio/mpeg",
            Size = doc.Contains("Size") ? doc["Size"].AsInt64 : 0,
            Thumbs = new TVector<IPhotoSize>(),
            VideoThumbs = new TVector<IVideoSize>(),
            DcId = doc.Contains("DcId") ? doc["DcId"].AsInt32 : 2,
            Attributes = attributes
        };
    }

    private async Task SetChatThemeAsync(long selfUserId, long targetUserId, IUserFull userFull)
    {
        try
        {
            // Load chat theme from MongoDB
            var collection = mongoDatabase.GetCollection<BsonDocument>("user_chat_themes");
            var filter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("UserId", selfUserId),
                Builders<BsonDocument>.Filter.Eq("PeerType", 0), // PeerType.User
                Builders<BsonDocument>.Filter.Eq("PeerId", targetUserId)
            );

            var themeDoc = await collection.Find(filter).FirstOrDefaultAsync();
            if (themeDoc != null)
            {
                // Check if it's an emoji theme or gift theme
                if (themeDoc.Contains("Emoticon") && !string.IsNullOrEmpty(themeDoc["Emoticon"].AsString))
                {
                    userFull.Theme = new MyTelegram.Schema.TChatTheme
                    {
                        Emoticon = themeDoc["Emoticon"].AsString
                    };
                }
                else if (themeDoc.Contains("GiftSlug") && !string.IsNullOrEmpty(themeDoc["GiftSlug"].AsString))
                {
                    // Load unique gift theme
                    var giftSlug = themeDoc["GiftSlug"].AsString;
                    var uniqueGiftsCol = mongoDatabase.GetCollection<BsonDocument>("unique-star-gifts");
                    var giftDoc = await uniqueGiftsCol.Find(
                        Builders<BsonDocument>.Filter.Eq("Slug", giftSlug)
                    ).FirstOrDefaultAsync();

                    if (giftDoc != null)
                    {
                        var starGift = new MyTelegram.Schema.TStarGiftUnique
                        {
                            ThemeAvailable = true,
                            Id = giftDoc["UniqueId"].ToInt64(),
                            GiftId = giftDoc["GiftId"].ToInt64(),
                            Title = giftDoc["Title"].AsString,
                            Slug = giftDoc["Slug"].AsString,
                            Num = giftDoc["Num"].AsInt32,
                            Attributes = new TVector<MyTelegram.Schema.IStarGiftAttribute>(),
                            AvailabilityIssued = giftDoc["AvailabilityIssued"].AsInt32,
                            AvailabilityTotal = giftDoc["AvailabilityTotal"].AsInt32
                        };

                        // Theme is owned by the gift type: load it from star-gifts
                        // by GiftId so freshly upgraded NFTs inherit the theme.
                        var giftId = giftDoc.Contains("GiftId") ? giftDoc["GiftId"].ToInt64() : 0L;
                        var giftTypeDoc = await mongoDatabase.GetCollection<BsonDocument>("star-gifts")
                            .Find(Builders<BsonDocument>.Filter.Eq("GiftId", giftId))
                            .FirstOrDefaultAsync();

                        var themeSettings = StarGiftThemeHelper.LoadThemeSettings(giftDoc, giftTypeDoc)
                            ?? new TVector<MyTelegram.Schema.IThemeSettings>();

                        userFull.Theme = new MyTelegram.Schema.TChatThemeUniqueGift
                        {
                            Gift = starGift,
                            ThemeSettings = themeSettings
                        };
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load chat theme for user {SelfUserId} -> {TargetUserId}", selfUserId, targetUserId);
        }
    }

    private async Task SetStarRefProgramAsync(long targetUserId, IUserFull userFull)
    {
        try
        {
            // Check if target user is a bot with active affiliate program
            var programsCol = mongoDatabase.GetCollection<BsonDocument>("star_ref_programs");
            var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var filter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("bot_id", targetUserId),
                Builders<BsonDocument>.Filter.Or(
                    Builders<BsonDocument>.Filter.Not(Builders<BsonDocument>.Filter.Exists("end_date")),
                    Builders<BsonDocument>.Filter.Eq("end_date", BsonNull.Value),
                    Builders<BsonDocument>.Filter.Gt("end_date", now)
                )
            );

            var program = await programsCol.Find(filter).FirstOrDefaultAsync();
            if (program != null)
            {
                long dailyRevenue = 100; // Default estimate
                if (program.Contains("daily_revenue_per_user"))
                {
                    var revenueValue = program["daily_revenue_per_user"];
                    dailyRevenue = revenueValue.BsonType == BsonType.Int64
                        ? revenueValue.AsInt64
                        : revenueValue.AsInt32;
                }

                userFull.StarrefProgram = new TStarRefProgram
                {
                    BotId = targetUserId,
                    CommissionPermille = program["commission_permille"].AsInt32,
                    DurationMonths = program.Contains("duration_months") && program["duration_months"].AsInt32 > 0
                        ? program["duration_months"].AsInt32
                        : null,
                    DailyRevenuePerUser = new TStarsAmount
                    {
                        Amount = dailyRevenue,
                        Nanos = 0
                    }
                };
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load star ref program for bot {BotId}", targetUserId);
        }
    }

    private async Task SetBotCanManageEmojiStatusAsync(long selfUserId, long targetBotId, IUserReadModel targetUser, IUserFull userFull)
    {
        if (!targetUser.Bot)
            return;

        try
        {
            var permissionCol = mongoDatabase.GetCollection<BsonDocument>("bot_emoji_status_permissions");
            var filter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("BotId", targetBotId),
                Builders<BsonDocument>.Filter.Eq("UserId", selfUserId)
            );
            var permissionDoc = await permissionCol.Find(filter).FirstOrDefaultAsync();
            if (permissionDoc != null && permissionDoc.Contains("Enabled") && permissionDoc["Enabled"].AsBoolean)
            {
                userFull.BotCanManageEmojiStatus = true;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to check bot emoji status permission for bot {BotId} user {UserId}", targetBotId, selfUserId);
        }
    }

    private async Task SetPinnedMsgIdAsync(long selfUserId, long targetUserId, IUserFull userFull)
    {
        var pinnedMsgId = await pinnedMessageResolver.GetPrivateChatPinnedMsgIdAsync(selfUserId, targetUserId);
        if (pinnedMsgId.HasValue)
        {
            userFull.PinnedMsgId = pinnedMsgId.Value;
        }
    }

    /// <summary>
    /// The wallpaper the caller set for this chat with <c>messages.setChatWallPaper</c>, or the one
    /// the other side set for both. Without it the wallpaper is stored but never reported, and the
    /// client falls back to the default one on every fresh start.
    /// See https://corefork.telegram.org/api/wallpapers
    /// </summary>
    private async Task SetWallPaperAsync(long selfUserId, long targetUserId, IUserFull userFull)
    {
        var (wallPaper, overridden) = await chatWallPaperService.GetChatWallPaperAsync(selfUserId,
            new Peer(PeerType.User, targetUserId));
        if (wallPaper == null)
        {
            return;
        }

        userFull.Wallpaper = wallPaper;
        userFull.WallpaperOverridden = overridden;
    }

    private async Task SetNoForwardsAsync(long selfUserId, long targetUserId, IUserFull userFull)
    {
        var collection = mongoDatabase.GetCollection<PrivateChatNoForwardsDocument>("private_chat_noforwards");
        var enabledFilter = Builders<PrivateChatNoForwardsDocument>.Filter.Eq(x => x.Enabled, true);

        var myFilter = Builders<PrivateChatNoForwardsDocument>.Filter.And(
            Builders<PrivateChatNoForwardsDocument>.Filter.Eq(x => x.OwnerUserId, selfUserId),
            Builders<PrivateChatNoForwardsDocument>.Filter.Eq(x => x.PeerUserId, targetUserId),
            enabledFilter);
        userFull.NoforwardsMyEnabled = await collection.Find(myFilter).AnyAsync();

        var peerFilter = Builders<PrivateChatNoForwardsDocument>.Filter.And(
            Builders<PrivateChatNoForwardsDocument>.Filter.Eq(x => x.OwnerUserId, targetUserId),
            Builders<PrivateChatNoForwardsDocument>.Filter.Eq(x => x.PeerUserId, selfUserId),
            enabledFilter);
        userFull.NoforwardsPeerEnabled = await collection.Find(peerFilter).AnyAsync();
    }
}
