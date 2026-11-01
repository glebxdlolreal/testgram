namespace MyTelegram.Messenger.Services;

public record SendMessageInput
{
    public SendMessageInput(RequestInfo requestInfo,
        long senderUserId,
        Peer toPeer,
        string message,
        long randomId,
        //byte[]? entities = null,
        TVector<IMessageEntity>? entities = null,
        //int? replyToMsgId = null,
        //IReplyTo? replyTo = null,
        IInputReplyTo? inputReplyTo = null,
        bool clearDraft = false,
        //byte[]? media = null,
        IMessageMedia? media = null,
        IInputMedia? inputMedia = null,
        //Peer? peer = null,
        SendMessageType sendMessageType = SendMessageType.Text,
        MessageType messageType = MessageType.Text,
        //MessageActionType messageActionType = MessageActionType.None,
        //string? messageActionData = null,
        IMessageAction? messageAction = null,
        long? groupId = null,
        int groupItemCount = 1,
        long? pollId = null,
        IReplyMarkup? replyMarkup = null,
        int? topMsgId = null,
        Peer? sendAs = null,
        //string? quickReplyShortcut = null,
        IInputQuickReplyShortcut? inputQuickReplyShortcut = null,
        long? effect = null,
        bool isSendGroupedMessage = false,
        bool isSendQuickReplyMessage = false,
        bool silent = false,
        int? scheduleDate = null,
        bool invertMedia = false,
        long? paidMessageStars = null,
        int? ttlPeriod = null,
        Peer? savedPeerId = null,
        int? messageId = null,
        ISuggestedPost? suggestedPost = null,
        MessageFwdHeader? fwdHeader = null,
        MessageSubType messageSubType = MessageSubType.Normal,
        string? postAuthor = null,
        int? views = null,
        bool noForwards = false,
        bool fromScheduled = false,
        int? scheduleRepeatPeriod = null,
        int? date = null
        )
    {
        Date = date;
        RequestInfo = requestInfo;
        SendMessageType = sendMessageType;
        MessageType = messageType;
        MessageAction = messageAction;
        //MessageActionType = messageActionType;
        SenderUserId = senderUserId;
        //Peer = peer;
        ToPeer = toPeer;
        Message = message;
        RandomId = randomId;
        //ReplyToMsgId = replyToMsgId;
        //MessageActionData = messageActionData;
        Entities = entities;
        InputReplyTo = inputReplyTo;
        ClearDraft = clearDraft;
        Media = media;
        InputMedia = inputMedia;
        GroupId = groupId;
        GroupItemCount = groupItemCount;
        PollId = pollId;
        ReplyMarkup = replyMarkup;
        TopMsgId = topMsgId;
        SendAs = sendAs;
        InputQuickReplyShortcut = inputQuickReplyShortcut;
        Effect = effect;
        IsSendGroupedMessage = isSendGroupedMessage;
        IsSendQuickReplyMessage = isSendQuickReplyMessage;
        Silent = silent;
        ScheduleDate = scheduleDate;
        InvertMedia = invertMedia;
        PaidMessageStars = paidMessageStars;
        TtlPeriod = ttlPeriod;
        SavedPeerId = savedPeerId;
        MessageId = messageId;
        SuggestedPost = suggestedPost;
        FwdHeader = fwdHeader;
        MessageSubType = messageSubType;
        PostAuthor = postAuthor;
        Views = views;
        NoForwards = noForwards;
        FromScheduled = fromScheduled;
        ScheduleRepeatPeriod = scheduleRepeatPeriod;
    }

    /// <summary>
    /// Timestamp the message must carry instead of the current time. Only an imported history sets it:
    /// its messages keep the date they had in the chat they came from.
    /// See https://corefork.telegram.org/api/import
    /// </summary>
    public int? Date { get; }

    public bool ClearDraft { get; }
    public TVector<IMessageEntity>? Entities { get; }
    public IInputReplyTo? InputReplyTo { get; }
    //public IReplyTo? ReplyTo { get; }
    public long? GroupId { get; }
    public int GroupItemCount { get; } = 1;
    public long? PollId { get; }
    public IReplyMarkup? ReplyMarkup { get; }
    public int? TopMsgId { get; }
    public Peer? SendAs { get; }
    public IInputQuickReplyShortcut? InputQuickReplyShortcut { get; }
    public long? Effect { get; }
    public bool IsSendGroupedMessage { get; }
    public bool IsSendQuickReplyMessage { get; }

    /// <summary>A greeting or away message the server sends for a business account.</summary>
    public bool IsBusinessAutoReply { get; init; }

    /// <summary>Set when a connected business bot sends this through <c>invokeWithBusinessConnection</c>.</summary>
    public long? ViaBusinessBotId { get; init; }
    public bool Silent { get; }
    public int? ScheduleDate { get; }

    /// <summary>
    /// <c>schedule_repeat_period</c>: the message is re-scheduled this many seconds after every send.
    /// Premium only, single messages only.
    /// </summary>
    public int? ScheduleRepeatPeriod { get; }
    public bool InvertMedia { get; }
    public long? PaidMessageStars { get; }
    public int? TtlPeriod { get; }
    public Peer? SavedPeerId { get; }
    public int? MessageId { get; }
    public ISuggestedPost? SuggestedPost { get; }
    public MessageFwdHeader? FwdHeader { get; }
    public MessageSubType MessageSubType { get; }
    public string? PostAuthor { get; }
    public int? Views { get; }
    public bool NoForwards { get; }

    /// <summary>
    /// Set when the message is being flushed from the schedule queue, so the sent message keeps the
    /// <c>from_scheduled</c> flag. See https://corefork.telegram.org/api/scheduled-messages
    /// </summary>
    public bool FromScheduled { get; }
    public IMessageMedia? Media { get; }
    public IInputMedia? InputMedia { get; }

    public string Message { get; }
    //public string? MessageActionData { get; }
    //public MessageActionType MessageActionType { get; }
    public MessageType MessageType { get; }

    public IMessageAction? MessageAction { get; }

    //public Peer? Peer { get; }
    public long RandomId { get; }
    //public int? ReplyToMsgId { get; }
    public long SenderUserId { get; }
    public RequestInfo RequestInfo { get; }
    public SendMessageType SendMessageType { get; }
    public Peer ToPeer { get; }
}
