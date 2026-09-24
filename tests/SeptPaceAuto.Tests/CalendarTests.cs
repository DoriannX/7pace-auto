#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using SeptPaceAuto.Services;
using Xunit;

namespace SeptPaceAuto.Tests;

public sealed class CalendarTests
{
    private static readonly TimeZoneInfo Paris = TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time");

    [Fact]
    public void Ics_RecurrenceEtEtatsOccupes()
    {
        const string ics = """
BEGIN:VCALENDAR
VERSION:2.0
PRODID:-//test//calendar//FR
BEGIN:VEVENT
UID:standup
DTSTART:20260921T071500Z
DTEND:20260921T073000Z
RRULE:FREQ=DAILY;COUNT=5
X-MICROSOFT-CDO-BUSYSTATUS:BUSY
END:VEVENT
BEGIN:VEVENT
UID:meeting
DTSTART:20260923T140000Z
DTEND:20260923T150000Z
X-MICROSOFT-CDO-BUSYSTATUS:BUSY
END:VEVENT
BEGIN:VEVENT
UID:tentative
DTSTART:20260923T080000Z
DTEND:20260923T090000Z
X-MICROSOFT-CDO-BUSYSTATUS:TENTATIVE
END:VEVENT
BEGIN:VEVENT
UID:free
DTSTART:20260923T090000Z
DTEND:20260923T100000Z
TRANSP:TRANSPARENT
END:VEVENT
BEGIN:VEVENT
UID:cancelled
DTSTART:20260923T100000Z
DTEND:20260923T110000Z
STATUS:CANCELLED
END:VEVENT
BEGIN:VEVENT
UID:all-day
DTSTART;VALUE=DATE:20260923
DTEND;VALUE=DATE:20260924
X-MICROSOFT-CDO-BUSYSTATUS:BUSY
END:VEVENT
END:VCALENDAR
""";

        var slots = CalendarSlots.Parse(ics, new DateOnly(2026, 9, 23), Paris);

        Assert.Equal(new[] { new CalendarSlot(555, 570, 175), new CalendarSlot(960, 1020, 83) }, slots);
    }

    [Fact]
    public async Task ReunionInterromptGitPuisLaBrancheReprend()
    {
        var feed = new ScriptedCalendarFeed { Slots = new[] { new CalendarSlot(555, 570, 175) } };
        using var harness = new TrackerHarness(calendar: feed);

        await harness.PrimeAt(9, 0);
        for (var minute = 1; minute <= 14; minute++) await harness.TickAt(9, minute);
        var reads = harness.Branches.Reads;
        for (var minute = 15; minute <= 29; minute++) await harness.TickAt(9, minute);
        Assert.Equal(reads, harness.Branches.Reads);
        Assert.Equal("meeting", harness.Tracker.Current.State);
        Assert.Equal(175, harness.Tracker.Current.WorkItem);
        await harness.TickAt(9, 30);
        await harness.TickAt(9, 32);

        var entries = harness.Entries();
        Assert.True(entries.Count == 3, string.Join(" | ", entries.Select(e => $"{e.Start}-{e.End}:{e.Source}")));
        Assert.Equal(("09:00", "09:15", "git"), (entries[0].Start, entries[0].End, entries[0].Source));
        Assert.Equal(("09:15", "09:30", "calendar"), (entries[1].Start, entries[1].End, entries[1].Source));
        Assert.Equal(("09:30", "09:32", "git"), (entries[2].Start, entries[2].End, entries[2].Source));
        Assert.Equal(175, entries[1].WorkItem);
        Assert.Empty(DayStore.Overlaps(entries));
    }

    [Fact]
    public async Task PublicationTardiveRemplaceLesMinutesGitDejaEcrites()
    {
        var feed = new ScriptedCalendarFeed();
        using var harness = new TrackerHarness(calendar: feed);

        await harness.PrimeAt(9, 0);
        for (var minute = 1; minute <= 22; minute++) await harness.TickAt(9, minute);
        feed.Slots = new[] { new CalendarSlot(555, 560, 83) };
        await harness.TickAt(9, 23);
        await harness.TickAt(9, 24);

        var entries = harness.Entries();
        Assert.True(entries.Count == 3, string.Join(" | ", entries.Select(e => $"{e.Start}-{e.End}:{e.Source}")));
        Assert.Equal(("09:00", "09:15", "git"), (entries[0].Start, entries[0].End, entries[0].Source));
        Assert.Equal(("09:15", "09:20", "calendar"), (entries[1].Start, entries[1].End, entries[1].Source));
        Assert.Equal(("09:20", "09:24", "git"), (entries[2].Start, entries[2].End, entries[2].Source));
        Assert.Equal(83, entries[1].WorkItem);
        Assert.Empty(DayStore.Overlaps(entries));
    }

    [Fact]
    public void LienOutlookValideResteSurLeDomaineAttendu()
    {
        Assert.True(CalendarLinkStore.ValidUrl("https://outlook.office365.com/owa/calendar/key/calendar.ics"));
        Assert.False(CalendarLinkStore.ValidUrl("https://example.com/calendar.ics"));
        Assert.False(CalendarLinkStore.ValidUrl("http://outlook.office365.com/owa/calendar/key/calendar.ics"));
    }
}
