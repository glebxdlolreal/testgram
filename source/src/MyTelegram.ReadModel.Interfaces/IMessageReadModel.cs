namespace MyTelegram.ReadModel.Interfaces;

public interface IMessageReadModel : IReadModel, IReactionItem
{
    int Date { get; }
    int? EditDate { get; }
    bool EditHide { get; }
    byte[]? Entities { get; }
    TVector<IMessageEntity>? Entities2 { get; }
    MessageFwdHeader? FwdHeader { get; }
    long? GroupedId { get; }
    string Id { get; }
    byte[]? Media { get; }
    IMessageMedia? Media2 { get; }
    string Message { get; }
    string? MessageActionData { get; }
    IMessageAction? MessageAction { get; }
    MessageActionType MessageActionType { get; }
    MessageType MessageType { get; }
    bool Out { get; }
    bool NoForwards { get; }
    long OwnerPeerId { get; }
    bool Pinned { get; }
    bool Post { get; }
    string? PostAuthor { get; }
    int Pts { get; }
    int? ReplyToMsgId { get; }
    int? TopMsgId { get; }

    /// <summary>
    /// True only when <see cref="TopMsgId"/> is a forum topic; a comment thread also has a TopMsgId
    /// but is not a forum topic. See https://corefork.telegram.org/api/threads
    /// </summary>
    bool ForumTopic { get; }
    int SenderMessageId { get; }
    long SenderPeerId { get; }
    long SenderUserId { get; }
    SendMessageType SendMessageType { get; }
    bool Silent { get; }
    long ToPeerId { get; }
    PeerType ToPeerType { get; }

    Peer? SavedPeerId { get; }
    int? Views { get; }
    long? LinkedChannelId { get; }
    int Replies { get; }
    long? PollId { get; }
    byte[]? ReplyMarkup { get; }
    IReplyMarkup? ReplyMarkup2 { get; }
    //IInputReplyTo? ReplyTo { get; }
    Peer? SendAs { get; }
    MessageReply? Reply { get; }
    long? PostChannelId { get; }
    int? PostMessageId { get; }
    bool IsQuickReplyMessage { get; }

    /// <summary><c>via_business_bot_id</c>: the connected business bot that sent this for its owner.</summary>
    long? ViaBusinessBotId { get; }
    int? ShortcutId { get; }
    QuickReplyItem? QuickReplyItem { get; }
    Guid BatchId { get; }
    long? Effect { get; }
    bool FromScheduled { get; }
    int? ScheduleDate { get; }
    int? TtlPeriod { get; }
    int? ExpirationTime { get; }
    bool InvertMedia { get; }
    bool PublicPosts { get; }
    List<string> Hashtags { get; }
    List<long>? MentionedUserIds { get; }
    long? TodoId { get; }
    ReadOnlyMemory<byte>? EncryptedData { get; }
    long? PaidMessageStars { get; }
    ISuggestedPost? SuggestedPost { get; }
    bool PaidSuggestedPostStars { get; }
    bool PaidSuggestedPostTon { get; }

    /// <summary>
    /// Validity period of a <a href="https://corefork.telegram.org/api/live-location">live location</a>,
    /// mirrored out of the stored <c>messageMediaGeoLive</c> so a query can select live locations
    /// without deserializing the media blob — <c>MessageType.Geo</c> alone also matches static
    /// locations and venues. Null for every other kind of message. <see cref="int.MaxValue"/> means
    /// the location is shared until switched off; otherwise it is active while
    /// <see cref="Date"/> + this value is in the future.
    /// </summary>
    int? GeoLivePeriod { get; }

    /// <summary>Direction of movement of a live location, in degrees (1-360), or null when unknown.</summary>
    int? GeoLiveHeading { get; }

    /// <summary>Proximity-alert radius of a live location in meters (0-100000), or null.</summary>
    int? GeoLiveProximityRadius { get; }

    /// <summary>Latitude of the last reported point of a live location.</summary>
    double? GeoLat { get; }

    /// <summary>Longitude of the last reported point of a live location.</summary>
    double? GeoLong { get; }
}
