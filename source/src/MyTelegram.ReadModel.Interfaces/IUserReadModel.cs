using MyTelegram.Schema;

namespace MyTelegram.ReadModel.Interfaces;

public interface IUserReadModel : IReadModel
{
    string? About { get; }
    long AccessHash { get; }
    int AccountTtl { get; }
    bool Bot { get; }
    int? BotInfoVersion { get; }
    string FirstName { get; }
    bool HasPassword { get; }
    string Id { get; }
    bool IsOnline { get; }
    string? LastName { get; }
    DateTime LastUpdateDate { get; }
    string PhoneNumber { get; }
    int? PinnedMsgId { get; }
    List<int> PinnedMsgIdList { get; } // = new();
    //byte[]? ProfilePhoto { get; }
    bool SensitiveCanChange { get; } //= true;
    bool SensitiveEnabled { get; }
    bool ShowContactSignUpNotification { get; }
    bool Fake { get; }
    bool Scam { get; }
    bool Support { get; }
    long UserId { get; }
    string? UserName { get; }
    bool Verified { get; }
    bool Premium { get; }
    string? Email { get; }
    long? EmojiStatusDocumentId { get; }
    int? EmojiStatusValidUntil { get; }
    long? EmojiStatusCollectibleId { get; }
    List<long> RecentEmojiStatuses { get; }
    VideoSizeEmojiMarkup? VideoEmojiMarkup { get; }

    long? ProfilePhotoId { get; }
    long? PersonalPhotoId { get; }
    long? FallbackPhotoId { get; }
    //int? Color { get; }
    //long? BackgroundEmojiId { get; }
    PeerColor? Color { get; }
    PeerColor? ProfileColor { get; }
    GlobalPrivacySettings? GlobalPrivacySettings { get; }
    long? PersonalChannelId { get; }
    Birthday? Birthday { get; }

    bool BotHasMainApp { get; }
    int? BotActiveUsers { get; }

    List<UsernameInfo>? Usernames { get; }
    DateTime? CreationTime { get; }
    int? ProfilePhotoUpdateDate { get; }
    int? UserNameUpdateDate { get; }
    bool? IsDeleted { get; }

    // Telegram Business profile, see https://corefork.telegram.org/api/business. Stored under new names:
    // documents written by the old handlers kept TL objects under BusinessWorkHours & co.
    BusinessWorkHoursItem? BusinessWorkHoursSettings { get; }
    BusinessLocationItem? BusinessLocationSettings { get; }
    BusinessGreetingItem? BusinessGreetingSettings { get; }
    BusinessAwayItem? BusinessAwaySettings { get; }
    BusinessIntroItem? BusinessIntroSettings { get; }

    /// <summary><c>user.bot_business</c>: the bot can be connected to business accounts.</summary>
    bool BotBusiness { get; }

    int? DefaultHistoryTTL { get; }
    string? MainProfileTab { get; }
}
