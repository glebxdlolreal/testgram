namespace MyTelegram.Domain.Aggregates.Dialog;

public class DialogSnapshot(
    long ownerId,
    int topMessage,
    int readInboxMaxId,
    int readOutboxMaxId,
    int unreadCount,
    Peer toPeer,
    bool unreadMark,
    bool pinned,
    int channelHistoryMinId,
    Draft? draft,
    int unreadMentionsCount,
    int? folderId,
    int unreadReactionsCount = 0,
    int unreadPollVotesCount = 0,
    List<string>? topicDraftKeys = null,
    bool hasAntiSpamMentionEffects = false,
    string? antiSpamMentionOperation = null,
    int antiSpamMentionMessageId = 0,
    bool antiSpamMentionConsumed = false,
    bool mentionRecountRequired = false,
    string? mentionReadOperation = null,
    List<int>? unreadReactionMessageIds = null
    )
    : ISnapshot
{
    public string? MentionReadOperation { get; } = mentionReadOperation;
    public string? AntiSpamMentionOperation { get; } = antiSpamMentionOperation;
    public int AntiSpamMentionMessageId { get; } = antiSpamMentionMessageId;
    public bool AntiSpamMentionConsumed { get; } = antiSpamMentionConsumed;
    public bool MentionRecountRequired { get; } = mentionRecountRequired;
    public bool HasAntiSpamMentionEffects { get; } = hasAntiSpamMentionEffects;
    public int ChannelHistoryMinId { get; } = channelHistoryMinId;

    public Draft? Draft { get; } = draft;

    /// <summary>
    /// Null in snapshots written before per topic drafts existed; the keys are rebuilt from the events
    /// replayed after the snapshot.
    /// </summary>
    public List<string>? TopicDraftKeys { get; } = topicDraftKeys;
    public int UnreadMentionsCount { get; } = unreadMentionsCount;

    /// <summary>
    /// Both counters default to 0 so snapshots written before they existed stay deserializable; the
    /// counts are rebuilt from events replayed after the snapshot.
    /// </summary>
    public int UnreadReactionsCount { get; } = unreadReactionsCount;
    public int UnreadPollVotesCount { get; } = unreadPollVotesCount;

    /// <summary>
    /// The messages behind <see cref="UnreadReactionsCount"/>. Null in snapshots written before it was
    /// tracked; such a counter still drops to zero on the next messages.readReactions.
    /// </summary>
    public List<int>? UnreadReactionMessageIds { get; } = unreadReactionMessageIds;
    public int? FolderId { get; } = folderId;

    public long OwnerId { get; } = ownerId;

    public bool Pinned { get; } = pinned;
    public int ReadInboxMaxId { get; } = readInboxMaxId;
    public int ReadOutboxMaxId { get; } = readOutboxMaxId;
    public Peer ToPeer { get; } = toPeer;
    public int TopMessage { get; } = topMessage;
    public int UnreadCount { get; } = unreadCount;
    public bool UnreadMark { get; } = unreadMark;
}
