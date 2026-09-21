#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SeptPaceAuto.Services;

/// <summary>
/// Erreur destinée à l'utilisateur : le message est la phrase française que l'interface affiche.
/// </summary>
public sealed class DomainException : Exception
{
    public DomainException(string message) : base(message) { }
}

/// <summary>
/// Un créneau de la journée. Seuls le début, la fin et l'élément de travail partent dans
/// 7pace ; le libellé n'existe que pour relire la journée avant de l'envoyer.
/// </summary>
public sealed class Entry
{
    [JsonPropertyName("id")] public int? Id { get; set; }
    [JsonPropertyName("start")] public string Start { get; set; } = "08:30";
    [JsonPropertyName("end")] public string End { get; set; } = "08:30";

    /// <summary>Élément de travail imputé. Nul : créneau à attribuer, qui bloque l'envoi.</summary>
    [JsonPropertyName("workItem")] public int? WorkItem { get; set; }

    /// <summary>Texte d'aide à la relecture, jamais envoyé à 7pace ni demandé à l'utilisateur.</summary>
    [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;

    /// <summary>Origine : <c>git</c>, <c>gap</c>, <c>quick</c> ou <c>manual</c>.</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = "manual";

    /// <summary>
    /// Horodatage d'un envoi accepté. Un créneau envoyé est verrouillé : il ne repart jamais,
    /// même si un autre créneau de la même journée a été refusé.
    /// </summary>
    [JsonPropertyName("sentAt")] public string? SentAt { get; set; }

    /// <summary>
    /// Bug ou PBI lu dans la branche. Conservé après attribution : c'est lui qui permet de
    /// redemander à Azure le Fix enfant créé après le relevé.
    /// </summary>
    [JsonPropertyName("bug")] public int? Bug { get; set; }

    [JsonIgnore] public int StartMinutes => TimeRules.Minutes(Start);
    [JsonIgnore] public int EndMinutes => TimeRules.Minutes(End);

    /// <summary>Un créneau non attribué ne part jamais dans 7pace.</summary>
    [JsonIgnore] public bool Unassigned => WorkItem is null || WorkItem < 1;

    public Entry Clone() => (Entry)MemberwiseClone();
}

/// <summary>État du suivi de la journée en cours, affiché de façon compacte.</summary>
public sealed record Tracking(
    [property: JsonPropertyName("branch")] string? Branch,
    [property: JsonPropertyName("bug")] int? Bug,
    [property: JsonPropertyName("workItem")] int? WorkItem,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("quickRunning")] bool QuickRunning,
    [property: JsonPropertyName("state")] string State);

/// <summary>
/// Fraîcheur du suivi : de quoi distinguer une collecte vivante d'une collecte figée sans
/// rien déclencher. Les horodatages partent bruts, au format aller-retour ; c'est
/// l'interface qui décide de ce qu'elle en dit.
/// </summary>
public sealed record TrackingHealth(
    /// <summary>Dernier relevé mené à son terme, réussi ou non concluant.</summary>
    [property: JsonPropertyName("observedAt")] string? ObservedAt,

    /// <summary>Dernière lecture de branche réussie : au-delà, le dépôt ne répond plus.</summary>
    [property: JsonPropertyName("branchAt")] string? BranchAt,

    /// <summary>Dernière écriture ou prolongation d'un créneau de collecte.</summary>
    [property: JsonPropertyName("writtenAt")] string? WrittenAt,

    [property: JsonPropertyName("spanDate")] string? SpanDate,
    [property: JsonPropertyName("spanStart")] int? SpanStart,
    [property: JsonPropertyName("spanEnd")] int? SpanEnd,

    /// <summary>Dernier relevé en échec, déjà rédigé pour l'utilisateur.</summary>
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("errorAt")] string? ErrorAt,

    /// <summary>Faux quand git est absent du poste : aucun relevé n'est alors possible.</summary>
    [property: JsonPropertyName("gitAvailable")] bool GitAvailable,

    /// <summary>Au-delà de ce délai sans relevé, la collecte doit être annoncée figée.</summary>
    [property: JsonPropertyName("staleAfterSeconds")] int StaleAfterSeconds);

/// <summary>État d'une intégration, affiché tel quel.</summary>
public sealed record ConnectionState(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("label")] string Label);

/// <summary>
/// Horaires pendant lesquels la branche active est relevée, en minutes depuis minuit. La
/// pause déjeuner n'est pas un réglage : elle est l'intervalle laissé entre deux créneaux.
/// </summary>
public sealed class Schedule
{
    /// <summary>Journée de bureau courante en France, utilisée tant que rien n'est réglé.</summary>
    public static readonly Schedule Default = new(new[] { (510, 750), (810, 1020) });

    private readonly (int Start, int End)[] _windows;

    public Schedule(IReadOnlyList<(int Start, int End)> windows)
    {
        _windows = new (int, int)[windows.Count];
        for (var index = 0; index < windows.Count; index++) _windows[index] = windows[index];
    }

    public IReadOnlyList<(int Start, int End)> Windows => _windows;

    /// <summary>Créneau contenant cette minute, sinon null (pause, soirée, week-end exclus ailleurs).</summary>
    public (int Start, int End)? WindowAt(int minute)
    {
        foreach (var window in _windows)
        {
            if (minute >= window.Start && minute < window.End) return window;
        }
        return null;
    }

    /// <summary>Minutes prévues au travail sur la journée.</summary>
    public int PlannedMinutes()
    {
        var total = 0;
        foreach (var window in _windows) total += window.End - window.Start;
        return total;
    }
}

/// <summary>Lecture et écriture des horaires, des dates et des durées. Sans état.</summary>
public static class TimeRules
{
    public static int Minutes(string value)
    {
        var parts = (value ?? string.Empty).Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hour)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minute)
            || hour is < 0 or > 23 || minute is < 0 or > 59)
        {
            throw new DomainException("Indique des horaires au format HH:MM.");
        }
        return hour * 60 + minute;
    }

    public static string AsTime(int minutes)
    {
        var clamped = Math.Clamp(minutes, 0, 24 * 60 - 1);
        return string.Concat((clamped / 60).ToString("00", CultureInfo.InvariantCulture), ":", (clamped % 60).ToString("00", CultureInfo.InvariantCulture));
    }

    public static int MinuteOfDay(DateTime moment) => moment.Hour * 60 + moment.Minute;

    /// <summary>Heure en toutes lettres : « 8 h 30 », « 17 h ».</summary>
    public static string Clock(int minutes)
    {
        var clamped = Math.Clamp(minutes, 0, 24 * 60);
        var hours = (clamped / 60).ToString(CultureInfo.InvariantCulture);
        var rest = clamped % 60;
        return rest == 0 ? string.Concat(hours, " h") : string.Concat(hours, " h ", rest.ToString("00", CultureInfo.InvariantCulture));
    }

    public static string DateKey(DateTime moment) => moment.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DateTime ParseDate(string date)
    {
        if (!DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            throw new DomainException("Date illisible : attendu AAAA-MM-JJ.");
        }
        return parsed;
    }

    public static string MonthKey(string date)
    {
        if (date is null || date.Length < 7) throw new DomainException("Date illisible : attendu AAAA-MM-JJ.");
        return date.Substring(0, 7);
    }

    /// <summary>Durée lisible : « 45 min », « 2 h 15 ».</summary>
    public static string Readable(int minutes)
    {
        if (minutes < 60) return string.Concat(minutes.ToString(CultureInfo.InvariantCulture), " min");
        var hours = minutes / 60;
        var rest = minutes % 60;
        return rest == 0
            ? string.Concat(hours.ToString(CultureInfo.InvariantCulture), " h")
            : string.Concat(hours.ToString(CultureInfo.InvariantCulture), " h ", rest.ToString("00", CultureInfo.InvariantCulture));
    }

    private static readonly string[] Weekdays = { "dimanche", "lundi", "mardi", "mercredi", "jeudi", "vendredi", "samedi" };
    private static readonly string[] Months =
    {
        "janvier", "février", "mars", "avril", "mai", "juin",
        "juillet", "août", "septembre", "octobre", "novembre", "décembre",
    };

    /// <summary>
    /// Date en toutes lettres sans dépendre des données de culture : le projet doit rester
    /// lisible même si l'hôte est compilé en globalisation invariante.
    /// </summary>
    public static string FrenchDate(DateTime date)
    {
        var day = date.Day == 1 ? "1er" : date.Day.ToString(CultureInfo.InvariantCulture);
        return string.Concat(Weekdays[(int)date.DayOfWeek], " ", day, " ", Months[date.Month - 1]);
    }
}

internal static class Json
{
    public static readonly JsonSerializerOptions Wire = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        PropertyNameCaseInsensitive = true,
    };

    public static readonly JsonSerializerOptions Pretty = new(Wire) { WriteIndented = true };
}
