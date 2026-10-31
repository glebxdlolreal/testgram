// ReSharper disable once CheckNamespace

namespace MyTelegram;

public class MyTelegramConsts
{
    public const int Layer = Layers.LayerLatest;

    public const string RepositoryUrl = "https://github.com/glebxdlolreal/testgram";
    public const long UserIdInitId = 2000000;
    public const long BotUserInitId = 600000000000;
    public const long ChatIdInitId = 700000000000;
    public const long ChannelInitId = 800000000000;
    public const long BotFatherUserId = BotUserInitId;

    /// <summary>
    /// Start of the block reserved for bots the server itself owns. Bots created through BotFather are
    /// numbered upwards from <see cref="BotUserInitId"/> + 1, so a system bot placed there collides with
    /// the first bot a user creates — this block sits far above that and is skipped by the allocator.
    /// </summary>
    public const long SystemBotUserIdBase = BotUserInitId + 900_000_000;

    /// <summary>How many ids <see cref="SystemBotUserIdBase"/> reserves.</summary>
    public const long SystemBotUserIdReservedCount = 1000;

    /// <summary>
    /// The <c>@gif</c> peer. Clients reach GIF search through the inline bot named by
    /// <c>config.gif_search_username</c>, so that username has to resolve to a real bot account.
    /// This one is served in-process by <c>GifSearchBotService</c> rather than by an external
    /// webhook, the same way BotFather is.
    /// See https://corefork.telegram.org/api/gifs#searching-gifs
    /// </summary>
    public const long GifSearchBotUserId = SystemBotUserIdBase + 1;

    /// <summary>Locally owned moderation actor, never a channel participant or administrator.</summary>
    public const long AntiSpamBotUserId = SystemBotUserIdBase + 2;
    public const string AntiSpamBotUserName = "TestgramAntiSpamBot";
    public const string AntiSpamActorCollection = "system-antispam-actor";

    /// <summary>
    /// <c>@PremiumBot</c>, the bot named by the <c>premium_bot_username</c> app config key that sells
    /// Telegram Premium. Served in-process by <c>PremiumBotService</c>, like BotFather.
    /// See https://corefork.telegram.org/api/premium
    /// </summary>
    public const long PremiumBotUserId = SystemBotUserIdBase + 3;
    public const string PremiumBotUserName = "PremiumBot";

    /// <summary>
    /// True for ids in the system-bot block, which <c>BotFatherBotService</c> must never hand out to a
    /// user-created bot.
    /// </summary>
    public static bool IsReservedSystemBotUserId(long userId)
    {
        return userId >= SystemBotUserIdBase && userId < SystemBotUserIdBase + SystemBotUserIdReservedCount;
    }

    public const int FolderInitId = 1;

    /// <summary>
    /// The chat archive, the only <a href="https://corefork.telegram.org/api/folders#peer-folders">peer
    /// folder</a> that exists besides the main list (0): "no other folder_id is allowed at the moment".
    /// </summary>
    public const int ArchiveFolderId = 1;

    public const int PtsInitId = 1;

    public const long NotificationServiceUserId = 777000;
    public const long GroupAnonymousBotUserId = 568888;
    public const long AnonymousUserId = 2666000;
    public const long DefaultSupportUserId = 569999;

    /// <summary>
    /// The <c>@ChatsImportBot</c> peer ("Imported Message"). Messages imported from a foreign chat app
    /// into a group are sent by this account, not by the user who started the import: the original
    /// author only lives in <c>fwd_from.from_name</c>, so the messages must not look like the
    /// importer's own. The id matches the one the production servers use.
    /// See https://corefork.telegram.org/api/import
    /// </summary>
    public const long ChatImporterBotUserId = 1474613229;

    /// <summary>
    /// The <c>@replies</c> peer. A user who commented on a channel post without joining the discussion
    /// group receives replies to their comments as messages from this peer; official clients render it
    /// as a read-only supergroup. The id matches the one official clients special-case.
    /// See https://corefork.telegram.org/api/discussion#replies
    /// </summary>
    public const long RepliesServiceUserId = 1271266957;

    public const long DeletedChannelIdForChannelPost = 777;

    public const string VideoMimeType = "video";
    public const int MediaDcId = 2;

    public const int MainDcId = 1;
    public const int QrCodeExpireSeconds = 30;
    public const int ChatAboutMaxLength = 255;

    public const int ChannelAdminMaxCount = 20;
    public const int UsernameMinLength = 5;
    public const int UsernameMaxLength = 32;
    public const int MaxRecentRepliersCount = 3;
    public const int MaxVoteOptions = 10;

    /// <summary>Minimum <c>close_period</c> accepted for a poll, in seconds.</summary>
    public const int MinPollClosePeriod = 5;

    /// <summary>Maximum <c>close_period</c> accepted for a poll, in seconds.</summary>
    public const int MaxPollClosePeriod = 600;

    /// <summary>How many recent voters are exposed in <c>pollResults.recent_voters</c>.</summary>
    public const int MaxPollRecentVoters = 3;
    public const int ChatMemberMaxCount = 50;
    public const int ChannelBotMaxCount = 20;

    public const int AuthKeyExpireSeconds = 30;
    //public const int MaxAllowedJoinChannel

    public const int InternalErrorCode = 500;
    public const int BadRequestErrorCode = 400;
    public const string InternalErrorMessage = "Internal_Server_Error";

    public const int ClearHistoryDefaultPageSize = 500;
    public const int UnPinAllMessagesDefaultPageSize = 500;
    public const int DefaultPtsTotalLimit = 500;

    /// <summary>
    /// How far a client's pts may fall behind the box before <c>updates.getDifference</c> answers
    /// with <c>differenceTooLong</c> instead of paging.
    ///
    /// <para>A page is capped at <see cref="DefaultPtsTotalLimit"/> rows and the server-side cursor
    /// only advances once the client acks the page it was sent. A session that is further behind than
    /// this would otherwise need hundreds of slice round-trips to catch up, and any dropped ack
    /// restarts it — the client ends up polling getDifference forever. Telling it to resync from
    /// scratch is bounded work instead.</para>
    /// </summary>
    public const int DifferenceTooLongPtsGap = DefaultPtsTotalLimit * 20;

    public const int MaxTimeDiffSeconds = 300;
    public const int LeftChannelUid = -1;
    public const int MaxBotCount = 20;
    public const int GzipMinLength = 512;

    public const int ReactionUniqueMax = 11;
    public const int RecentReactionMaxCount = 10;
    public const int ChatInviteRecentRequesterMaxCount = 10;
    public const int ChatReadMarkExpirePeriod = 604800;

    public const string BlockedCuckooFilterKey = "MyTelegarm.CuckooFilter.Blocked";
    public const string UserNameCuckooFilterKey = "MyTelegarm.CuckooFilter.UserName";
    public const string ChannelMessageViewsBloomFilterKey = "MyTelegarm.BloomFilter.ChannelMessageViews";
    public static int EditTimeLimit { get; set; } = 172800;

    public const int QuickRepliesLimit = 100;
    public const int QuickReplyMessagesLimit = 20;
    public const int ScheduleMessagesLimit = 30;
    public const int ActiveStoriesLimit = 10;
    public const int StoriesWeeklyLimit = 10;
    public const int StoriesMonthlyLimit = 30;
    public const int StoryCaptionLengthLimit = 200;

    public const int ChatlistInvitesLimitDefault = 3;
    public const int ChatlistInvitesLimitPremium = 50;
    public const int ChatlistsJoinedLimitDefault = 2;
    public const int ChatlistsJoinedLimitPremium = 20;

    public class GeoKeys
    {
        public const string User = "User";
        public const string GeoGroup = "GeoGroup";
    }
}