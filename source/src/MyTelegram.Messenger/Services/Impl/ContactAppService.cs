namespace MyTelegram.Messenger.Services.Impl;

public interface IContactHelper
{
    ContactType GetContactType(IContactReadModel? myContactReadModel,
        IContactReadModel? targetUserContactReadModel);

    ContactType GetContactType(long selfUserId, long targetUserId,
        IReadOnlyCollection<IContactReadModel> contactReadModels);
}

public class ContactHelper : IContactHelper, ITransientDependency
{
    public ContactType GetContactType(IContactReadModel? myContactReadModel,
        IContactReadModel? targetUserContactReadModel)
    {
        var contactType = (myContactReadModel, targetUserContactReadModel)
            switch
        {
            { myContactReadModel: not null, targetUserContactReadModel: not null } => ContactType.Mutual,
            { myContactReadModel: null, targetUserContactReadModel: not null } => ContactType
                .ContactOfTargetUser,
            { myContactReadModel: not null, targetUserContactReadModel: null } => ContactType
                .TargetUserIsMyContact,
            _ => ContactType.None
        };

        return contactType;
    }

    public ContactType GetContactType(long selfUserId, long targetUserId,
        IReadOnlyCollection<IContactReadModel> contactReadModels)
    {
        var myContactReadModel =
            contactReadModels.FirstOrDefault(p => p.SelfUserId == selfUserId && p.TargetUserId == targetUserId);
        var targetUserContactReadModel =
            contactReadModels.FirstOrDefault(p => p.SelfUserId == targetUserId && p.TargetUserId == selfUserId);

        var contactType = (myContactReadModel, targetUserContactReadModel)
            switch
        {
            { myContactReadModel: not null, targetUserContactReadModel: not null } => ContactType.Mutual,
            { myContactReadModel: null, targetUserContactReadModel: not null } => ContactType
                .ContactOfTargetUser,
            { myContactReadModel: not null, targetUserContactReadModel: null } => ContactType
                .TargetUserIsMyContact,
            _ => ContactType.None
        };

        return contactType;
    }
}

public class ContactAppService(
    IQueryProcessor queryProcessor,
    IPhotoAppService photoAppService,
    IChannelAppService channelAppService,
    IUserAppService userAppService,
    IPeerHelper peerHelper,
    ILogger<ContactAppService> logger,
    IOptionsMonitor<MyTelegramMessengerServerOptions> options)
    : BaseAppService, IContactAppService, ITransientDependency
{
    private const int MinSearchKeywordLength = 2;
    private const int MinNonContactSearchKeywordLength = 3;
    private const int MaxSearchResultLimit = 50;

    public ContactType GetContactType(long selfUserId, long targetUserId,
        IReadOnlyCollection<IContactReadModel> contactReadModels)
    {
        var myContactReadModel =
            contactReadModels.FirstOrDefault(p => p.SelfUserId == selfUserId && p.TargetUserId == targetUserId);
        var targetUserContactReadModel =
            contactReadModels.FirstOrDefault(p => p.SelfUserId == targetUserId && p.TargetUserId == selfUserId);

        var contactType = (myContactReadModel, targetUserContactReadModel)
            switch
        {
            { myContactReadModel: not null, targetUserContactReadModel: not null } => ContactType.Mutual,
            { myContactReadModel: null, targetUserContactReadModel: not null } => ContactType
                .ContactOfTargetUser,
            { myContactReadModel: not null, targetUserContactReadModel: null } => ContactType
                .TargetUserIsMyContact,
            _ => ContactType.None
        };

        return contactType;
    }

    public async Task<ContactType> GetContactTypeAsync(long selfUserId, long targetUserId)
    {
        var contactReadModels =
            await queryProcessor.ProcessAsync(new GetContactListBySelfIdAndTargetUserIdQuery(selfUserId, targetUserId));

        return GetContactType(selfUserId, targetUserId, contactReadModels);
    }

    /// <summary>
    /// Drops matches on a Fragment collectible its holder has not activated: it is visible to the owner
    /// only. See https://corefork.telegram.org/api/fragment
    /// </summary>
    private async Task<List<IUserNameReadModel>> ExcludeDisabledCollectiblesAsync(long selfUserId,
        IReadOnlyCollection<IUserNameReadModel> matches)
    {
        var users = (await userAppService.GetListAsync(matches
                .Where(p => p.PeerType == PeerType.User && p.PeerId != selfUserId)
                .Select(p => p.PeerId).Distinct()))
            .ToDictionary(p => p.UserId);
        var channels = (await channelAppService.GetListAsync(matches
                .Where(p => p.PeerType == PeerType.Channel)
                .Select(p => p.PeerId).Distinct()))
            .ToDictionary(p => p.ChannelId);

        return matches.Where(p => p.PeerType switch
        {
            PeerType.User => !users.TryGetValue(p.PeerId, out var user) ||
                             !user.Usernames.IsDisabledCollectible(p.UserName),
            PeerType.Channel => !channels.TryGetValue(p.PeerId, out var channel) ||
                                channel.CreatorId == selfUserId ||
                                !channel.Usernames.IsDisabledCollectible(p.UserName),
            _ => true
        }).ToList();
    }

    public async Task<SearchContactOutput> SearchAsync(long selfUserId,
        string keyword, int limit)
    {
        var searchKeyword = NormalizeSearchKeyword(keyword);
        if (searchKeyword.Length >= MinSearchKeywordLength)
        {
            var defaultLimit = NormalizeLimit(limit);

            var contactReadModels = await queryProcessor
                .ProcessAsync(new SearchContactQuery(selfUserId, searchKeyword, defaultLimit));
            var userNameReadModels = await ExcludeDisabledCollectiblesAsync(selfUserId,
                await queryProcessor.ProcessAsync(new SearchUserNameQuery(searchKeyword)));

            // Collect channel IDs from username search and keyword search
            var channelIdList = userNameReadModels.Where(p => p.PeerType == PeerType.Channel).Select(p => p.PeerId)
                .ToList();
            var channelIds2 =
                await queryProcessor.ProcessAsync(new GetChannelIdsByKeywordQuery(selfUserId, searchKeyword, defaultLimit));
            channelIdList.AddRange(channelIds2);
            channelIdList = channelIdList.Distinct().ToList();

            // Collect user IDs from contacts and username search (includes both users and bots)
            var userIdList = contactReadModels.Select(p => p.TargetUserId).ToList();
            userIdList.AddRange(userNameReadModels.Where(p => p.PeerType == PeerType.User).Select(p => p.PeerId));

            var userReadModels = await userAppService.GetListAsync(userIdList);
            var allUserReadModels = userReadModels.ToList();

            if (options.CurrentValue.EnableSearchNonContacts &&
                searchKeyword.Length >= MinNonContactSearchKeywordLength)
            {
                var userReadModels2 =
                    await queryProcessor.ProcessAsync(new SearchUserByKeywordQuery(searchKeyword, defaultLimit));
                allUserReadModels.AddRange(userReadModels2);
                allUserReadModels = allUserReadModels.DistinctBy(p => p.UserId).ToList();
            }

            var channelReadModels = await channelAppService.GetListAsync(channelIdList);

            // Also search for chats (groups) by keyword
            var chatReadModels = new List<IChannelReadModel>();
            try
            {
                var chatIds = await queryProcessor.ProcessAsync(new GetChannelIdsByKeywordQuery(selfUserId, searchKeyword, defaultLimit));
                if (chatIds.Any())
                {
                    var chats = await channelAppService.GetListAsync(chatIds);
                    chatReadModels = chats.Where(c => !c.Broadcast).ToList(); // Groups are non-broadcast channels
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Chat search failed while resolving contacts search results");
            }

            var photoReadModels =
                await photoAppService.GetPhotosAsync(allUserReadModels, contactReadModels, channelReadModels);

            return new SearchContactOutput(selfUserId,
                allUserReadModels,
                photoReadModels,
                contactReadModels,
                chatReadModels,
                channelReadModels,
                [],
                []
            );
        }

        return new SearchContactOutput(selfUserId,
            new List<IUserReadModel>(),
            new List<IPhotoReadModel>(),
            new List<IContactReadModel>(),
            new List<IChannelReadModel>(),
            new List<IChannelReadModel>(),
            new List<IPrivacyReadModel>(),
            new List<IChannelMemberReadModel>());
    }

    private static int NormalizeLimit(int limit)
    {
        return limit <= 0 ? 20 : Math.Min(limit, MaxSearchResultLimit);
    }

    private static string NormalizeSearchKeyword(string? keyword)
    {
        var q = keyword?.Trim() ?? string.Empty;
        return q.StartsWith("@") ? q[1..].Trim() : q;
    }
}
