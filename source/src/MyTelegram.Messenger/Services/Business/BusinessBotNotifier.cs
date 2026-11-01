using MyTelegram.Messenger.Services.Bots;
using MyTelegram.Messenger.Services.Phone;

namespace MyTelegram.Messenger.Services.Business;

/// <summary>
/// What a <a href="https://corefork.telegram.org/api/bots/connected-business-bots">connected business bot</a>
/// is told: its connection (<c>updateBotBusinessConnect</c>) and the messages of the private chats it manages
/// (<c>updateBotNewBusinessMessage</c>, <c>updateBotEditBusinessMessage</c>, <c>updateBotDeleteBusinessMessage</c>),
/// all qts-sequenced through <see cref="IBotUpdatesSender"/> so an offline bot gets them from getDifference.
/// </summary>
public interface IBusinessBotNotifier
{
    Task NotifyConnectionAsync(BusinessConnection connection, bool disabled);

    TBotBusinessConnection ToTl(BusinessConnection connection, bool disabled);

    /// <summary>
    /// Messages that appeared (or were edited) in <paramref name="ownerUserId"/>'s private chat with
    /// <paramref name="peerUserId"/>, as stored in the owner's box.
    /// </summary>
    Task NotifyMessagesAsync(long ownerUserId, long peerUserId, IReadOnlyList<MessageItem> items, bool edited,
        long? skipBotId = null);

    Task NotifyDeletedAsync(long ownerUserId, long peerUserId, IReadOnlyList<int> messageIds);

    /// <summary>
    /// The answer to a send or edit the bot made through <c>invokeWithBusinessConnection</c>: tdlib accepts
    /// only <c>updates</c> made of <c>updateBotNewBusinessMessage</c>.
    /// </summary>
    Task<IUpdates?> BuildSentResultAsync(RequestInfo requestInfo, IReadOnlyList<MessageItem> items);

    Task<List<IUser>> GetUsersForBotAsync(long botId, IEnumerable<long> userIds);

    /// <summary>
    /// The "bot manages this chat" bar (<c>peerSettings.business_bot_*</c>), shown to the account owner in
    /// private chats its connected bot works in.
    /// </summary>
    Task FillPeerSettingsAsync(MyTelegram.Schema.IPeerSettings settings, long selfUserId, long peerUserId);
}

public sealed class BusinessBotNotifier(
    IBusinessConnectionStore connectionStore,
    IBotUpdatesSender botUpdatesSender,
    IMessageConverterService messageConverterService,
    IUserConverterService userConverterService,
    IUserAccessHashKeyCache userAccessHashKeyCache,
    IQueryProcessor queryProcessor,
    IUserAppService userAppService,
    IOptions<MyTelegramMessengerServerOptions> options,
    ILogger<BusinessBotNotifier> logger) : IBusinessBotNotifier, ITransientDependency
{
    public TBotBusinessConnection ToTl(BusinessConnection connection, bool disabled) => new()
    {
        Disabled = disabled,
        ConnectionId = connection.ConnectionId,
        UserId = connection.UserId,
        DcId = options.Value.ThisDcId > 0 ? options.Value.ThisDcId : 1,
        Date = connection.Date,
        Rights = connection.Rights.ToTl()
    };

    public async Task NotifyConnectionAsync(BusinessConnection connection, bool disabled)
    {
        var users = await GetUsersForBotAsync(connection.BotId, [connection.UserId]);
        await botUpdatesSender.PushUpdateToBotAsync(connection.BotId, qts => new TUpdateBotBusinessConnect
        {
            Connection = ToTl(connection, disabled),
            Qts = qts
        }, users);
    }

    public async Task NotifyMessagesAsync(long ownerUserId, long peerUserId, IReadOnlyList<MessageItem> items,
        bool edited, long? skipBotId = null)
    {
        try
        {
            var connection = await connectionStore.GetByUserAsync(ownerUserId);
            if (connection == null || connection.BotId == skipBotId || peerUserId == connection.BotId ||
                !await connectionStore.AppliesToAsync(connection, peerUserId))
            {
                return;
            }

            var users = await GetUsersForBotAsync(connection.BotId, [ownerUserId, peerUserId]);
            foreach (var item in items)
            {
                var message = messageConverterService.ToMessage(ownerUserId, item);
                var replyTo = await GetReplyToMessageAsync(ownerUserId, item);
                await botUpdatesSender.PushUpdateToBotAsync(connection.BotId, qts => edited
                    ? new TUpdateBotEditBusinessMessage
                    {
                        ConnectionId = connection.ConnectionId,
                        Message = message,
                        ReplyToMessage = replyTo,
                        Qts = qts
                    }
                    : new TUpdateBotNewBusinessMessage
                    {
                        ConnectionId = connection.ConnectionId,
                        Message = message,
                        ReplyToMessage = replyTo,
                        Qts = qts
                    }, users);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not notify the business bot of {UserId}", ownerUserId);
        }
    }

    public async Task NotifyDeletedAsync(long ownerUserId, long peerUserId, IReadOnlyList<int> messageIds)
    {
        try
        {
            var connection = await connectionStore.GetByUserAsync(ownerUserId);
            if (connection == null || !await connectionStore.AppliesToAsync(connection, peerUserId))
            {
                return;
            }

            var users = await GetUsersForBotAsync(connection.BotId, [ownerUserId, peerUserId]);
            await botUpdatesSender.PushUpdateToBotAsync(connection.BotId, qts => new TUpdateBotDeleteBusinessMessage
            {
                ConnectionId = connection.ConnectionId,
                Peer = new TPeerUser { UserId = peerUserId },
                Messages = new TVector<int>(messageIds),
                Qts = qts
            }, users);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not notify the business bot of {UserId} about deletions", ownerUserId);
        }
    }

    public async Task<IUpdates?> BuildSentResultAsync(RequestInfo requestInfo, IReadOnlyList<MessageItem> items)
    {
        if (requestInfo.BusinessBotId is not { } botId)
        {
            return null;
        }

        var ownerUserId = requestInfo.UserId;
        var connection = await connectionStore.GetByUserAsync(ownerUserId);
        if (connection == null || connection.BotId != botId)
        {
            return null;
        }

        var peerIds = items.Select(p => p.ToPeer.PeerId).Distinct();
        var users = await GetUsersForBotAsync(botId, peerIds.Append(ownerUserId));
        var updates = new List<IUpdate>();
        foreach (var item in items)
        {
            updates.Add(new TUpdateBotNewBusinessMessage
            {
                ConnectionId = connection.ConnectionId,
                Message = messageConverterService.ToMessage(ownerUserId, item, layer: requestInfo.Layer),
                ReplyToMessage = await GetReplyToMessageAsync(ownerUserId, item),
                Qts = 0
            });
        }

        return new TUpdates
        {
            Updates = new TVector<IUpdate>(updates),
            Users = new TVector<IUser>(users),
            Chats = [],
            Date = DateTime.UtcNow.ToTimestamp(),
            Seq = 0
        };
    }

    public async Task<List<IUser>> GetUsersForBotAsync(long botId, IEnumerable<long> userIds)
    {
        var accessHashKeyId = await userAccessHashKeyCache.GetAsync(botId) ?? 0;
        var request = new RequestInfo("business", 0, 0, botId, accessHashKeyId, 0, 0, Guid.NewGuid(), 0,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var users = await userConverterService.GetUserListAsync(request, userIds.Distinct().ToList());
        return users.Cast<IUser>().ToList();
    }

    public async Task FillPeerSettingsAsync(MyTelegram.Schema.IPeerSettings settings, long selfUserId, long peerUserId)
    {
        if (settings is not MyTelegram.Schema.TPeerSettings peerSettings || peerUserId == selfUserId ||
            PeerKindHelper.IsSystemUserId(peerUserId))
        {
            return;
        }

        var connection = await connectionStore.GetByUserAsync(selfUserId);
        if (connection == null || !await connectionStore.SelectsAsync(connection, peerUserId))
        {
            return;
        }

        var peer = await userAppService.GetAsync(peerUserId);
        if (peer == null || peer.Bot)
        {
            return;
        }

        var bot = await userAppService.GetAsync(connection.BotId);
        peerSettings.BusinessBotId = connection.BotId;
        peerSettings.BusinessBotManageUrl = bot?.UserName != null
            ? $"https://t.me/{bot.UserName}?start=bizChat{peerUserId}"
            : $"tg://user?id={connection.BotId}";
        peerSettings.BusinessBotPaused = await connectionStore.IsPausedAsync(selfUserId, peerUserId);
        peerSettings.BusinessBotCanReply = connection.Rights.Reply;
    }

    private async Task<IMessage?> GetReplyToMessageAsync(long ownerUserId, MessageItem item)
    {
        if (item.InputReplyTo is not TInputReplyToMessage { ReplyToPeerId: null } reply)
        {
            return null;
        }

        var replied = await queryProcessor.ProcessAsync(
            new GetMessageByIdQuery(MessageId.Create(ownerUserId, reply.ReplyToMsgId).Value));
        return replied == null ? null : messageConverterService.ToMessage(ownerUserId, replied);
    }
}
