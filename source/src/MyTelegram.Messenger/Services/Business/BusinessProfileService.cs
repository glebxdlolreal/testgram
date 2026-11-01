using MyTelegram.Messenger.Services.Documents;
using TBusinessAwayMessage = MyTelegram.Schema.TBusinessAwayMessage;
using TBusinessGreetingMessage = MyTelegram.Schema.TBusinessGreetingMessage;
using TBusinessIntro = MyTelegram.Schema.TBusinessIntro;
using TBusinessLocation = MyTelegram.Schema.TBusinessLocation;
using TBusinessRecipients = MyTelegram.Schema.TBusinessRecipients;
using TBusinessWorkHours = MyTelegram.Schema.TBusinessWorkHours;

namespace MyTelegram.Messenger.Services.Business;

/// <summary>
/// Telegram Business profile settings (<a href="https://corefork.telegram.org/api/business">api/business</a>):
/// turning stored settings into the <c>userFull</c> fields and deciding whether the business is open.
/// </summary>
public interface IBusinessProfileService
{
    /// <summary>
    /// Fills the business fields of <paramref name="userFull"/>. Greeting and away settings name the owner's
    /// quick reply shortcuts and chosen recipients, so only the owner sees them.
    /// </summary>
    Task FillUserFullAsync(IUserReadModel user, IUserFull userFull, bool isSelf);

    /// <summary>Whether the business is open at <paramref name="utcNow"/> according to its opening hours.</summary>
    bool IsOpen(BusinessWorkHoursItem workHours, DateTime utcNow);

    /// <summary>
    /// After a profile change: drops this process' cached copy and sends <c>updateUser</c> to the caller's
    /// other sessions so they refetch <c>userFull</c>.
    /// </summary>
    Task NotifyUserUpdatedAsync(IRequestInput input);
}

public sealed class BusinessProfileService(
    ITimezoneHelper timezoneHelper,
    IDocumentReader documentReader,
    IUserAppService userAppService,
    IObjectMessageSender objectMessageSender)
    : IBusinessProfileService, ITransientDependency
{
    public Task NotifyUserUpdatedAsync(IRequestInput input)
    {
        userAppService.InvalidateCache(input.UserId);
        var updates = new TUpdates
        {
            Updates = new TVector<IUpdate>(new TUpdateUser { UserId = input.UserId }),
            Users = [],
            Chats = [],
            Date = DateTime.UtcNow.ToTimestamp(),
            Seq = 0
        };

        return objectMessageSender.PushMessageToPeerAsync(input.UserId.ToUserPeer(), updates,
            excludeAuthKeyId: input.PermAuthKeyId);
    }

    public const int MinutesInWeek = 7 * 24 * 60;

    public async Task FillUserFullAsync(IUserReadModel user, IUserFull userFull, bool isSelf)
    {
        if (userFull is not TUserFull full)
        {
            return;
        }

        full.BusinessWorkHours = user.BusinessWorkHoursSettings == null
            ? null
            : ToWorkHours(user.BusinessWorkHoursSettings, DateTime.UtcNow);
        full.BusinessLocation = user.BusinessLocationSettings == null
            ? null
            : ToLocation(user.BusinessLocationSettings);
        full.BusinessGreetingMessage = isSelf && user.BusinessGreetingSettings != null
            ? ToGreeting(user.BusinessGreetingSettings)
            : null;
        full.BusinessAwayMessage = isSelf && user.BusinessAwaySettings != null
            ? ToAway(user.BusinessAwaySettings)
            : null;
        full.BusinessIntro = user.BusinessIntroSettings == null
            ? null
            : await ToIntroAsync(user.BusinessIntroSettings);
    }

    public TBusinessWorkHours ToWorkHours(BusinessWorkHoursItem item, DateTime utcNow)
    {
        return new TBusinessWorkHours
        {
            OpenNow = IsOpen(item, utcNow),
            TimezoneId = item.TimezoneId,
            WeeklyOpen = new TVector<IBusinessWeeklyOpen>(item.WeeklyOpen.Select(p =>
                (IBusinessWeeklyOpen)new TBusinessWeeklyOpen { StartMinute = p.StartMinute, EndMinute = p.EndMinute }))
        };
    }

    public static TBusinessLocation ToLocation(BusinessLocationItem item)
    {
        return new TBusinessLocation
        {
            Address = item.Address,
            GeoPoint = item is { Latitude: not null, Longitude: not null }
                ? new TGeoPoint
                {
                    Lat = item.Latitude.Value,
                    Long = item.Longitude.Value,
                    AccessHash = 0,
                    AccuracyRadius = item.AccuracyRadius
                }
                : null
        };
    }

    public static TBusinessRecipients ToRecipients(BusinessRecipientsItem item)
    {
        return new TBusinessRecipients
        {
            ExistingChats = item.ExistingChats,
            NewChats = item.NewChats,
            Contacts = item.Contacts,
            NonContacts = item.NonContacts,
            ExcludeSelected = item.ExcludeSelected,
            Users = item.UserIds.Count > 0 ? new TVector<long>(item.UserIds) : null
        };
    }

    public static TBusinessGreetingMessage ToGreeting(BusinessGreetingItem item)
    {
        return new TBusinessGreetingMessage
        {
            ShortcutId = item.ShortcutId,
            Recipients = ToRecipients(item.Recipients),
            NoActivityDays = item.NoActivityDays
        };
    }

    public static TBusinessAwayMessage ToAway(BusinessAwayItem item)
    {
        return new TBusinessAwayMessage
        {
            OfflineOnly = item.OfflineOnly,
            ShortcutId = item.ShortcutId,
            Recipients = ToRecipients(item.Recipients),
            Schedule = item.Schedule switch
            {
                BusinessAwayScheduleKind.OutsideWorkHours => new TBusinessAwayMessageScheduleOutsideWorkHours(),
                BusinessAwayScheduleKind.Custom => new TBusinessAwayMessageScheduleCustom
                {
                    StartDate = item.StartDate ?? 0,
                    EndDate = item.EndDate ?? 0
                },
                _ => new TBusinessAwayMessageScheduleAlways()
            }
        };
    }

    private async Task<TBusinessIntro> ToIntroAsync(BusinessIntroItem item)
    {
        IDocument? sticker = null;
        if (item.StickerDocumentId.HasValue)
        {
            // The real stored document: clients download and render it in the empty chat.
            var document = await documentReader.GetAsync(item.StickerDocumentId.Value);
            if (document != null)
            {
                sticker = documentReader.Map(document);
            }
        }

        return new TBusinessIntro
        {
            Title = item.Title,
            Description = item.Description,
            Sticker = sticker
        };
    }

    public bool IsOpen(BusinessWorkHoursItem workHours, DateTime utcNow)
    {
        var local = utcNow + GetUtcOffset(workHours.TimezoneId, utcNow);
        var weekMinute = ((int)local.DayOfWeek + 6) % 7 * 24 * 60 + local.Hour * 60 + local.Minute;

        return IsOpenAt(workHours.WeeklyOpen, weekMinute);
    }

    /// <summary>
    /// <paramref name="weekMinute"/> counts from Monday 00:00. An interval may run past the end of the week
    /// (up to <c>8*24*60</c>), covering the start of the next one.
    /// </summary>
    public static bool IsOpenAt(IEnumerable<BusinessWeeklyOpenItem> intervals, int weekMinute)
    {
        return intervals.Any(p =>
            (p.StartMinute <= weekMinute && weekMinute < p.EndMinute) ||
            (p.StartMinute <= weekMinute + MinutesInWeek && weekMinute + MinutesInWeek < p.EndMinute));
    }

    private TimeSpan GetUtcOffset(string timezoneId, DateTime utcNow)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezoneId).GetUtcOffset(utcNow);
        }
        catch (Exception)
        {
            // No tz database in the container: fall back to the fixed offset help.getTimezonesList advertises.
            return TimeSpan.FromSeconds(timezoneHelper.GetTimeZoneItem(timezoneId)?.UtcOffset ?? 0);
        }
    }

    /// <summary>
    /// The interval normalization tdlib applies before uploading opening hours
    /// (<c>BusinessWorkHours::sanitize_work_hours</c>): drops empty and out-of-range intervals, sorts,
    /// merges intersecting ones, caps them at one week and folds the end of the week onto its start.
    /// Applying it on the server as well makes the stored form canonical whatever the client sent.
    /// </summary>
    public static List<BusinessWeeklyOpenItem> Normalize(IEnumerable<BusinessWeeklyOpenItem> intervals)
    {
        var list = intervals
            .Where(p => p.StartMinute < p.EndMinute && p.StartMinute >= 0 && p.EndMinute <= 8 * 24 * 60)
            .Select(p => (Start: p.StartMinute, End: p.EndMinute))
            .ToList();
        Combine(list);
        return list.Select(p => new BusinessWeeklyOpenItem(p.Start, p.End)).ToList();
    }

    private static void Combine(List<(int Start, int End)> list)
    {
        if (list.Count == 0)
        {
            return;
        }

        list.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(int Start, int End)> { list[0] };
        for (var i = 1; i < list.Count; i++)
        {
            var last = merged[^1];
            if (list[i].Start <= last.End)
            {
                merged[^1] = (last.Start, Math.Max(last.End, list[i].End));
            }
            else
            {
                merged.Add(list[i]);
            }
        }

        for (var i = 0; i < merged.Count; i++)
        {
            merged[i] = (merged[i].Start, Math.Min(merged[i].End, merged[i].Start + MinutesInWeek));
        }

        if (merged[0].Start != 0 && merged[0].Start + MinutesInWeek == merged[^1].End)
        {
            if (merged[^1].Start >= MinutesInWeek)
            {
                merged[0] = (merged[^1].Start - MinutesInWeek, merged[0].End);
                merged.RemoveAt(merged.Count - 1);
            }
            else
            {
                merged[0] = (0, merged[0].End);
                merged[^1] = (merged[^1].Start, MinutesInWeek);
            }
        }

        var maxMinute = merged[0].Start + MinutesInWeek;
        if (merged[^1].End > maxMinute || merged[^1].Start >= MinutesInWeek)
        {
            var size = merged.Count;
            for (var i = 0; i < size; i++)
            {
                if (merged[i].Start >= MinutesInWeek)
                {
                    merged[i] = (merged[i].Start - MinutesInWeek, merged[i].End - MinutesInWeek);
                }
                else if (merged[i].End > maxMinute)
                {
                    merged.Add((maxMinute - MinutesInWeek, merged[i].End - MinutesInWeek));
                    merged[i] = (merged[i].Start, maxMinute);
                }
            }

            Combine(merged);
        }

        list.Clear();
        list.AddRange(merged);
    }
}
