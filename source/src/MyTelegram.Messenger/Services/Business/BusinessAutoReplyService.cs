using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Caching;

namespace MyTelegram.Messenger.Services.Business;

/// <summary>
/// Sends Telegram Business <a href="https://corefork.telegram.org/api/business#greeting-messages">greeting</a>
/// and <a href="https://corefork.telegram.org/api/business#away-messages">away</a> messages: the messages of
/// the chosen quick reply shortcut, sent as the business account into the private chat.
///
/// <list type="bullet">
/// <item>Greeting: the chat partner writes for the first time, or after <c>no_activity_days</c> without any
/// message in the chat.</item>
/// <item>Away: the account is away by its schedule (always, outside opening hours, or a date range); with
/// <c>offline_only</c> only while it has not been online for 10 minutes (as tdlib describes the flag). It is sent
/// once per chat per away period.</item>
/// </list>
/// A claim row per chat in <c>business_auto_replies</c> makes concurrent incoming messages send each reply once.
/// </summary>
public interface IBusinessAutoReplyService
{
    Task HandleIncomingAsync(BusinessIncomingMessageEvent incoming);
}

public sealed class BusinessAutoReplyService(
    IUserAppService userAppService,
    IQueryProcessor queryProcessor,
    IQuickReplyService quickReplyService,
    IBusinessProfileService businessProfileService,
    IMessageAppService messageAppService,
    IMongoDatabase mongoDatabase,
    ILogger<BusinessAutoReplyService> logger) : IBusinessAutoReplyService, ITransientDependency
{
    public const string CollectionName = "business_auto_replies";

    /// <summary>tdlib: "the away message is sent only if the account was offline for at least 10 minutes".</summary>
    private static readonly TimeSpan OfflineThreshold = TimeSpan.FromMinutes(10);

    /// <summary>Presence older than this is not trusted as "online" (the user status cache's window).</summary>
    private static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(90);

    public async Task HandleIncomingAsync(BusinessIncomingMessageEvent incoming)
    {
        var business = await userAppService.GetAsync(incoming.BusinessUserId);
        if (business == null || !IsPremiumActive(business) ||
            (business.BusinessGreetingSettings == null && business.BusinessAwaySettings == null))
        {
            return;
        }

        var peerUserId = incoming.PeerUserId;
        var contacts = await queryProcessor.ProcessAsync(
            new GetContactListBySelfIdAndTargetUserIdQuery(incoming.BusinessUserId, peerUserId));
        var isContact = contacts?.Any(p => p.SelfUserId == incoming.BusinessUserId && p.TargetUserId == peerUserId) ??
                        false;
        var previousMessageDate = await GetPreviousMessageDateAsync(incoming);
        var chatStartDate = previousMessageDate.HasValue
            ? await GetChatStartDateAsync(incoming.BusinessUserId, peerUserId) ?? incoming.Date
            : incoming.Date;
        var now = DateTime.UtcNow;
        var nowSeconds = (int)new DateTimeOffset(now).ToUnixTimeSeconds();

        if (business.BusinessGreetingSettings is { } greeting &&
            BusinessRecipientsMatcher.Matches(greeting.Recipients, peerUserId, isContact,
                chatStartDate >= greeting.UpdatedAt) &&
            ShouldGreet(previousMessageDate, nowSeconds, greeting.NoActivityDays) &&
            await TryClaimAsync(incoming, "greeting", greeting.NoActivityDays * 86400, nowSeconds, null))
        {
            await SendShortcutAsync(incoming, greeting.ShortcutId, "greeting");
        }

        if (business.BusinessAwaySettings is { } away &&
            BusinessRecipientsMatcher.Matches(away.Recipients, peerUserId, isContact,
                chatStartDate >= away.UpdatedAt))
        {
            var periodKey = await GetAwayPeriodKeyAsync(business, away, now);
            if (periodKey != null && await TryClaimAsync(incoming, "away", 0, nowSeconds, periodKey))
            {
                await SendShortcutAsync(incoming, away.ShortcutId, "away");
            }
        }
    }

    /// <summary>First contact, or the chat was silent for <paramref name="noActivityDays"/> days.</summary>
    public static bool ShouldGreet(int? previousMessageDate, int now, int noActivityDays)
    {
        return previousMessageDate == null || now - previousMessageDate.Value >= noActivityDays * 86400;
    }

    private static bool IsPremiumActive(IUserReadModel user) => user.Premium;

    /// <summary>
    /// A key naming the current away period, or null when the account is not away. One away message is
    /// sent per chat per key.
    /// </summary>
    private async Task<string?> GetAwayPeriodKeyAsync(IUserReadModel business, BusinessAwayItem away, DateTime now)
    {
        string? key;
        switch (away.Schedule)
        {
            case BusinessAwayScheduleKind.Custom:
                var nowSeconds = new DateTimeOffset(now).ToUnixTimeSeconds();
                key = away.StartDate <= nowSeconds && nowSeconds < away.EndDate ? $"custom:{away.StartDate}" : null;
                break;
            case BusinessAwayScheduleKind.OutsideWorkHours:
                // Without opening hours the account is never "outside" them.
                if (business.BusinessWorkHoursSettings == null ||
                    businessProfileService.IsOpen(business.BusinessWorkHoursSettings, now))
                {
                    return null;
                }

                key = $"closed:{now:yyyyMMdd}";
                break;
            default:
                key = $"always:{now:yyyyMMdd}";
                break;
        }

        if (key == null || !away.OfflineOnly)
        {
            return key;
        }

        var lastOnline = await GetLastOnlineAsync(business.UserId);
        if (lastOnline.Online && now - lastOnline.LastOnline < OnlineWindow)
        {
            return null;
        }

        if (lastOnline.LastOnline != default && now - lastOnline.LastOnline < OfflineThreshold)
        {
            return null;
        }

        // A new away message per offline stretch.
        return $"{key}:{lastOnline.LastOnline.Ticks / TimeSpan.TicksPerMinute}";
    }

    private async Task<(bool Online, DateTime LastOnline)> GetLastOnlineAsync(long userId)
    {
        var status = await mongoDatabase.GetCollection<UserStatusMongoModel>("user_status")
            .Find(Builders<UserStatusMongoModel>.Filter.Eq(p => p.UserId, userId))
            .FirstOrDefaultAsync();

        return status == null ? (false, default) : (status.Online, status.LastOnline);
    }

    /// <summary>The date of the newest message in the chat before the one that just arrived, either direction.</summary>
    private async Task<int?> GetPreviousMessageDateAsync(BusinessIncomingMessageEvent incoming)
    {
        var messages = mongoDatabase.GetCollection<BsonDocument>("eventflow-messagereadmodel");
        var builder = Builders<BsonDocument>.Filter;
        var previous = await messages
            .Find(builder.And(
                builder.Eq("OwnerPeerId", incoming.BusinessUserId),
                builder.Eq("ToPeerId", incoming.PeerUserId),
                builder.Lt("MessageId", incoming.MessageId),
                builder.Ne("IsQuickReplyMessage", true)))
            .Sort(Builders<BsonDocument>.Sort.Descending("MessageId"))
            .Project(Builders<BsonDocument>.Projection.Include("Date"))
            .Limit(1)
            .FirstOrDefaultAsync();

        return previous != null && previous.TryGetValue("Date", out var date) && date.IsNumeric
            ? date.ToInt32()
            : null;
    }

    private async Task<int?> GetChatStartDateAsync(long businessUserId, long peerUserId)
    {
        var dialog = await queryProcessor.ProcessAsync(new GetDialogByIdQuery(
            DialogId.Create(businessUserId, PeerType.User, peerUserId).Value));

        return dialog == null ? null : (int)new DateTimeOffset(dialog.CreationTime).ToUnixTimeSeconds();
    }

    /// <summary>
    /// Records that a reply of <paramref name="kind"/> goes to this chat now. Fails when one was already
    /// sent within <paramref name="minIntervalSeconds"/> or, for away messages, in the same period.
    /// </summary>
    private async Task<bool> TryClaimAsync(BusinessIncomingMessageEvent incoming, string kind,
        int minIntervalSeconds, int now, string? periodKey)
    {
        var collection = mongoDatabase.GetCollection<BsonDocument>(CollectionName);
        var id = $"{incoming.BusinessUserId}:{incoming.PeerUserId}:{kind}";
        var builder = Builders<BsonDocument>.Filter;
        var notYet = periodKey == null
            ? builder.Or(builder.Exists("LastSentDate", false), builder.Lte("LastSentDate", now - minIntervalSeconds))
            : builder.Or(builder.Exists("PeriodKey", false), builder.Ne("PeriodKey", periodKey));
        try
        {
            var result = await collection.FindOneAndUpdateAsync(
                builder.And(builder.Eq("_id", id), notYet),
                Builders<BsonDocument>.Update
                    .Set("BusinessUserId", incoming.BusinessUserId)
                    .Set("PeerUserId", incoming.PeerUserId)
                    .Set("Kind", kind)
                    .Set("LastSentDate", now)
                    .Set("PeriodKey", periodKey ?? string.Empty),
                new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After });

            return result != null;
        }
        catch (MongoCommandException ex) when (ex.Code == 11000)
        {
            // The row exists and did not match: already sent.
            return false;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    private async Task SendShortcutAsync(BusinessIncomingMessageEvent incoming, int shortcutId, string kind)
    {
        var messages = (await quickReplyService.GetMessagesAsync(incoming.BusinessUserId, [shortcutId]))
            .OrderBy(p => p.MessageId)
            .ToList();
        if (messages.Count == 0)
        {
            logger.LogWarning("Business {Kind} shortcut {ShortcutId} of {UserId} has no messages", kind, shortcutId,
                incoming.BusinessUserId);
            return;
        }

        // No caller is waiting for this: the result is pushed to every session of the business account.
        var requestInfo = new RequestInfo(
            ConnectionId: "business",
            SessionId: 0,
            ReqMsgId: 0,
            UserId: incoming.BusinessUserId,
            AccessHashKeyId: 0,
            AuthKeyId: 0,
            PermAuthKeyId: 0,
            RequestId: Guid.NewGuid(),
            Layer: 0,
            Date: DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var randomIds = messages.Select(_ => Random.Shared.NextInt64(1, long.MaxValue)).ToList();
        var inputs = quickReplyService
            .BuildSendInputs(requestInfo, incoming.BusinessUserId, incoming.PeerUserId.ToUserPeer(), messages,
                randomIds)
            .Select(p => p with { IsBusinessAutoReply = true })
            .ToList();
        try
        {
            await messageAppService.SendMessageAsync(inputs);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not send business {Kind} message from {UserId} to {PeerUserId}", kind,
                incoming.BusinessUserId, incoming.PeerUserId);
        }
    }
}
