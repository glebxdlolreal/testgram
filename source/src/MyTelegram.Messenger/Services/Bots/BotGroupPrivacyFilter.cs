namespace MyTelegram.Messenger.Services.Bots;

/// <summary>
/// Group privacy mode (@BotFather's /setprivacy): a bot that has it on is left out of the delivery of
/// group messages that are not meant for it.
/// See https://core.telegram.org/bots/features#privacy-mode
/// </summary>
public interface IBotGroupPrivacyFilter
{
    /// <summary>The bot members of <paramref name="channel"/> that must not receive <paramref name="item"/>.</summary>
    Task<List<long>> GetExcludedBotIdsAsync(IChannelReadModel channel, MessageItem item, IReadOnlyCollection<long>? mentionedUserIds);

    /// <summary>
    /// The part of <paramref name="messages"/> a bot catching up through getChannelDifference may see:
    /// the same rule the live delivery applies.
    /// </summary>
    Task<IReadOnlyCollection<IMessageReadModel>> FilterForBotAsync(long botUserId, IChannelReadModel channel,
        IChannelMemberReadModel member, IReadOnlyCollection<IMessageReadModel> messages);
}

/// <inheritdoc />
public class BotGroupPrivacyFilter(
    IQueryProcessor queryProcessor,
    IUserAppService userAppService) : IBotGroupPrivacyFilter, ITransientDependency
{
    /// <summary>More bots than this in one group is not a case worth a second page.</summary>
    private const int MaxBots = 200;

    public async Task<List<long>> GetExcludedBotIdsAsync(IChannelReadModel channel, MessageItem item,
        IReadOnlyCollection<long>? mentionedUserIds)
    {
        // Channels deliver every post to member bots, and service messages reach every bot.
        if (channel.Broadcast || item.MessageAction != null)
        {
            return [];
        }

        // "'Enable' - your bot will only receive messages that either start with the '/' symbol or
        // mention the bot by username." (real BotFather's /setprivacy prompt)
        if (StartsWithCommand(item))
        {
            return [];
        }

        var bots = await queryProcessor.ProcessAsync(
            new GetChannelMembersByChannelIdQuery(channel.ChannelId, [], 0, MaxBots, OnlyBots: true));
        var admins = channel.AdminList.Select(p => p.UserId).Append(channel.CreatorId).ToHashSet();

        // Bot admins always receive all messages.
        var candidates = bots
            .Where(p => !p.IsAdmin && !admins.Contains(p.UserId) && p.UserId != item.SenderUserId)
            .Where(p => mentionedUserIds?.Contains(p.UserId) != true)
            .Select(p => p.UserId)
            .ToList();
        if (candidates.Count == 0)
        {
            return [];
        }

        var excluded = (await userAppService.GetListAsync(candidates))
            .Where(p => p.Bot && p.BotPrivacyMode)
            .Select(p => p.UserId)
            .ToList();
        if (excluded.Count == 0)
        {
            return excluded;
        }

        // Replies to the bot's own messages are meant for it.
        var replyToMsgId = item.InputReplyTo.ToReplyToMsgId();
        if (replyToMsgId is > 0)
        {
            var repliedTo = await queryProcessor.ProcessAsync(
                new GetMessageByIdQuery(MessageId.Create(channel.ChannelId, replyToMsgId.Value).Value));
            if (repliedTo != null)
            {
                excluded.Remove(repliedTo.SenderPeerId);
            }
        }

        return excluded;
    }

    public async Task<IReadOnlyCollection<IMessageReadModel>> FilterForBotAsync(long botUserId,
        IChannelReadModel channel, IChannelMemberReadModel member, IReadOnlyCollection<IMessageReadModel> messages)
    {
        if (channel.Broadcast || member.IsAdmin || channel.CreatorId == botUserId ||
            channel.AdminList.Any(p => p.UserId == botUserId) || messages.Count == 0)
        {
            return messages;
        }

        var bot = await userAppService.GetAsync(botUserId);
        if (bot is not { Bot: true, BotPrivacyMode: true })
        {
            return messages;
        }

        var byId = messages.ToDictionary(p => p.MessageId, p => p.SenderPeerId);
        var missingReplyIds = messages.Where(p => p.ReplyToMsgId is > 0 && !byId.ContainsKey(p.ReplyToMsgId.Value))
            .Select(p => p.ReplyToMsgId!.Value).Distinct().ToList();
        foreach (var replyId in missingReplyIds)
        {
            var repliedTo = await queryProcessor.ProcessAsync(
                new GetMessageByIdQuery(MessageId.Create(channel.ChannelId, replyId).Value));
            if (repliedTo != null)
            {
                byId[replyId] = repliedTo.SenderPeerId;
            }
        }

        return messages.Where(p =>
                p.MessageAction != null ||
                p.SenderPeerId == botUserId ||
                p.MentionedUserIds?.Contains(botUserId) == true ||
                p.Entities2?.Any(e => e is TMessageEntityBotCommand { Offset: 0 }) == true ||
                p.Message.StartsWith('/') ||
                (p.ReplyToMsgId is > 0 && byId.TryGetValue(p.ReplyToMsgId.Value, out var repliedSender) &&
                 repliedSender == botUserId))
            .ToList();
    }

    /// <summary>
    /// A leading /command. The command entity is detected on send and kept in clear even when the text
    /// itself is stored encrypted.
    /// </summary>
    internal static bool StartsWithCommand(MessageItem item) =>
        item.Entities?.Any(p => p is TMessageEntityBotCommand { Offset: 0 }) == true ||
        item.Message.StartsWith('/');
}
