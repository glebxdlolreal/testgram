using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;
using MyTelegram.Messenger.Services;
using MyTelegram.Messenger.Services.Bots;
using MyTelegram.Messenger.Services.Stories;
using IChannelParticipant = MyTelegram.Schema.IChannelParticipant;
using IChatFull = MyTelegram.Schema.IChatFull;
using TChannelParticipant = MyTelegram.Schema.Channels.TChannelParticipant;
using TChatFull = MyTelegram.Schema.Messages.TChatFull;

using MyTelegram.Converters.Mappers;

namespace MyTelegram.Messenger.Converters.ConverterServices;

public class ChatConverterService(
    IQueryProcessor queryProcessor,
    IPhotoAppService photoAppService,
    IChannelAppService channelAppService,
    IAccessHashHelper2 accessHashHelper2,
    ILayeredService<IChannelConverter> channelLayeredService,
    ILayeredService<IPhotoConverter> photoLayeredService,
    ILayeredService<IChannelFullConverter> channelFullLayeredService,
    ILayeredService<IChannelParticipantConverter> channelParticipantLayeredService,
    ILayeredService<IChannelParticipantSelfConverter> channelParticipantSelfLayeredService,
    ILayeredService<IPeerNotifySettingsConverter> peerNotifySettingsLayeredService,
    ILayeredService<IChatAdminRightsConverter> chatAdminRightsLayeredService,
    IChatInviteExportedConverterService chatInviteExportedConverterService,
    ILayeredService<IEmojiStatusConverter> emojiStatusLayeredService,
    IEmojiStatusResolver emojiStatusResolver,
    ILayeredService<IChatBannedRightsConverter> chatBannedRightsLayeredService,
    IBotVerificationCache botVerificationCache,
    IMongoDatabase mongoDatabase)
    : IChatConverterService, ITransientDependency
{
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");
    private readonly IMongoCollection<BsonDocument> _storyHiddenCollection =
        mongoDatabase.GetCollection<BsonDocument>("story_hidden_peers");

    public async Task<IChat> GetChannelAsync(IRequestWithAccessHashKeyId request, long channelId,
        bool checkChannelMember, bool? channelMemberIsLeft, int layer = 0, bool throwIfNotFound = true)
    {
        var channelReadModel = throwIfNotFound
            ? await channelAppService.GetAsync(channelId)
            : await channelAppService.GetAsync((long?)channelId);
        if (channelReadModel == null)
        {
            if (throwIfNotFound)
            {
                throw new RpcException(RpcErrors.RpcErrors400.ChannelInvalid);
            }

            return null!;
        }

        IChannelMemberReadModel? channelMemberReadModel = null;
        if (checkChannelMember && channelMemberIsLeft == null)
        {
            channelMemberReadModel =
                await queryProcessor.ProcessAsync(new GetChannelMemberByUserIdQuery(channelId, request.UserId));
        }

        var photoReadModel = await photoAppService.GetAsync(channelReadModel.PhotoId);
        await botVerificationCache.EnsureFreshAsync();

        return ToChannelCore(request, channelReadModel, photoReadModel, channelMemberReadModel, channelMemberIsLeft,
            layer);
    }

    public async Task<List<IChat>> GetChannelListAsync(IRequestWithAccessHashKeyId request,
        List<long> channelIds,
        IReadOnlyCollection<IChannelMemberReadModel>? channelMemberReadModels = null,
        int layer = 0)
    {
        var channels = new List<IChat>();
        var channelReadModels = await channelAppService.GetListAsync(channelIds);
        var photoReadModels = await photoAppService.GetPhotosAsync(channelReadModels);
        var channelMembers = channelMemberReadModels?.ToDictionary(k => k.ChannelId) ?? [];
        var photos = photoReadModels.ToDictionary(k => k.PhotoId);
        await botVerificationCache.EnsureFreshAsync();

        foreach (var channelReadModel in channelReadModels)
        {
            photos.TryGetValue(channelReadModel.PhotoId ?? 0, out var photoReadModel);
            channelMembers.TryGetValue(channelReadModel.ChannelId, out var channelMemberReadModel);

            var channel = ToChannelCore(request, channelReadModel, photoReadModel, channelMemberReadModel, null,
                layer);

            channels.Add(channel);
        }

        return channels;
    }

    public async Task<IChatFull> GetChannelFullAsync(IRequestWithAccessHashKeyId request, long channelId,
        IPeerNotifySettingsReadModel? peerNotifySettingsReadModel = null,
        IChatInviteReadModel? chatInviteReadModel = null,
        int layer = 0)
    {
        var channelReadModel = await channelAppService.GetAsync(channelId);
        var channelFullReadModel = await channelAppService.GetChannelFullAsync(channelId);
        if (channelReadModel == null || channelFullReadModel == null)
        {
            throw new RpcException(RpcErrors.RpcErrors400.ChannelInvalid);
        }

        var photoReadModel = await photoAppService.GetAsync(channelReadModel.PhotoId);
        return ToChannelFull(request, channelReadModel, photoReadModel, channelFullReadModel,
            peerNotifySettingsReadModel, chatInviteReadModel, layer);
    }

    public Schema.Channels.IChannelParticipant ToChannelParticipant(
        IRequestWithAccessHashKeyId request,
        IChannelReadModel channelReadModel,
        IPhotoReadModel? photoReadModel,
        IChannelMemberReadModel channelMemberReadModel,
        IUser user,
        int layer = 0
    )
    {
        var participant = ToChannelParticipantCore(request, channelReadModel, channelMemberReadModel, layer);
        var channel = ToChannel(request, channelReadModel, photoReadModel, channelMemberReadModel, null, layer);
        return new TChannelParticipant
        {
            Chats = new TVector<IChat>(channel),
            Participant = participant,
            Users = new TVector<IUser>(user)
        };
    }

    public IChat ToChannel(IRequestWithAccessHashKeyId request, IChannelReadModel channelReadModel, IPhotoReadModel? photoReadModel,
        IChannelMemberReadModel? channelMemberReadModel, bool? channelMemberIsLeft, int layer)
    {
        return ToChannelCore(request, channelReadModel, photoReadModel, channelMemberReadModel, channelMemberIsLeft,
            layer);
    }

    public List<IChat> ToChannelList(IRequestWithAccessHashKeyId request, IReadOnlyCollection<IChannelReadModel> channelReadModels,
        IReadOnlyCollection<IPhotoReadModel> photoReadModels,
        IReadOnlyCollection<IChannelMemberReadModel>? channelMemberReadModels,
        IReadOnlyCollection<long>? joinedChannelIds = null, int layer = 0)
    {
        var channels = new List<IChat>();
        var channelMembers = channelMemberReadModels?.ToDictionary(k => k.ChannelId) ?? [];
        var photos = photoReadModels.ToDictionary(k => k.PhotoId);
        var shouldCheckJoinedChannelList = joinedChannelIds != null;
        foreach (var channelReadModel in channelReadModels)
        {
            photos.TryGetValue(channelReadModel.PhotoId ?? 0, out var photoReadModel);
            channelMembers.TryGetValue(channelReadModel.ChannelId, out var channelMemberReadModel);

            var channel = ToChannelCore(request, channelReadModel, photoReadModel, channelMemberReadModel, null,
                layer);
            if (channel is ILayeredChannel chat)
            {
                if (shouldCheckJoinedChannelList)
                {
                    chat.Left = !joinedChannelIds!.Contains(channelReadModel.ChannelId);
                }
            }

            channels.Add(channel);
        }

        return channels;
    }

    public IChannelParticipants ToChannelParticipants(IRequestWithAccessHashKeyId request, IChannelReadModel channelReadModel,
        IPhotoReadModel? photoReadModel,
        //IReadOnlyCollection<IChatAdminReadModel>? chatAdminReadModels,
        int participantCount,
        IReadOnlyCollection<IChannelMemberReadModel> channelMemberReadModels, IEnumerable<IUser> users,
        DeviceType deviceType, bool forceNotLeft, int layer)
    {
        var channelMemberReadModel = channelMemberReadModels.FirstOrDefault(p => p.UserId == request.UserId);
        var channelMemberIsLeft = true;
        if (channelMemberReadModel == null)
        {
            if (forceNotLeft)
            {
                channelMemberIsLeft = false;
            }
        }
        else
        {
            channelMemberIsLeft = channelMemberReadModel.Left;
        }

        var channel = ToChannel(
            request,
            channelReadModel,
            photoReadModel,
            channelMemberReadModel,
            channelMemberIsLeft, layer);

        //if (channelReadModel.Broadcast)
        //{
        //    if (request.UserId != channelReadModel.CreatorId)
        //    {
        //        chatAdminReadModels = [];
        //    }
        //}

        var participants =
            ToChannelParticipantsCore(request, channelReadModel, [], channelMemberReadModels,
                layer);

        return new TChannelParticipants
        {
            Chats = new TVector<IChat>(channel),
            Count = participantCount,//channelReadModel.ParticipantsCount ?? participants.Count,
            Participants = [.. participants],
            Users = [.. users]
        };
    }

    public Schema.Messages.IChatFull ToChannelFull(
        IRequestWithAccessHashKeyId request,
        IChannelReadModel channelReadModel,
        IPhotoReadModel? photoReadModel,
        IChannelFullReadModel channelFullReadModel,
        IChannelMemberReadModel? channelMemberReadModel,
        IPeerNotifySettingsReadModel peerNotifySettingsReadModel,
        IChatInviteReadModel? chatInviteReadModel = null,
        int layer = 0
    )
    {
        var channel = ToChannel(request, channelReadModel, photoReadModel, channelMemberReadModel, null, layer);
        if (channel is ILayeredChannel layeredChannel)
        {
            layeredChannel.ParticipantsCount = null;
        }

        var fullChat = ToChannelFull(
            request,
            channelReadModel,
            photoReadModel,
            channelFullReadModel,
            peerNotifySettingsReadModel,
            chatInviteReadModel,
            layer
        );

        var chatFull = new TChatFull
        {
            Chats = new TVector<IChat>(channel),
            FullChat = fullChat,
            Users = []
        };

        return chatFull;
    }

    private IChat ToChannelCore(IRequestWithAccessHashKeyId request, IChannelReadModel channelReadModel,
        IPhotoReadModel? photoReadModel,
        IChannelMemberReadModel? channelMemberReadModel,
        bool? channelMemberIsLeft,
        int layer
    )
    {
        var accessHash = 0L;
        if (request.AccessHashKeyId != 0)
        {
            accessHash = accessHashHelper2.GenerateAccessHash(request.UserId, request.AccessHashKeyId,
                 channelReadModel.ChannelId, AccessHashType.Channel);
        }
        // A ban with an until_date that has expired no longer hides the channel from the user.
        if (channelMemberReadModel is { Kicked: true } &&
            BannedRightsHelper.IsCurrentlyKicked(channelMemberReadModel, DateTime.UtcNow.ToTimestamp()))
        {
            return new TChannelForbidden
            {
                Broadcast = channelReadModel.Broadcast,
                AccessHash = accessHash,
                Id = channelReadModel.ChannelId,
                Title = channelReadModel.Title,
                Megagroup = channelReadModel.MegaGroup,
                UntilDate = channelMemberReadModel.UntilDate
            };
        }

        var channel = channelLayeredService.GetConverter(layer).ToChannel(channelReadModel);
        channel.Creator = channelReadModel.CreatorId == request.UserId;
        channel.Photo = photoLayeredService.GetConverter(layer).ToChatPhoto(photoReadModel);
        channel.EmojiStatus = emojiStatusResolver.Resolve(channelReadModel.EmojiStatus, layer);
        channel.Left = false;
        channel.AccessHash = accessHash;

        if (channel is TChannel tChannel)
        {
            if (!channel.Creator)
            {
                // Disabled Fragment collectibles are visible to the owner only.
                // See https://corefork.telegram.org/api/fragment
                (tChannel.Username, tChannel.Usernames) = UsernameVectorBuilder.Build(channelReadModel.Usernames,
                    channelReadModel.UserName, includeDisabled: false);
            }

            tChannel.Forum = channelReadModel.Forum;
            tChannel.ForumTabs = channelReadModel.ForumTabs;
            // subscription_until_date describes the *requesting user's* Star subscription, so it
            // comes from their membership rather than from the channel itself.
            tChannel.SubscriptionUntilDate = channelMemberReadModel?.SubscriptionUntilDate;
            ApplyBotVerificationIcon(tChannel);
            ApplyStoriesInfo(tChannel, request);
        }

        if (channelMemberIsLeft.HasValue)
        {
            channel.Left = channelMemberIsLeft.Value;
        }
        else
        {
            if (channelMemberReadModel == null || channelMemberReadModel.Left)
            {
                channel.Left = true;
            }
        }

        // A restriction with an until_date that has already passed no longer applies, so it must
        // not be reported to the client either.
        var effectiveBannedRights = BannedRightsHelper.GetEffectiveBannedRights(channelMemberReadModel,
            DateTime.UtcNow.ToTimestamp());
        if (effectiveBannedRights != null)
        {
            channel.BannedRights = chatBannedRightsLayeredService.GetConverter(layer)
                .ToChatBannedRights(effectiveBannedRights);
        }

        if (channel.Creator)
        {
            channel.AdminRights = chatAdminRightsLayeredService.GetConverter(layer)
                .ToChatAdminRights(ChatAdminRights.GetCreatorRights());
            channel.Left = false;
        }
        else
        {
            if (channel.BannedRights == null)
            {
                var admin = channelReadModel.AdminList.FirstOrDefault(p => p.UserId == request.UserId);
                channel.AdminRights = admin != null
                    ? chatAdminRightsLayeredService.GetConverter(layer)
                        .ToChatAdminRights(admin.AdminRights)
                    : null;
            }
        }

        return channel;
    }

    /// <summary>
    /// The <a href="https://corefork.telegram.org/api/bots/verification">third-party verification</a>
    /// icon, read from the in-process snapshot rather than from MongoDB: this runs for every channel
    /// in every dialog list.
    /// <para>
    /// A zero icon is left unset on purpose - <c>TChannel.ComputeFlag</c> raises the flag on
    /// <c>HasValue</c> alone, so writing 0 would serialize a badge that does not exist.
    /// </para>
    /// </summary>
    private void ApplyBotVerificationIcon(TChannel channel)
    {
        var icon = botVerificationCache.GetChannelIcon(channel.Id);
        if (icon == 0)
        {
            return;
        }

        channel.BotVerificationIcon = icon;
    }

    /// <summary>
    /// Sets the channel's <a href="https://corefork.telegram.org/api/stories">stories</a> summary, so
    /// clients know whether to draw a story ring on the channel and where the unread boundary is.
    /// Without this, channel stories never appear in the chat list.
    /// </summary>
    private void ApplyStoriesInfo(TChannel channel, IRequestWithAccessHashKeyId request)
    {
        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var latest = _storyCollection
            .Find(Builders<StoryDocument>.Filter.And(
                Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, channel.Id),
                Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, StoryHelper.PeerTypeChannel),
                Builders<StoryDocument>.Filter.Eq(s => s.Deleted, false),
                Builders<StoryDocument>.Filter.Lte(s => s.Date, currentTime),
                Builders<StoryDocument>.Filter.Gte(s => s.ExpireDate, currentTime)))
            .SortByDescending(s => s.StoryId)
            .Limit(1)
            .FirstOrDefault();

        if (latest == null)
        {
            channel.StoriesUnavailable = true;
            return;
        }

        channel.StoriesMaxId = new TRecentStory
        {
            MaxId = latest.StoryId,
            Live = latest.IsLive
        };

        // Match on the (peerId, peerType) pair: channel ids and user ids come from separate sequences
        // and do collide, so a peerId-only filter would report a channel as hidden because the caller
        // hid a user with the same numeric id. Rows written before peerType existed are user rows only,
        // so requiring the channel type here loses nothing.
        var hidden = _storyHiddenCollection
            .Find(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("userId", request.UserId),
                Builders<BsonDocument>.Filter.Eq("peerId", channel.Id),
                Builders<BsonDocument>.Filter.Eq("peerType", StoryHelper.PeerTypeChannel),
                Builders<BsonDocument>.Filter.Eq("hidden", true)))
            .Any();

        channel.StoriesHidden = hidden;
    }

    public IChatFull ToChannelFull(IRequestWithAccessHashKeyId request,
        IChannelReadModel channelReadModel,
        IPhotoReadModel? photoReadModel,
        IChannelFullReadModel channelFullReadModel,
        //IChannelMemberReadModel? channelMemberReadModel,
        IPeerNotifySettingsReadModel? peerNotifySettingsReadModel = null,
        IChatInviteReadModel? chatInviteReadModel = null,
        int layer = 0
    )
    {
        if (channelReadModel == null || channelFullReadModel == null)
        {
            throw new RpcException(RpcErrors.RpcErrors400.ChannelInvalid);
        }

        var channelFull = channelFullLayeredService.GetConverter(layer)
            .ToChannelFull(channelFullReadModel);
        channelFull.ChatPhoto = photoLayeredService.GetConverter(layer)
            .ToPhoto(photoReadModel);
        channelFull.NotifySettings = peerNotifySettingsLayeredService.GetConverter(layer)
            .ToPeerNotifySettings(peerNotifySettingsReadModel);
        channelFull.Pts = channelReadModel.Pts;
        channelFull.ParticipantsCount = channelReadModel.ParticipantsCount;
        channelFull.BotInfo = [];
        if (channelFull is TChannelFull tChannelFull)
        {
            tChannelFull.PaidMediaAllowed = channelReadModel.Broadcast;
            tChannelFull.PaidReactionsAvailable = channelReadModel.PaidReactionsEnabled;
            if (channelReadModel.CreatorId == request.UserId ||
                channelReadModel.AdminList.Any(p => p.UserId == request.UserId))
            {
                // Clients show the Monetization/Stars balance UI only when these
                // channelFull capability flags are present. The revenue handlers
                // already return the channel ledger; expose the capability to the
                // creator/admins that are allowed to open it.
                tChannelFull.CanViewStarsRevenue = true;
                tChannelFull.CanViewRevenue = true;

                // Same gate as StatsAccessController.ResolveChannelForStatsAsync: without this flag
                // official clients never surface the Statistics entry point at all.
                tChannelFull.CanViewStats = true;
            }
        }
        if (channelFullReadModel.RecentRequesters?.Count > 0 &&
            channelReadModel.AdminList.Any(p => p.UserId == request.UserId))
        {
            channelFull.RequestsPending = channelFullReadModel.RequestsPending;
            channelFull.RecentRequesters = [.. channelFullReadModel.RecentRequesters];
        }

        // Only creator and channel admin can view participants list for broadcast
        if (channelReadModel.Broadcast)
        {
            if (channelReadModel.CreatorId == request.UserId ||
                channelReadModel.AdminList.FirstOrDefault(p => p.UserId == request.UserId) != null)
            {
                channelFull.CanViewParticipants = true;
            }
            else
            {
                channelFull.CanViewParticipants = false;
            }
        }

        if (request.UserId == MyTelegramConsts.LeftChannelUid)
        {
            channelFull.CanViewParticipants = false;
            channelFull.CanSetUsername = false;
        }

        if (channelReadModel.CreatorId == request.UserId)
        {
            channelFull.CanSetUsername = true;
            channelFull.CanDeleteChannel = true;
        }

        if (channelFull.SlowmodeSeconds > 0)
        {
            if (request.UserId != channelReadModel.CreatorId && request.UserId == channelReadModel.LastSenderPeerId)
            {
                var nextSendDate = channelReadModel.LastSendDate + channelFull.SlowmodeSeconds;
                channelFull.SlowmodeNextSendDate = nextSendDate;
            }
        }

        if (chatInviteReadModel != null && channelReadModel.AdminList.Any(p => p.UserId == request.UserId))
        {
            channelFull.ExportedInvite = chatInviteExportedConverterService.ToExportedChatInvite(chatInviteReadModel, layer);
        }

        if (channelFull.Call != null)
        {
            channelFull.GroupcallDefaultJoinAs = new TPeerUser
            {
                UserId = request.UserId
            };
        }

        if (!channelReadModel.Broadcast && channelReadModel.CreatorId == request.UserId)
        {
            channelFull.CanSetStickers = true;
        }

        var anonymouseAdminCount = channelReadModel.AdminList.Count(p => p.AdminRights.Anonymous);
        if (channelFull.ParticipantsCount != null)
        {
            channelFull.ParticipantsCount -= anonymouseAdminCount;
            if (channelFull.ParticipantsCount < 0)
            {
                channelFull.ParticipantsCount = 0;
            }
        }

        return channelFull;
    }

    private bool IsAnonymousAdmin(int adminRights)
    {
        return (adminRights & (1 << 10)) != 0;
    }

    private IReadOnlyList<IChannelParticipant> ToChannelParticipantsCore(
        IRequestWithAccessHashKeyId request,
        IChannelReadModel channelReadModel,
        //IPhotoReadModel? photoReadModel,
        IReadOnlyCollection<IChatAdminReadModel>? chatAdminReadModels,
        IReadOnlyCollection<IChannelMemberReadModel> channelMemberReadModels,
        int layer
    )
    {
        var participants = new List<IChannelParticipant>();
        var selfUserId = request.UserId;
        //foreach (var chatAdminReadModel in chatAdminReadModels ?? [])
        //{
        //    participants.Add(ToChatParticipantAdmin(request, chatAdminReadModel));
        //}

        foreach (var channelMemberReadModel in channelMemberReadModels)
        {
            if (
                (channelReadModel.Broadcast || channelReadModel.HasLink) &&
                channelMemberReadModel.UserId == channelReadModel.CreatorId &&
                selfUserId != channelReadModel.CreatorId)
            {
                continue;
            }

            participants.Add(ToChannelParticipantCore(request, channelReadModel, channelMemberReadModel, layer));
        }

        return participants;
    }

    protected virtual IChannelParticipant ToChatParticipantAdmin(IRequestWithAccessHashKeyId request,
        IChatAdminReadModel chatAdminReadModel)
    {
        if (chatAdminReadModel.IsCreator)
        {
            return new TChannelParticipantCreator
            {
                AdminRights = ChatAdminRights.GetCreatorRights().ToChatAdminRights(),
                Rank = chatAdminReadModel.Rank,
                UserId = chatAdminReadModel.UserId
            };
        }

        return new TChannelParticipantAdmin
        {
            Date = chatAdminReadModel.Date,
            InviterId = chatAdminReadModel.PromotedBy,
            UserId = chatAdminReadModel.UserId,
            CanEdit = chatAdminReadModel.CanEdit,
            AdminRights = chatAdminReadModel.AdminRights.ToChatAdminRights(),
            PromotedBy = chatAdminReadModel.PromotedBy,
            Self = request.UserId == chatAdminReadModel.UserId,
            Rank = chatAdminReadModel.Rank
        };
    }

    private IChannelParticipant ToChannelParticipantCore(
        IRequestWithAccessHashKeyId request,
        IChannelReadModel channelReadModel,
        //IPhotoReadModel? photoReadModel,
        IChannelMemberReadModel channelMemberReadModel,
        int layer
    )
    {
        // Once the until_date of the restriction has passed the member is a normal participant again.
        var effectiveBannedRights = BannedRightsHelper.GetEffectiveBannedRights(channelMemberReadModel,
            DateTime.UtcNow.ToTimestamp());
        if (effectiveBannedRights != null &&
            (channelMemberReadModel.Kicked ||
             (channelMemberReadModel.BannedRights != ChatBannedRights.CreateDefaultBannedRights().ToIntValue() &&
              !channelMemberReadModel.Left)))
        {
            return new TChannelParticipantBanned
            {
                BannedRights = effectiveBannedRights.ToChatBannedRights(),
                Date = channelMemberReadModel.Date,
                Peer = new TPeerUser { UserId = channelMemberReadModel.UserId },
                KickedBy = channelMemberReadModel.KickedBy,
                Left = false,
                // A restriction does not wipe the tag. See https://corefork.telegram.org/api/rank
                Rank = string.IsNullOrEmpty(channelMemberReadModel.Rank) ? null : channelMemberReadModel.Rank
            };
        }

        if (channelMemberReadModel.Left)
        {
            return new TChannelParticipantLeft { Peer = new TPeerUser { UserId = channelMemberReadModel.UserId } };
        }

        if (channelMemberReadModel.UserId == channelReadModel.CreatorId)
        {
            var creatorRights = ChatAdminRights.GetCreatorRights();
            creatorRights.Anonymous = new ChatAdminRights(channelMemberReadModel.AdminRights).Anonymous;
            return new TChannelParticipantCreator
            {
                UserId = channelMemberReadModel.UserId,
                AdminRights = creatorRights.ToChatAdminRights(),
                // No tag means no flag at all: clients then fall back to showing "Owner".
                // See https://corefork.telegram.org/api/rank
                Rank = string.IsNullOrEmpty(channelMemberReadModel.Rank) ? null : channelMemberReadModel.Rank
            };
        }

        //var admin = channelReadModel.AdminList.FirstOrDefault(p => p.UserId == channelMemberReadModel.UserId);
        //if (admin != null)
        if (channelMemberReadModel.IsAdmin)
        {
            return new TChannelParticipantAdmin
            {
                //AdminRights = admin.AdminRights.ToChatAdminRights(),
                AdminRights = new ChatAdminRights(channelMemberReadModel.AdminRights).ToChatAdminRights(),
                Date = channelMemberReadModel.Date,
                InviterId = channelMemberReadModel.InviterId,
                // No tag means no flag at all: clients then show the default "Admin" label.
                // See https://corefork.telegram.org/api/rank
                Rank = string.IsNullOrEmpty(channelMemberReadModel.Rank) ? null : channelMemberReadModel.Rank,
                UserId = channelMemberReadModel.UserId,
                Self = channelMemberReadModel.UserId == request.UserId,
                CanEdit = channelMemberReadModel.CanEdit,
                PromotedBy = channelMemberReadModel.PromotedBy ?? 0
            };
        }

        return channelParticipantLayeredService.GetConverter(layer).ToChatParticipant(channelMemberReadModel);
    }
}
