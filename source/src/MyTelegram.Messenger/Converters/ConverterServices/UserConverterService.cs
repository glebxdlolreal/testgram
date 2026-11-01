using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services;
using MyTelegram.Messenger.Services.Bots;
using MyTelegram.Messenger.Services.Impl;
using MyTelegram.Messenger.Services.Privacy;
using MyTelegram.Messenger.Services.StarGifts;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using TPeerSettings = MyTelegram.Schema.TPeerSettings;

using MyTelegram.Converters.Mappers;

namespace MyTelegram.Messenger.Converters.ConverterServices;

public class UserConverterService(
    IQueryProcessor queryProcessor,
    IUserAppService userAppService,
    IPhotoAppService photoAppService,
    IPrivacyAppService privacyAppService,
    IPrivacyHelper privacyHelper,
    IContactHelper contactHelper,
    IAccessHashHelper2 accessHashHelper2,
    IUserStatusCacheAppService userStatusCacheAppService,
    ILayeredService<IUserConverter> userLayeredService,
    ILayeredService<IUserFullConverter> userFullLayeredService,
    ILayeredService<IEmojiStatusConverter> emojiStatusLayeredService,
    IEmojiStatusResolver emojiStatusResolver,
    ILayeredService<IPhotoConverter> photoLayeredService,
    IBotVerificationCache botVerificationCache,
    IMongoDatabase mongoDatabase) : IUserConverterService, ITransientDependency
{
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");

    public async Task<ILayeredUser> GetUserAsync(IRequestWithAccessHashKeyId request, long userId, bool skipSetContactProperties = true,
        bool skipCheckPrivacy = false, int layer = 0)
    {
        var userReadModel = await userAppService.GetAsync(userId);
        if (userReadModel == null)
        {
            throw new RpcException(RpcErrors.RpcErrors400.UserIdInvalid);
        }

        await botVerificationCache.EnsureFreshAsync();

        IReadOnlyCollection<IPrivacyReadModel>? privacyReadModels = null;
        IContactReadModel? myContactReadModel = null;
        IContactReadModel? targetUserContactReadModel = null;
        // Contacts are also required when evaluating privacy: without them the viewer looks
        // like a stranger and allowContacts rules would hide data from actual contacts.
        if (!skipSetContactProperties || !skipCheckPrivacy)
        {
            var contactReadModels =
                await queryProcessor.ProcessAsync(new GetContactListBySelfIdAndTargetUserIdQuery(request.UserId, userId));
            myContactReadModel =
                contactReadModels?.FirstOrDefault(p => p.SelfUserId == request.UserId && p.TargetUserId == userId);
            targetUserContactReadModel =
                contactReadModels?.FirstOrDefault(p => p.SelfUserId == userId && p.TargetUserId == request.UserId);
        }
        var photoReadModels = (await photoAppService.GetPhotosAsync(userReadModel, myContactReadModel)).ToDictionary(k => k.PhotoId);

        if (!skipCheckPrivacy)
        {
            privacyReadModels = await privacyAppService.GetPrivacyListAsync(userId);
        }

        return ToUserCore(request, userReadModel, photoReadModels, myContactReadModel, targetUserContactReadModel,
            privacyReadModels, layer);
    }

    public async Task<List<ILayeredUser>> GetUserListAsync(IRequestWithAccessHashKeyId request, List<long> userIds,
        bool skipSetContactProperties = true,
        bool skipCheckPrivacy = false, int layer = 0)
    {
        var userReadModels = await userAppService.GetListAsync(userIds);
        var photoReadModels = await photoAppService.GetPhotosAsync(userReadModels);
        IReadOnlyCollection<IPrivacyReadModel>? privacyReadModels = null;
        IReadOnlyCollection<IContactReadModel>? contactReadModels = null;
        if (!skipSetContactProperties || !skipCheckPrivacy)
        {
            contactReadModels = await queryProcessor.ProcessAsync(new GetContactListQuery(request.UserId, userIds));
        }

        if (!skipCheckPrivacy)
        {
            privacyReadModels = await privacyAppService.GetPrivacyListAsync(userIds);
        }

        return await ToUserListAsync(request, userReadModels, photoReadModels, contactReadModels, privacyReadModels, layer);
    }

    public IUserFull ToUserFull(IRequestWithAccessHashKeyId request,
        IUserReadModel userReadModel,
        IReadOnlyCollection<IPhotoReadModel>? photoReadModels,
        IReadOnlyCollection<IContactReadModel>? contactReadModels,
        IReadOnlyCollection<IPrivacyReadModel>? privacyReadModels, int layer = 0)
    {
        var userId = userReadModel.UserId;
        var isOfficialUserId = userId == MyTelegramConsts.NotificationServiceUserId;
        var phoneCallAvailable = !isOfficialUserId &&
                                 !userReadModel.Bot &&
                                 userId != request.UserId;
        var userFull = userFullLayeredService.GetConverter(layer).ToUserFull(userReadModel);
        userFull.CanPinMessage = !isOfficialUserId;

        if (userReadModel.IsDeleted == true)
        {
            userFull.Settings = new TPeerSettings
            {
                NeedContactsException = true
            };
            userFull.NotifySettings = new TPeerNotifySettings();

            return userFull;
        }

        userFull.PhoneCallsAvailable = phoneCallAvailable;
        userFull.VideoCallsAvailable = phoneCallAvailable;
        userFull.PhoneCallsPrivate = isOfficialUserId;

        var photos = photoReadModels?.ToDictionary(k => k.PhotoId) ?? [];

        if (userReadModel.ProfilePhotoId != null)
        {
            if (photos.TryGetValue(userReadModel.ProfilePhotoId.Value, out var profilePhotoReadModel))
            {
                userFull.ProfilePhoto = photoLayeredService.GetConverter(layer).ToPhoto(profilePhotoReadModel);
            }
        }

        if (userReadModel.FallbackPhotoId != null)
        {
            if (photos.TryGetValue(userReadModel.FallbackPhotoId.Value, out var fallbackPhotoReadModel))
            {
                userFull.FallbackPhoto = photoLayeredService.GetConverter(layer).ToPhoto(fallbackPhotoReadModel);
                var profilePhotoId = userReadModel.ProfilePhotoId;

                userFull.ProfilePhoto = profilePhotoId is null or 0 ? null : new TPhotoEmpty { Id = profilePhotoId.Value };
            }
        }

        if (request.UserId != userId)
        {
            var myContactReadModel =
                contactReadModels?.FirstOrDefault(p => p.SelfUserId == request.UserId && p.TargetUserId == userId);
            var targetUserContactReadModel =
                contactReadModels?.FirstOrDefault(p => p.SelfUserId == userId && p.TargetUserId == request.UserId);
            var contactType = contactHelper.GetContactType(myContactReadModel, targetUserContactReadModel);

            ApplyPrivacyToUserFull(request.UserId, userFull, privacyReadModels, contactType);

            if (myContactReadModel is { PhotoId: not null })
            {
                if (photos.TryGetValue(myContactReadModel.PhotoId.Value, out var photoReadModel))
                {
                    userFull.PersonalPhoto = photoLayeredService.GetConverter(layer).ToPhoto(photoReadModel);
                    userFull.ProfilePhoto ??= userFull.PersonalPhoto;
                }
            }
        }
        else
        {
            userFull.SendPaidMessagesStars = null;
        }

        if (userReadModel.Bot && IsBotOwner(userId, request.UserId))
        {
            // Bot monetization is gated client-side by userFull.can_view_revenue.
            // Balances are stored on the bot user ledger; expose the UI only to
            // the BotFather owner recorded in bot-owners.
            userFull.CanViewRevenue = true;
        }

        return userFull;
    }

    private bool IsBotOwner(long botUserId, long ownerUserId)
    {
        return mongoDatabase.GetCollection<BsonDocument>("bot-owners")
            .Find(Builders<BsonDocument>.Filter.Eq("BotId", botUserId) &
                  Builders<BsonDocument>.Filter.Eq("OwnerId", ownerUserId))
            .Limit(1)
            .Any();
    }

    public async Task<IUserFull> GetUserFullAsync(IRequestWithAccessHashKeyId request, long userId, int layer = 0)
    {
        var userReadModel = await userAppService.GetAsync(userId);
        var privacyReadModels = await privacyAppService.GetPrivacyListAsync(userId);
        IReadOnlyCollection<IContactReadModel>? contactReadModels = null;
        IContactReadModel? myContactReadModel = null;
        if (request.UserId != userId)
        {
            contactReadModels =
              await queryProcessor.ProcessAsync(new GetContactListBySelfIdAndTargetUserIdQuery(request.UserId, userId));
            myContactReadModel =
                contactReadModels?.FirstOrDefault(p => p.SelfUserId == request.UserId && p.TargetUserId == userId);
        }
        var photoReadModels = await photoAppService.GetPhotosAsync(userReadModel, myContactReadModel);

        return ToUserFull(request, userReadModel, photoReadModels, contactReadModels, privacyReadModels, layer);
    }

    public ILayeredUser ToUser(IRequestWithAccessHashKeyId request, IUserReadModel userReadModel, IReadOnlyCollection<IPhotoReadModel>? photoReadModels = null,
        IContactReadModel? contactReadModel = null, IContactReadModel? targetUserContactReadModel = null, IReadOnlyCollection<IPrivacyReadModel>? privacyReadModels = null, int layer = 0)
    {
        var photos = photoReadModels?.ToDictionary(k => k.PhotoId);

        return ToUserCore(request, userReadModel, photos, contactReadModel, targetUserContactReadModel,
            privacyReadModels, layer);
    }

    public async Task<List<ILayeredUser>> ToUserListAsync(IRequestWithAccessHashKeyId request, IReadOnlyCollection<IUserReadModel> userReadModels, IReadOnlyCollection<IPhotoReadModel>? photoReadModels = null,
        IReadOnlyCollection<IContactReadModel>? contactReadModels = null, IReadOnlyCollection<IPrivacyReadModel>? privacyReadModels = null, int layer = 0)
    {
        var userIds = userReadModels.Select(u => u.UserId).ToList();
        var storyMaxIdsWithLive = await GetStoriesMaxIdsAsync(userIds);
        await botVerificationCache.EnsureFreshAsync();

        var users = new List<ILayeredUser>();

        var photos = photoReadModels?
            .DistinctBy(p => p.PhotoId)
            .ToDictionary(k => k.PhotoId) ?? [];

        var targetUserContacts = contactReadModels?
             .Where(p => p.TargetUserId == request.UserId)
             .DistinctBy(p => p.SelfUserId)
             .ToDictionary(k => k.SelfUserId) ?? [];

        var myContacts = contactReadModels?
             .Where(p => p.SelfUserId == request.UserId)
             .DistinctBy(p => p.TargetUserId)
             .ToDictionary(k => k.TargetUserId) ?? [];

        var groupedPrivacyReadModels = privacyReadModels?.GroupBy(p => p.UserId).ToDictionary(k => k.Key, v => v.ToList()) ?? [];

        foreach (var userReadModel in userReadModels)
        {
            myContacts.TryGetValue(userReadModel.UserId, out var myContactReadModel);
            targetUserContacts.TryGetValue(userReadModel.UserId, out var targetUserContactReadModel);
            groupedPrivacyReadModels.TryGetValue(userReadModel.UserId, out var currentUserPrivacyReadModels);
            var user = ToUserCore(request, userReadModel, photos, myContactReadModel,
                targetUserContactReadModel, currentUserPrivacyReadModels, layer);

            if (user is TUser tUser)
            {
                if (storyMaxIdsWithLive.TryGetValue(userReadModel.UserId, out var storyInfo))
                {
                    tUser.StoriesMaxId = new TRecentStory
                    {
                        MaxId = storyInfo.maxId,
                        Live = storyInfo.hasLive
                    };
                }
                else
                {
                    tUser.StoriesUnavailable = true;
                }
            }

            users.Add(user);
        }

        return users;
    }

    public List<ILayeredUser> ToUserList(IRequestWithAccessHashKeyId request, IReadOnlyCollection<IUserReadModel> userReadModels, IReadOnlyCollection<IPhotoReadModel>? photoReadModels = null,
        IReadOnlyCollection<IContactReadModel>? contactReadModels = null, IReadOnlyCollection<IPrivacyReadModel>? privacyReadModels = null, int layer = 0)
    {
        var users = new List<ILayeredUser>();

        var photos = photoReadModels?
            .DistinctBy(p => p.PhotoId)
            .ToDictionary(k => k.PhotoId) ?? [];

        var targetUserContacts = contactReadModels?
             .Where(p => p.TargetUserId == request.UserId)
             .DistinctBy(p => p.SelfUserId)
             .ToDictionary(k => k.SelfUserId) ?? [];

        var myContacts = contactReadModels?
             .Where(p => p.SelfUserId == request.UserId)
             .DistinctBy(p => p.TargetUserId)
             .ToDictionary(k => k.TargetUserId) ?? [];

        var groupedPrivacyReadModels = privacyReadModels?.GroupBy(p => p.UserId).ToDictionary(k => k.Key, v => v.ToList()) ?? [];

        foreach (var userReadModel in userReadModels)
        {
            myContacts.TryGetValue(userReadModel.UserId, out var myContactReadModel);
            targetUserContacts.TryGetValue(userReadModel.UserId, out var targetUserContactReadModel);
            groupedPrivacyReadModels.TryGetValue(userReadModel.UserId, out var currentUserPrivacyReadModels);
            var user = ToUserCore(request, userReadModel, photos, myContactReadModel,
                targetUserContactReadModel, currentUserPrivacyReadModels, layer);
            users.Add(user);
        }

        return users;
    }

    private async Task<Dictionary<long, (int maxId, bool hasLive)>> GetStoriesMaxIdsAsync(List<long> userIds)
    {
        if (userIds.Count == 0)
            return new Dictionary<long, (int, bool)>();

        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var filter = Builders<StoryDocument>.Filter.And(
            Builders<StoryDocument>.Filter.In(s => s.OwnerPeerId, userIds),
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, 0),
            Builders<StoryDocument>.Filter.Eq(s => s.Deleted, false),
            Builders<StoryDocument>.Filter.Gte(s => s.ExpireDate, currentTime)
        );

        var stories = await _storyCollection.Find(filter).ToListAsync();

        var result = stories
            .GroupBy(s => s.OwnerPeerId)
            .ToDictionary(
                g => g.Key,
                g => {
                    var maxStory = g.OrderByDescending(s => s.StoryId).First();
                    return (maxStory.StoryId, maxStory.IsLive);
                });

        return result;
    }

    private ILayeredUser ToUserCore(IRequestWithAccessHashKeyId request, IUserReadModel userReadModel,
        Dictionary<long, IPhotoReadModel>? photoReadModels = null,
        IContactReadModel? myContactReadModel = null,
        IContactReadModel? targetUserContactReadModel = null,
        IReadOnlyCollection<IPrivacyReadModel>? privacyReadModels = null, int layer = 0)
    {
        var user = userLayeredService.GetConverter(layer).ToUser(userReadModel);
        user.AccessHash = 0;
        if (request.AccessHashKeyId != 0)
        {
            // For bots, use permanent AccessHash from database instead of session-specific
            if (userReadModel.Bot)
            {
                user.AccessHash = userReadModel.AccessHash;
            }
            else
            {
                user.AccessHash = accessHashHelper2.GenerateAccessHash(request.UserId, request.AccessHashKeyId,
                    userReadModel.UserId, AccessHashType.User);
            }
        }

        if (user is TUser tUserWithVerification)
        {
            ApplyBotVerificationIcon(tUserWithVerification);
        }

        // Set BotBusiness flag from MongoDB if user is a bot
        if (userReadModel.Bot && user is TUser tUser)
        {
            var userCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-userreadmodel");
            var botUserDoc = userCollection.Find(Builders<BsonDocument>.Filter.Eq("UserId", userReadModel.UserId)).FirstOrDefault();
            tUser.BotBusiness = userReadModel.BotBusiness;

            // Set BotCanEdit flag if current user owns this bot
            if (botUserDoc != null && botUserDoc.Contains("CreatorUserId"))
            {
                var creatorUserId = botUserDoc["CreatorUserId"].AsInt64;
                if (creatorUserId == request.UserId)
                {
                    tUser.BotCanEdit = true;
                }
            }
        }

        if (userReadModel.IsDeleted == true)
        {
            user.Deleted = true;
            user.Photo = null;

            return user;
        }

        if (request.UserId == userReadModel.UserId)
        {
            user.Self = true;
        }

        // Disabled Fragment collectibles are visible to their owner only (the bot's owner, for a bot).
        // See https://corefork.telegram.org/api/fragment
        if (!user.Self && user is TUser { BotCanEdit: not true } visibleUser)
        {
            (visibleUser.Username, visibleUser.Usernames) = UsernameVectorBuilder.Build(userReadModel.Usernames,
                userReadModel.UserName, includeDisabled: false);
        }

        user.Status = userStatusCacheAppService.GetUserStatus(user.Id);
        // The read model is kept up to date by UserEmojiStatusUpdatedEvent, so it is the single
        // source of truth here; expired statuses and collectible decoration are handled by the resolver.
        if (userReadModel.EmojiStatusDocumentId is { } emojiStatusDocumentId)
        {
            user.EmojiStatus = emojiStatusResolver.Resolve(
                new EmojiStatus(
                    emojiStatusDocumentId,
                    userReadModel.EmojiStatusValidUntil,
                    userReadModel.EmojiStatusCollectibleId),
                layer);
        }
        var contactType = contactHelper.GetContactType(myContactReadModel, targetUserContactReadModel);
        var photos = photoReadModels ?? [];
        SetUserProfilePhoto(userReadModel, user, photos, layer);
        SetContactPersonalProfilePhoto(user, photos, myContactReadModel, layer);
        SetMutualContact(user, contactType);
        ApplyPrivacyToUser(request.UserId, userReadModel, user, photos, contactType, privacyReadModels, layer);

        return user;
    }

    /// <summary>
    /// The <a href="https://corefork.telegram.org/api/bots/verification">third-party verification</a>
    /// icon, read from the in-process snapshot: this runs for every user in every dialog list, search
    /// result and message batch, and several of those paths are synchronous.
    /// <para>
    /// A zero icon is left unset on purpose - <c>TUser.ComputeFlag</c> raises the flag on
    /// <c>HasValue</c> alone, so writing 0 would serialize a badge that does not exist.
    /// </para>
    /// </summary>
    private void ApplyBotVerificationIcon(TUser user)
    {
        var icon = botVerificationCache.GetUserIcon(user.Id);
        if (icon == 0)
        {
            return;
        }

        user.BotVerificationIcon = icon;
    }

    private void ApplyPrivacyToUserFull(long selfUserId,
        IUserFull userFull,
        IReadOnlyCollection<IPrivacyReadModel>? privacyReadModels,
        ContactType contactType)
    {
        if (selfUserId == userFull.Id)
        {
            return;
        }

        foreach (var privacy in privacyReadModels ?? [])
        {
            switch (privacy.PrivacyType)
            {
                case PrivacyType.PhoneCall:
                    privacyHelper.ApplyPrivacy(privacy, _ =>
                    {
                        userFull.PhoneCallsAvailable = false;
                        userFull.PhoneCallsPrivate = false;
                    }, selfUserId, contactType);
                    break;

                case PrivacyType.ProfilePhoto:
                    privacyHelper.ApplyPrivacy(privacy,
                        _ =>
                        {
                            userFull.ProfilePhoto = null;
                        },
                        selfUserId, contactType);
                    break;

                case PrivacyType.VoiceMessages:
                    privacyHelper.ApplyPrivacy(privacy, _ => { userFull.VoiceMessagesForbidden = true; },
                        selfUserId, contactType);
                    break;
                case PrivacyType.About:
                    privacyHelper.ApplyPrivacy(privacy, _ => { userFull.About = null; }, selfUserId, contactType);
                    break;

                case PrivacyType.Birthday:
                    privacyHelper.ApplyPrivacy(privacy, _ =>
                    {
                        userFull.Birthday = null;
                    }, selfUserId, contactType);
                    break;
            }
        }
    }


    private void ApplyPrivacyToUser(long selfUserId, IUserReadModel userReadModel, ILayeredUser user,
        Dictionary<long, IPhotoReadModel> photos, ContactType contactType,
        IReadOnlyCollection<IPrivacyReadModel>? privacyReadModels, int layer)
    {
        if (selfUserId == userReadModel.UserId)
        {
            return;
        }

        photos.TryGetValue(userReadModel.FallbackPhotoId ?? 0, out var fallbackPhotoReadModel);
        var phoneNumberRuleEvaluated = false;

        foreach (var privacy in privacyReadModels ?? [])
        {
            switch (privacy.PrivacyType)
            {
                case PrivacyType.StatusTimestamp:
                    privacyHelper.ApplyPrivacy(privacy,
                        _ => PrivacyMaskingHelper.HideStatusTimestamp(user),
                        selfUserId,
                        contactType);
                    break;
                case PrivacyType.ProfilePhoto:
                    privacyHelper.ApplyPrivacy(privacy,
                        _ => user.Photo = photoLayeredService.GetConverter(layer)
                            .ToProfilePhoto(fallbackPhotoReadModel), selfUserId,
                        contactType);
                    break;
                case PrivacyType.PhoneNumber:
                    phoneNumberRuleEvaluated = true;
                    privacyHelper.ApplyPrivacy(privacy, _ => user.Phone = null, selfUserId, contactType);
                    break;
            }
        }

        // The phone number used to be cleared unconditionally for anyone who was not a mutual
        // contact, which made an explicit "phone number: everybody" rule have no effect. The
        // rule above now decides; when the user never set one we fall back to Telegram's
        // documented default for this key (allowContacts) instead of the old mutual-only rule.
        if (!phoneNumberRuleEvaluated && contactType is not (ContactType.Mutual or ContactType.ContactOfTargetUser))
        {
            user.Phone = null;
        }
    }


    private static void SetMutualContact(ILayeredUser user, ContactType contactType)
    {
        if (user is TUser tUser)
        {
            tUser.MutualContact = contactType == ContactType.Mutual;
        }
    }

    private void SetContactPersonalProfilePhoto(ILayeredUser user, Dictionary<long, IPhotoReadModel> photos,
        IContactReadModel? contactReadModel,
        int layer)
    {
        if (contactReadModel != null)
        {
            user.Contact = true;
            user.FirstName = contactReadModel.FirstName;
            user.LastName = contactReadModel.LastName;

            if (contactReadModel.PhotoId != null)
            {
                if (photos.TryGetValue(contactReadModel.PhotoId.Value, out var photoReadModel))
                {
                    user.Photo = photoLayeredService.GetConverter(layer).ToProfilePhoto(photoReadModel);
                    if (user.Photo is TUserProfilePhoto profilePhoto)
                    {
                        profilePhoto.Personal = true;
                    }
                }
            }
        }
    }

    private void SetUserProfilePhoto(IUserReadModel userReadModel,
        ILayeredUser user, Dictionary<long, IPhotoReadModel> photoReadModels, int layer)
    {
        if (userReadModel.ProfilePhotoId != null)
        {
            if (photoReadModels.TryGetValue(userReadModel.ProfilePhotoId.Value, out var photoReadModel))
            {
                user.Photo = photoLayeredService.GetConverter(layer).ToProfilePhoto(photoReadModel);
            }
        }
    }
}
