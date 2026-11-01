namespace MyTelegram;

public record MessageItem
(Peer OwnerPeer,
    Peer ToPeer,
    Peer SenderPeer,
    long SenderUserId,
    int MessageId,
    string Message,
    int Date,
    long RandomId,
    bool IsOut,
    SendMessageType SendMessageType = SendMessageType.Text,
    MessageType MessageType = MessageType.Text,
    MessageSubType MessageSubType = MessageSubType.Normal,
    IInputReplyTo? InputReplyTo = null,
    IMessageAction? MessageAction = null,
    //string? MessageActionData = null,
    MessageActionType MessageActionType = MessageActionType.None,
    TVector<IMessageEntity>? Entities = null,
    IMessageMedia? Media = null,
    long? GroupId = null,
    bool Post = false,
    MessageFwdHeader? FwdHeader = null,
    int? Views = null,
    long? PollId = null,
    IReplyMarkup? ReplyMarkup = null,
    long? LinkedChannelId = null,
    int? TopMsgId = null,
    string? PostAuthor = null,
    Peer? SavedPeerId = null,
    Peer? SendAs = null,
    MessageReply? Reply = null,
    bool EditHide = false,
    bool IsForwardFromChannelPost = false,
    long? PostChannelId = null,
    int? PostMessageId = null,
    //bool IsQuickReply = false,
    QuickReplyItem? QuickReplyItem = null,
    Guid? BatchId = null,
    long? Effect = null,
    List<ReactionCount>? Reactions = null,
    List<MessagePeerReaction>? RecentReactions = null,
    int? EditDate = null,
    IReadOnlyCollection<InboxItem>? InboxItems = null,
    int Pts = 0,
    List<ReplyToMsgItem>? ReplyToMsgItems = null,
    bool Silent = false,
    int? ScheduleDate = null,
    int? ScheduleMessageId = null,
    int? TtlPeriod = null,
    bool IsTtlFromDefaultSetting = false,
    bool Pinned = false,
    bool InvertMedia = false,
    bool PublicPosts = false,
    List<string>? Hashtags = null,
    List<long>? MentionedUserIds = null,
    ReadOnlyMemory<byte>? EncryptedData = null,
    ReadOnlyMemory<byte>? InboxMessageEncryptedData = null,
    long? PaidMessageStars = null,
    ISuggestedPost? SuggestedPost = null,
    bool PaidSuggestedPostStars = false,
    bool PaidSuggestedPostTon = false,
    bool NoForwards = false,
    int? ReportDeliveryUntilDate = null,
    /// <summary>
    /// True only when <see cref="TopMsgId"/> points at a forum topic. A comment thread also carries a
    /// TopMsgId but is not a forum topic, so the two must not be conflated in messageReplyHeader.
    /// See https://corefork.telegram.org/api/threads
    /// </summary>
    bool ForumTopic = false,
    /// <summary>
    /// True when the message was flushed from the schedule queue. <see cref="ScheduleDate"/> is cleared
    /// at that point, so the <c>from_scheduled</c> flag has to be carried separately.
    /// See https://corefork.telegram.org/api/scheduled-messages
    /// </summary>
    bool FromScheduled = false,
    /// <summary>
    /// Sent by the server on behalf of a business account: a greeting or away message. Such a message never
    /// triggers the recipient's own automatic replies. See https://corefork.telegram.org/api/business
    /// </summary>
    bool IsBusinessAutoReply = false,
    /// <summary>
    /// The connected business bot that sent this message on behalf of its owner (<c>via_business_bot_id</c>).
    /// See https://corefork.telegram.org/api/bots/connected-business-bots
    /// </summary>
    long? ViaBusinessBotId = null
//int? DefaultHistoryTtl = null,
//int? Ttl = null
);
