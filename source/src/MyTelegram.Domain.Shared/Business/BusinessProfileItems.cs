namespace MyTelegram;

// Telegram Business profile settings as stored by UserAggregate events. Plain records rather than TL
// types: TL objects carry flag words that the Mongo class maps drop, and copying a request's flags
// into them produced constructors that could not be serialized.
// See https://corefork.telegram.org/api/business

/// <summary>One opening interval, in minutes since Monday 00:00 of the business' time zone.</summary>
public record BusinessWeeklyOpenItem(int StartMinute, int EndMinute);

/// <summary><a href="https://corefork.telegram.org/api/business#opening-hours">Opening hours</a>.</summary>
public record BusinessWorkHoursItem(string TimezoneId, List<BusinessWeeklyOpenItem> WeeklyOpen);

/// <summary><a href="https://corefork.telegram.org/api/business#location">Location</a>; the point is optional.</summary>
public record BusinessLocationItem(string Address, double? Latitude, double? Longitude, int? AccuracyRadius);

/// <summary>
/// Who an automatic message or a connected bot applies to. With <see cref="ExcludeSelected"/> the
/// selection is inverted.
/// </summary>
public record BusinessRecipientsItem(
    bool ExistingChats,
    bool NewChats,
    bool Contacts,
    bool NonContacts,
    bool ExcludeSelected,
    List<long> UserIds);

/// <summary>
/// <a href="https://corefork.telegram.org/api/business#greeting-messages">Greeting message</a>.
/// <see cref="UpdatedAt"/> separates "existing" from "new" chats.
/// </summary>
public record BusinessGreetingItem(int ShortcutId, BusinessRecipientsItem Recipients, int NoActivityDays,
    int UpdatedAt);

public enum BusinessAwayScheduleKind
{
    Always = 0,
    OutsideWorkHours = 1,
    Custom = 2
}

/// <summary><a href="https://corefork.telegram.org/api/business#away-messages">Away message</a>.</summary>
public record BusinessAwayItem(
    int ShortcutId,
    BusinessRecipientsItem Recipients,
    BusinessAwayScheduleKind Schedule,
    int? StartDate,
    int? EndDate,
    bool OfflineOnly,
    int UpdatedAt);

/// <summary><a href="https://corefork.telegram.org/api/business#business-introduction">Business introduction</a>.</summary>
public record BusinessIntroItem(string Title, string Description, long? StickerDocumentId);
