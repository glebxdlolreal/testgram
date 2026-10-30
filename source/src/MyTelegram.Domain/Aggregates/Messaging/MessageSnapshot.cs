namespace MyTelegram.Domain.Aggregates.Messaging;

public class MessageSnapshot(MessageItem messageItem,
        List<InboxItem> inboxItems,
        int senderMessageId,
        bool pinned,
        int editDate,
        bool edited,
        int pts,
        bool isDeleted,
        bool isChannelDeleted = false,
        Guid? channelDeletionRequestId = null,
        int channelDeletionProofVersion = 0,
        Guid? antiSpamDeletionCancelledRequestId = null)
    : ISnapshot
{
    //bool editHide,
    //EditHide = editHide;

    public int EditDate { get; } = editDate;

    //public bool EditHide { get; }
    public bool Edited { get; } = edited;

    public List<InboxItem> InboxItems { get; } = inboxItems;
    public MessageItem MessageItem { get; } = messageItem;
    public bool Pinned { get; } = pinned;
    public int Pts { get; } = pts;
    public bool IsDeleted { get; } = isDeleted;
    public bool IsChannelDeleted { get; } = isChannelDeleted;
    public Guid? ChannelDeletionRequestId { get; } = channelDeletionRequestId;
    public Guid? AntiSpamDeletionCancelledRequestId { get; } = antiSpamDeletionCancelledRequestId;
    public int ChannelDeletionProofVersion { get; } = channelDeletionProofVersion;
    public int SenderMessageId { get; } = senderMessageId;
}
