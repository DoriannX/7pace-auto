#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace SeptPaceAuto.Services;

internal sealed class MicrosoftRegistration
{
    [JsonPropertyName("clientId")] public string? ClientId { get; set; }
    [JsonPropertyName("tenantId")] public string? TenantId { get; set; }
    [JsonPropertyName("authorizationStatus")] public string? AuthorizationStatus { get; set; }
}

internal sealed class MicrosoftToken
{
    [JsonPropertyName("accessToken")] public string? AccessToken { get; set; }
    [JsonPropertyName("expiresOn")] public string? ExpiresOn { get; set; }
}

/// <summary>Réunion lue dans l'agenda ; aucune donnée n'est fabriquée localement.</summary>
public sealed record CalendarEvent(DateTime Start, DateTime End, string Subject);

/// <summary>
/// Agenda Outlook. L'application Microsoft dédiée existe mais l'approbation administrateur
/// est en attente : tant que ce n'est pas accordé, statut « pending », aucun évènement et
/// surtout aucun appel réseau. Le jour où l'approbation arrive, seule l'obtention du jeton
/// reste à brancher : l'appel Graph est déjà écrit ci-dessous.
/// </summary>
public sealed class OutlookCalendar
{
    private static readonly HashSet<string> Granted = new(StringComparer.OrdinalIgnoreCase)
    {
        "granted", "approved", "consented", "admin_approval_granted", "admin_consent_granted",
    };

    private readonly HttpClient _http;

    public OutlookCalendar(HttpClient http) => _http = http;

    /// <summary>État affiché dans le tiroir : jamais « connecté » tant que l'accès n'est pas réel.</summary>
    public ConnectionState State()
    {
        var registration = AppPaths.ReadJson<MicrosoftRegistration>(AppPaths.Microsoft);
        if (registration is null) return new ConnectionState("off", "Outlook · non configuré");
        if (!Granted.Contains(registration.AuthorizationStatus ?? string.Empty))
        {
            return new ConnectionState("pending", "Outlook · autorisation en attente");
        }
        return Token() is null
            ? new ConnectionState("off", "Outlook · reconnexion nécessaire")
            : new ConnectionState("connected", "Outlook · agenda connecté");
    }

    /// <summary>Réunions du jour, vide tant que l'intégration n'est pas réellement accordée.</summary>
    public async Task<IReadOnlyList<CalendarEvent>> EventsAsync(DateTime date, CancellationToken ct)
    {
        var state = State();
        if (!string.Equals(state.Status, "connected", StringComparison.Ordinal)) return Array.Empty<CalendarEvent>();

        var token = Token();
        if (token is null) return Array.Empty<CalendarEvent>();

        var from = date.Date.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        var to = date.Date.AddDays(1).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        var url = "https://graph.microsoft.com/v1.0/me/calendarView"
            + $"?startDateTime={from}&endDateTime={to}"
            + "&$select=subject,start,end,isAllDay,showAs&$orderby=start/dateTime&$top=100";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("Prefer", "outlook.timezone=\"Romance Standard Time\"");

        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return Array.Empty<CalendarEvent>();
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Parse(body);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
        {
            return Array.Empty<CalendarEvent>();
        }
    }

    private static IReadOnlyList<CalendarEvent> Parse(string body)
    {
        var events = new List<CalendarEvent>();
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array) return events;

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (item.TryGetProperty("isAllDay", out var allDay) && allDay.ValueKind == JsonValueKind.True) continue;
            if (item.TryGetProperty("showAs", out var showAs)
                && showAs.ValueKind == JsonValueKind.String
                && string.Equals(showAs.GetString(), "free", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var start = Moment(item, "start");
            var end = Moment(item, "end");
            if (start is null || end is null || end <= start) continue;

            var subject = item.TryGetProperty("subject", out var title) && title.ValueKind == JsonValueKind.String
                ? title.GetString() ?? "Réunion"
                : "Réunion";
            events.Add(new CalendarEvent(start.Value, end.Value, subject));
        }
        return events;
    }

    private static DateTime? Moment(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.Object) return null;
        if (!node.TryGetProperty("dateTime", out var text) || text.ValueKind != JsonValueKind.String) return null;
        return DateTime.TryParse(text.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var moment) ? moment : null;
    }

    /// <summary>
    /// Jeton déposé par la configuration initiale, hors de cette application.
    /// Aucun jeton exploitable : l'agenda reste déconnecté, jamais simulé.
    /// </summary>
    private static string? Token()
    {
        var stored = AppPaths.ReadJson<MicrosoftToken>(AppPaths.MicrosoftToken);
        if (stored is null || string.IsNullOrWhiteSpace(stored.AccessToken)) return null;
        if (DateTimeOffset.TryParse(stored.ExpiresOn, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiry)
            && expiry <= DateTimeOffset.Now.AddMinutes(1))
        {
            return null;
        }
        return stored.AccessToken;
    }
}
