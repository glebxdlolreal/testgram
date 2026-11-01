using MyTelegram.Messenger.Services.Business;

namespace MyTelegram.Messenger.Tests.Business;

/// <summary>
/// Feature: <a href="https://corefork.telegram.org/api/business#opening-hours">opening hours</a> and the
/// recipient selection of automatic messages.
///
/// <para>
/// The normalization is tdlib's <c>BusinessWorkHours::sanitize_work_hours</c>: clients upload intervals in
/// that form, so the server keeping the same canonical form means a re-upload of what it returned is a
/// no-op. Week minutes start on Monday 00:00 and an interval may run past the end of the week.
/// </para>
/// </summary>
public class BusinessProfileTests
{
    private const int Day = 24 * 60;
    private const int Week = 7 * Day;

    private static List<(int, int)> Normalize(params (int Start, int End)[] intervals) =>
        BusinessProfileService.Normalize(intervals.Select(p => new BusinessWeeklyOpenItem(p.Start, p.End)))
            .Select(p => (p.StartMinute, p.EndMinute))
            .ToList();

    [Fact]
    public void Drops_empty_and_out_of_range_intervals_and_merges_overlaps()
    {
        Normalize((600, 540), (-5, 10), (9 * 60, 13 * 60), (12 * 60, 18 * 60), (0, 9 * Day))
            .ShouldBe([(9 * 60, 18 * 60)]);
    }

    [Fact]
    public void Touching_intervals_are_merged()
    {
        Normalize((0, 60), (60, 120)).ShouldBe([(0, 120)]);
    }

    [Fact]
    public void An_interval_running_into_next_week_that_overlaps_monday_is_split_at_the_week_boundary()
    {
        // Sunday 22:00 to Monday 02:00, plus Monday 01:00-05:00: tdlib ends up with Monday 00:00-05:00 and
        // Sunday 22:00-24:00.
        Normalize((6 * Day + 22 * 60, Week + 2 * 60), (60, 5 * 60))
            .ShouldBe([(0, 5 * 60), (6 * Day + 22 * 60, Week)]);
    }

    [Fact]
    public void Always_open_is_one_week_long()
    {
        Normalize((0, Week)).ShouldBe([(0, Week)]);
    }

    [Fact]
    public void Is_open_inside_an_interval_and_inside_the_wrapped_part_of_a_late_sunday_one()
    {
        BusinessWeeklyOpenItem[] hours = [new(9 * 60, 18 * 60), new(6 * Day + 22 * 60, Week + 2 * 60)];

        BusinessProfileService.IsOpenAt(hours, 10 * 60).ShouldBeTrue();
        BusinessProfileService.IsOpenAt(hours, 18 * 60).ShouldBeFalse();
        BusinessProfileService.IsOpenAt(hours, 60).ShouldBeTrue(); // Monday 01:00
        BusinessProfileService.IsOpenAt(hours, 3 * 60).ShouldBeFalse();
        BusinessProfileService.IsOpenAt(hours, 6 * Day + 23 * 60).ShouldBeTrue();
    }

    [Fact]
    public void Week_minutes_start_on_monday_in_the_business_time_zone()
    {
        var service = new BusinessProfileService(new MyTelegram.Messenger.Services.Impl.TimezoneHelper(), null!,
            null!, null!);
        // Monday 09:30 in UTC.
        var mondayMorning = new DateTime(2026, 10, 26, 9, 30, 0, DateTimeKind.Utc);
        var hours = new BusinessWorkHoursItem("UTC", [new BusinessWeeklyOpenItem(9 * 60, 10 * 60)]);

        service.IsOpen(hours, mondayMorning).ShouldBeTrue();
        service.IsOpen(hours, mondayMorning.AddDays(1)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false, false, false, false, false, 0, true)]
    [InlineData(false, false, false, false, false, 1, false)]
    [InlineData(true, false, false, false, false, 0, false)]
    // "All private chats except the selected": nothing selected means everybody.
    [InlineData(false, false, false, false, true, 0, false)]
    public void Only_an_inclusive_selection_can_be_empty(bool existing, bool @new, bool contacts, bool nonContacts,
        bool exclude, int users, bool empty)
    {
        var item = new BusinessRecipientsItem(existing, @new, contacts, nonContacts, exclude,
            Enumerable.Range(1, users).Select(p => (long)p).ToList());

        BusinessRecipientsReader.IsEmpty(item).ShouldBe(empty);
    }
}
