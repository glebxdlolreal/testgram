namespace MyTelegram.Domain.Aggregates.User;

public class UserState : AggregateState<UserAggregate, UserId, UserState>,
    IApply<UserCreatedEvent>,
    IApply<UserProfileUpdatedEvent>,
    IApply<CheckUserStatusCompletedEvent>,
    IApply<UserSupportHasSetEvent>,
    IApply<UserVerifiedHasSetEvent>,
    IApply<UserNameUpdatedEvent>,
    IApply<UserUsernamesChangedEvent>,
    IApply<UserProfilePhotoChangedEvent>,
    IApply<UserProfilePhotoUploadedEvent>,
    IApply<UserColorUpdatedEvent>,
    IApply<UserEmojiStatusUpdatedEvent>,
    IApply<UserRecentEmojiStatusesClearedEvent>,
    IApply<UserGlobalPrivacySettingsChangedEvent>,
    IApply<UserAccountTtlUpdatedEvent>,
    IApply<UserContactSignUpNotificationUpdatedEvent>,
    IApply<UserPremiumStatusChangedEvent>,
    IApply<PersonalChannelUpdatedEvent>,
    IApply<BirthdayUpdatedEvent>,
    IApply<BusinessWorkHoursUpdatedEvent>,
    IApply<BusinessLocationUpdatedEvent>,
    IApply<BusinessGreetingMessageUpdatedEvent>,
    IApply<BusinessAwayMessageUpdatedEvent>,
    IApply<BusinessIntroUpdatedEvent>,
    IApply<UserAboutUpdatedEvent>,
    IApply<UserFirstNameUpdatedEvent>,
    IApply<UserDeletedEvent>
{
    public long AccessHash { get; private set; }
    public string FirstName { get; private set; } = null!;
    public bool HasPassword { get; private set; }
    public bool IsOnline { get; private set; }
    public string? LastName { get; private set; }
    public string PhoneNumber { get; private set; } = string.Empty;
    public byte[]? Photo { get; private set; }
    public bool SensitiveEnabled { get; private set; }
    public bool Support { get; private set; }
    public long UserId { get; private set; }
    public string? UserName { get; private set; }
    public bool Verified { get; private set; }
    public bool IsBot { get; private set; }
    public bool IsDeleted { get; private set; }
    //public bool IsPremium { get; private set; }
    public long? EmojiStatusDocumentId { get; private set; }
    public int? EmojiStatusValidUntil { get; private set; }
    /// <summary>
    /// Set when the current emoji status comes from a collectible gift, see
    /// <a href="https://core.telegram.org/api/emoji-status">emoji statuses</a>.
    /// </summary>
    public long? EmojiStatusCollectibleId { get; private set; }
    public long? PhotoId { get; private set; }
    public long? FallbackPhotoId { get; private set; }
    /// <summary>
    /// Recently used emoji statuses, most recent first, capped at <see cref="MaxRecentEmojiStatuses"/>.
    /// Served by <c>account.getRecentEmojiStatuses</c>.
    /// </summary>
    public List<long> RecentEmojiStatus { get; private set; } = [];

    /// <summary>How many recent emoji statuses are kept, matching the official server.</summary>
    public const int MaxRecentEmojiStatuses = 10;
    //public int Color { get; private set; }
    //public long? BackgroundEmojiId { get; private set; }

    public PeerColor? Color { get; private set; }
    public PeerColor? ProfileColor { get; private set; }
    public GlobalPrivacySettings GlobalPrivacySettings { get; private set; }
    public bool Premium { get; private set; }
    public long? PersonalChannelId { get; private set; }
    public Birthday? Birthday { get; private set; }
    public int? ProfilePhotoUpdateDate { get; private set; }
    public int? UserNameUpdateDate { get; private set; }
    public int AccountTtl { get; private set; }
    public bool ShowContactSignUpNotification { get; private set; }

    public void Apply(CheckUserStatusCompletedEvent aggregateEvent)
    {
        //throw new NotImplementedException();
    }

    public void Apply(UserCreatedEvent aggregateEvent)
    {
        UserId = aggregateEvent.UserId;
        PhoneNumber = aggregateEvent.PhoneNumber;
        FirstName = aggregateEvent.FirstName;
        LastName = aggregateEvent.LastName;
        SensitiveEnabled = true;
        AccessHash = aggregateEvent.AccessHash;
        IsBot = aggregateEvent.Bot;
        UserName = aggregateEvent.UserName;
        if (!string.IsNullOrEmpty(aggregateEvent.UserName))
        {
            UserNameUpdateDate = aggregateEvent.CreationTime.ToTimestamp();
        }
    }

    /// <summary>
    /// The usernames vector lives in the read model only; the aggregate keeps no copy of it.
    /// </summary>
    public void Apply(UserUsernamesChangedEvent aggregateEvent)
    {
    }

    public void Apply(UserNameUpdatedEvent aggregateEvent)
    {
        UserName = aggregateEvent.UserItem.UserName;
        UserNameUpdateDate = aggregateEvent.Date;
    }
   
    public void Apply(UserProfilePhotoChangedEvent aggregateEvent)
    {
        //Photo = aggregateEvent.UserItem.ProfilePhoto;
        if (aggregateEvent.Fallback)
        {
            FallbackPhotoId = aggregateEvent.PhotoId;
        }
        else
        {
            PhotoId = aggregateEvent.PhotoId;
        }
    }

    public void Apply(UserProfileUpdatedEvent aggregateEvent)
    {
        if (!string.IsNullOrEmpty(aggregateEvent.FirstName))
        {
            FirstName = aggregateEvent.FirstName;
        }

        LastName = aggregateEvent.LastName;
    }

    public void Apply(UserSupportHasSetEvent aggregateEvent)
    {
        Support = aggregateEvent.Support;
    }

    public void Apply(UserVerifiedHasSetEvent aggregateEvent)
    {
        Verified = aggregateEvent.Verified;
    }
    public void LoadFromSnapshot(UserSnapshot snapshot)
    {
        UserId = snapshot.UserId;
        IsOnline = snapshot.IsOnline;

        AccessHash = snapshot.AccessHash;
        FirstName = snapshot.FirstName;
        LastName = snapshot.LastName;
        PhoneNumber = snapshot.PhoneNumber;
        HasPassword = snapshot.HasPassword;
        UserName = snapshot.UserName;
        Photo = snapshot.Photo;
        IsBot = snapshot.IsBot;
        IsDeleted = snapshot.IsDeleted;

        EmojiStatusDocumentId = snapshot.EmojiStatusDocumentId;
        EmojiStatusValidUntil = snapshot.EmojiStatusValidUntil;
        EmojiStatusCollectibleId = snapshot.EmojiStatusCollectibleId;
        RecentEmojiStatus = snapshot.RecentEmojiStatuses.Take(MaxRecentEmojiStatuses).ToList();

        PhotoId = snapshot.PhotoId;
        FallbackPhotoId = snapshot.FallbackPhotoId;
        Color = snapshot.Color;
        ProfileColor = snapshot.ProfileColor;
        GlobalPrivacySettings = snapshot.GlobalPrivacySettings;
        Premium = snapshot.Premium;
        PersonalChannelId = snapshot.PersonalChannelId;
        Birthday = snapshot.Birthday;
        ProfilePhotoUpdateDate = snapshot.ProfilePhotoUpdateDate;
        UserNameUpdateDate = snapshot.UserNameUpdateDate;
        Verified = snapshot.Verified;
    }

    public void Apply(UserProfilePhotoUploadedEvent aggregateEvent)
    {
        PhotoId = aggregateEvent.PhotoId;
    }

    public void Apply(UserEmojiStatusUpdatedEvent aggregateEvent)
    {
        var emojiStatus = aggregateEvent.EmojiStatus;
        EmojiStatusDocumentId = emojiStatus?.DocumentId;
        EmojiStatusValidUntil = emojiStatus?.Until;
        EmojiStatusCollectibleId = emojiStatus?.CollectibleId;

        if (emojiStatus == null)
        {
            return;
        }

        // Most recent first, without duplicates: re-picking a status must move it up the list
        // rather than fill the list with copies of itself.
        RecentEmojiStatus.RemoveAll(p => p == emojiStatus.DocumentId);
        RecentEmojiStatus.Insert(0, emojiStatus.DocumentId);
        if (RecentEmojiStatus.Count > MaxRecentEmojiStatuses)
        {
            RecentEmojiStatus.RemoveRange(MaxRecentEmojiStatuses,
                RecentEmojiStatus.Count - MaxRecentEmojiStatuses);
        }
    }

    public void Apply(UserRecentEmojiStatusesClearedEvent aggregateEvent)
    {
        RecentEmojiStatus.Clear();
    }

    public void Apply(UserColorUpdatedEvent aggregateEvent)
    {
        if (aggregateEvent.ForProfile)
        {
            ProfileColor = aggregateEvent.Color;
        }
        else
        {
            Color = aggregateEvent.Color;
        }
    }
    public void Apply(UserGlobalPrivacySettingsChangedEvent aggregateEvent)
    {
        GlobalPrivacySettings= aggregateEvent.GlobalPrivacySettings;
    }
    public void Apply(UserAccountTtlUpdatedEvent aggregateEvent)
    {
        AccountTtl = aggregateEvent.AccountTtl;
    }
    public void Apply(UserContactSignUpNotificationUpdatedEvent aggregateEvent)
    {
        ShowContactSignUpNotification = aggregateEvent.ShowContactSignUpNotification;
    }
    public void Apply(UserPremiumStatusChangedEvent aggregateEvent)
    {
        Premium=aggregateEvent.Premium;
    }
    public void Apply(PersonalChannelUpdatedEvent aggregateEvent)
    {
        PersonalChannelId = aggregateEvent.PersonalChannelId;
    }
    public void Apply(BirthdayUpdatedEvent aggregateEvent)
    {
        Birthday = aggregateEvent.Birthday;
    }

    // Telegram Business settings live in the read model only; the aggregate decides nothing from them.
    public void Apply(BusinessWorkHoursUpdatedEvent aggregateEvent)
    {
    }

    public void Apply(BusinessLocationUpdatedEvent aggregateEvent)
    {
    }

    public void Apply(BusinessGreetingMessageUpdatedEvent aggregateEvent)
    {
    }

    public void Apply(BusinessAwayMessageUpdatedEvent aggregateEvent)
    {
    }

    public void Apply(BusinessIntroUpdatedEvent aggregateEvent)
    {
    }

    public void Apply(UserAboutUpdatedEvent aggregateEvent)
    {
        
    }

    public void Apply(UserFirstNameUpdatedEvent aggregateEvent)
    {
        FirstName= aggregateEvent.FirstName;
    }

    /// <summary>
    /// Wipes the profile of a deleted account. Everything a client could render for the user is
    /// cleared here, so nothing survives in the aggregate snapshot after
    /// <a href="https://corefork.telegram.org/api/account-deletion">account deletion</a>.
    /// </summary>
    public void Apply(UserDeletedEvent aggregateEvent)
    {
        IsDeleted = true;
        FirstName = string.Empty;
        LastName = null;
        UserName = null;
        UserNameUpdateDate = null;
        PhoneNumber = string.Empty;
        Photo = null;
        PhotoId = null;
        FallbackPhotoId = null;
        Birthday = null;
        EmojiStatusDocumentId = null;
        EmojiStatusValidUntil = null;
        EmojiStatusCollectibleId = null;
        RecentEmojiStatus.Clear();
        Color = null;
        ProfileColor = null;
        PersonalChannelId = null;
        HasPassword = false;
        Premium = false;
        IsOnline = false;
    }
}
