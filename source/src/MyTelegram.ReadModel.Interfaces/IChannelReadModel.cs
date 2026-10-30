namespace MyTelegram.ReadModel.Interfaces;

public interface IChannelReadModel : IReadModel
{
    string? About { get; }
    long AccessHash { get; }
    //GeoPoint? GeoPoint { get; }
    //string? Address { get; }

    List<ChatAdmin> AdminList { get; }
    List<long> Bots { get; }
    bool Broadcast { get; }

    long ChannelId { get; }
    long CreatorId { get; }

    int Date { get; }

    ChatBannedRights? DefaultBannedRights { get; }
    string Id { get; }
    int LastSendDate { get; }

    long LastSenderPeerId { get; }
    bool MegaGroup { get; }
    int? ParticipantsCount { get; }
    int Pts { get; }
    bool Signatures { get; }
    bool SlowModeEnabled { get; }
    string Title { get; }
    int TopMessageId { get; }
    string? UserName { get; }
    bool Verified { get; }
    bool Fake { get; }
    bool Scam { get; }
    long? LinkedChatId { get; }
    bool Forum { get; }
    bool ForumTabs { get; }
    //int? TtlPeriod { get; }

    long? PhotoId { get; }
    bool NoForwards { get; }
    PeerColor? Color { get; }
    PeerColor? ProfileColor { get; }
    long? BackgroundEmojiId { get; }
    int? Level { get; }
    bool HasLink { get; }
    bool IsDeleted { get; }
    EmojiStatus? EmojiStatus { get; }
    bool SignatureProfiles { get; }
    int? SubscriptionUntilDate { get; }
    bool HiddenPreHistory { get; }
    List<UsernameInfo>? Usernames { get; }
    bool ParticipantsHidden { get; }

    /// <summary>
    /// <c>channel.autotranslation</c>: the channel translates every post for all users, including those
    /// without Premium. See https://corefork.telegram.org/api/translation#autotranslation-for-channels
    /// </summary>
    bool Autotranslation { get; }
    bool AntiSpam { get; }
    bool Gigagroup { get; }
    bool JoinToSend { get; }
    bool JoinRequest { get; }
    bool IsMonoforum { get; }
    bool BroadcastMessagesAllowed { get; }
    long? LinkedMonoforumId { get; }
    bool PaidReactionsEnabled { get; }
    string? MainProfileTab { get; }
    long? SendPaidMessagesStars { get; }
}
