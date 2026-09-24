#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;

namespace SeptPaceAuto.Services;

/// <summary>Un créneau occupé du calendrier, en minutes locales de la journée.</summary>
internal sealed record CalendarSlot(int Start, int End, int WorkItem);
internal sealed record CalendarSnapshot(bool Valid, IReadOnlyList<CalendarSlot> Slots, string? Error = null)
{
    public static CalendarSnapshot Unavailable { get; } = new(false, Array.Empty<CalendarSlot>());
    public static CalendarSnapshot Failed { get; } = new(false, Array.Empty<CalendarSlot>(), "Calendrier Outlook indisponible : les réunions ne sont plus déduites.");
}

internal interface ICalendarFeed
{
    Task<CalendarSnapshot> ReadDayAsync(DateTime now, CancellationToken ct);
    void Invalidate();
}

internal sealed class EmptyCalendarFeed : ICalendarFeed
{
    public Task<CalendarSnapshot> ReadDayAsync(DateTime now, CancellationToken ct) =>
        Task.FromResult(CalendarSnapshot.Unavailable);

    public void Invalidate() { }
}

/// <summary>Lit le lien ICS Outlook sans exposer son adresse à l'interface ou aux journaux.</summary>
internal sealed class OutlookCalendarFeed : ICalendarFeed, IDisposable
{
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan KeepOnFailure = TimeSpan.FromMinutes(30);
    private readonly HttpClient _http;
    private readonly Func<string?> _url;
    private readonly TimeZoneInfo _zone;
    private string? _cachedUrl;
    private string? _cachedIcs;
    private DateTime _fetchedAt;
    private DateTime _attemptedAt;

    public OutlookCalendarFeed(Func<string?> url)
    {
        _url = url;
        _zone = TimeZoneInfo.Local;
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(8) };
    }

    public void Invalidate()
    {
        _cachedUrl = null;
        _cachedIcs = null;
        _fetchedAt = default;
        _attemptedAt = default;
    }

    public async Task<CalendarSnapshot> ReadDayAsync(DateTime now, CancellationToken ct)
    {
        var url = _url();
        if (string.IsNullOrWhiteSpace(url)) return CalendarSnapshot.Unavailable;
        if (!string.Equals(url, _cachedUrl, StringComparison.Ordinal))
        {
            Invalidate();
            _cachedUrl = url;
        }

        if (_attemptedAt == default || now - _attemptedAt >= RefreshAfter)
        {
            _attemptedAt = now;
            try
            {
                _cachedIcs = await DownloadAsync(_http, url, ct).ConfigureAwait(false);
                _fetchedAt = now;
            }
            catch (Exception error) when (!ct.IsCancellationRequested && error is HttpRequestException or TaskCanceledException or DomainException)
            {
                if (_cachedIcs is null || now - _fetchedAt > KeepOnFailure) return CalendarSnapshot.Failed;
            }
        }

        try
        {
            return new CalendarSnapshot(true, CalendarSlots.Parse(_cachedIcs!, DateOnly.FromDateTime(now), _zone));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return CalendarSnapshot.Failed;
        }
    }

    internal static async Task<string> DownloadAsync(HttpClient http, string url, CancellationToken ct)
    {
        if (!CalendarLinkStore.ValidUrl(url)) throw new DomainException("Le lien ICS Outlook est invalide.");
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw new DomainException("Le lien ICS Outlook ne répond pas.");
        if (response.Content.Headers.ContentLength is > 2_000_000) throw new DomainException("Le calendrier ICS est trop volumineux.");
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (text.Length > 2_000_000 || !text.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
            throw new DomainException("Le lien ne renvoie pas de calendrier ICS.");
        return text;
    }

    public void Dispose() => _http.Dispose();
}

internal static class CalendarSlots
{
    /// <summary>
    /// Le flux « occupé » ne porte pas la réponse de l'invité. On ne retient que BUSY, en
    /// excluant les éléments libres, provisoires, annulés et de journée entière.
    /// </summary>
    public static IReadOnlyList<CalendarSlot> Parse(string ics, DateOnly day, TimeZoneInfo zone)
    {
        var calendar = Calendar.Load(ics) ?? throw new FormatException("Calendrier ICS vide.");
        // Départ volontairement élargi : une réunion qui franchit minuit doit rester visible.
        var from = new CalDateTime(day.AddDays(-2).ToDateTime(TimeOnly.MinValue));
        var until = day.AddDays(2).ToDateTime(TimeOnly.MinValue);
        var busy = new List<CalendarSlot>();
        foreach (var occurrence in calendar.GetOccurrences<CalendarEvent>(from)
                     .TakeWhile(item => item.Period.StartTime.Value < until))
        {
            if (occurrence.Source is not CalendarEvent item || item.IsAllDay) continue;
            if (string.Equals(item.Properties.Get<string>("STATUS"), "CANCELLED", StringComparison.OrdinalIgnoreCase)) continue;
            var status = item.Properties.Get<string>("X-MICROSOFT-CDO-BUSYSTATUS");
            if (status is not null && !string.Equals(status, "BUSY", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(item.Properties.Get<string>("TRANSP"), "TRANSPARENT", StringComparison.OrdinalIgnoreCase)) continue;

            if (occurrence.Period.EffectiveEndTime is not CalDateTime eventEnd) continue;
            var localStart = Local(occurrence.Period.StartTime, zone);
            var localEnd = Local(eventEnd, zone);
            var startMinute = Math.Max(0, (int)(localStart - day.ToDateTime(TimeOnly.MinValue)).TotalMinutes);
            var endMinute = Math.Min(24 * 60, (int)(localEnd - day.ToDateTime(TimeOnly.MinValue)).TotalMinutes);
            if (endMinute <= startMinute) continue;
            var standup = localStart.Hour == 9 && localStart.Minute == 15
                && localEnd.Hour == 9 && localEnd.Minute == 30
                && localStart.Date == localEnd.Date;
            busy.Add(new CalendarSlot(startMinute, endMinute, standup ? 175 : 83));
        }
        return Normalize(busy);
    }

    private static DateTime Local(CalDateTime value, TimeZoneInfo zone) =>
        value.IsFloating ? value.Value : TimeZoneInfo.ConvertTimeFromUtc(value.AsUtc, zone);

    /// <summary>Fusionne les doublons et donne priorité au standup dans les chevauchements.</summary>
    private static IReadOnlyList<CalendarSlot> Normalize(List<CalendarSlot> slots)
    {
        if (slots.Count == 0) return slots;
        var boundaries = slots.SelectMany(slot => new[] { slot.Start, slot.End }).Distinct().OrderBy(value => value).ToArray();
        var result = new List<CalendarSlot>();
        for (var index = 0; index < boundaries.Length - 1; index++)
        {
            var from = boundaries[index];
            var to = boundaries[index + 1];
            var covering = slots.Where(slot => slot.Start < to && slot.End > from).ToArray();
            if (covering.Length == 0) continue;
            var item = covering.Any(slot => slot.WorkItem == 175) ? 175 : 83;
            if (result.Count > 0 && result[^1].End == from && result[^1].WorkItem == item)
                result[^1] = result[^1] with { End = to };
            else
                result.Add(new CalendarSlot(from, to, item));
        }
        return result;
    }
}
