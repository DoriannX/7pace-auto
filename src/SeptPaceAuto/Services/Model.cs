#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
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

/// <summary>Un créneau de la journée, tel qu'il circule sur le seam et tel qu'il est stocké.</summary>
public sealed class Entry
{
    [JsonPropertyName("id")] public int? Id { get; set; }
    [JsonPropertyName("start")] public string Start { get; set; } = "08:30";
    [JsonPropertyName("end")] public string End { get; set; } = "08:30";
    [JsonPropertyName("activity")] public string Activity { get; set; } = "unknown";
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("workItem")] public int? WorkItem { get; set; }
    [JsonPropertyName("source")] public string Source { get; set; } = "manual";
    [JsonPropertyName("sentAt")] public string? SentAt { get; set; }

    [JsonIgnore] public int StartMinutes => TimeRules.Minutes(Start);
    [JsonIgnore] public int EndMinutes => TimeRules.Minutes(End);
    [JsonIgnore] public bool Excluded => string.Equals(Activity, "excluded", StringComparison.Ordinal);

    /// <summary>Un créneau non attribué ne part jamais dans 7pace.</summary>
    [JsonIgnore]
    public bool Unassigned =>
        string.Equals(Activity, "unknown", StringComparison.Ordinal) || (!Excluded && (WorkItem is null || WorkItem < 1));

    public Entry Clone() => (Entry)MemberwiseClone();
}

/// <summary>État du chrono poussé à l'interface.</summary>
public sealed record Tracking(
    [property: JsonPropertyName("paused")] bool Paused,
    [property: JsonPropertyName("branch")] string? Branch,
    [property: JsonPropertyName("bug")] int? Bug,
    [property: JsonPropertyName("workItem")] int? WorkItem,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("elapsedSeconds")] int ElapsedSeconds,
    [property: JsonPropertyName("state")] string State);

/// <summary>État d'une intégration, affiché tel quel dans le tiroir d'état.</summary>
public sealed record ConnectionState(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("label")] string Label);

/// <summary>
/// Créneaux de travail configurés par l'utilisateur, en minutes depuis minuit. La pause
/// déjeuner n'est jamais comptée : elle ne tombe dans aucun créneau.
/// </summary>
public sealed class Schedule
{
    /// <summary>Journée de bureau courante en France, utilisée tant que rien n'est réglé.</summary>
    public static readonly Schedule Default = new(new[] { (510, 750), (810, 1020) }, (750, 810));

    private readonly (int Start, int End)[] _windows;

    public Schedule(IReadOnlyList<(int Start, int End)> windows, (int Start, int End) lunch)
    {
        _windows = new (int, int)[windows.Count];
        for (var index = 0; index < windows.Count; index++) _windows[index] = windows[index];
        Lunch = lunch;
    }

    public IReadOnlyList<(int Start, int End)> Windows => _windows;

    public (int Start, int End) Lunch { get; }

    /// <summary>Créneau contenant cette minute, sinon null (pause déjeuner, soirée, week-end exclus ailleurs).</summary>
    public (int Start, int End)? WindowAt(int minute)
    {
        foreach (var window in _windows)
        {
            if (minute >= window.Start && minute < window.End) return window;
        }
        return null;
    }

    public bool InsideWindow(int from, int to)
    {
        foreach (var window in _windows)
        {
            if (from >= window.Start && to <= window.End) return true;
        }
        return false;
    }

    /// <summary>Créneaux en toutes lettres, tels que les messages d'erreur les citent.</summary>
    public string Describe()
    {
        var text = new StringBuilder();
        for (var index = 0; index < _windows.Length; index++)
        {
            if (index > 0) text.Append(index == _windows.Length - 1 ? ", ou " : ", ");
            text.Append("entre ").Append(TimeRules.Clock(_windows[index].Start)).Append(" et ").Append(TimeRules.Clock(_windows[index].End));
        }
        return text.ToString();
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

    /// <summary>Heure en toutes lettres, comme dans l'interface : « 8 h 30 », « 17 h ».</summary>
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

    /// <summary>Durée lisible, même style que la maquette : « 45 min », « 2 h 15 ».</summary>
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

/// <summary>
/// Vocabulaire partagé avec l'interface. Les cinq activités récurrentes sont
/// paramétrables (libellé et élément de travail) ; aucun numéro n'est fourni ici.
/// </summary>
public static class Activities
{
    /// <summary>Activités dont le libellé et la tâche viennent des réglages, dans l'ordre affiché.</summary>
    public static readonly IReadOnlyList<string> Configurable = new[] { "standup", "meeting", "review", "planning", "training" };

    private static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ticket"] = "Développement",
        ["standup"] = "Stand-up",
        ["meeting"] = "Réunion",
        ["review"] = "Revue de sprint",
        ["planning"] = "Rétro / planning",
        ["training"] = "Formation",
        ["unknown"] = "À attribuer",
        ["excluded"] = "Pause / absence",
    };

    /// <summary>Libellé neutre d'une activité, avant toute personnalisation.</summary>
    public static string DefaultLabel(string activity) => Labels.TryGetValue(activity, out var label) ? label : activity;

    public static bool Known(string activity) => Labels.ContainsKey(activity);
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
