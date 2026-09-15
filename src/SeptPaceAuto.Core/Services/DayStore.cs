#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SeptPaceAuto.Services;

/// <summary>Journées closes : envoyées intégralement ou explicitement ignorées.</summary>
internal sealed class ClosedDays
{
    [JsonPropertyName("dates")] public List<string> Dates { get; set; } = new();
}

/// <summary>Période des horaires de travail que ne couvre aucun créneau.</summary>
public sealed record Hole(int Start, int End);

/// <summary>Période couverte par plusieurs créneaux, signalée sans jamais être corrigée.</summary>
public sealed record Overlap(int Start, int End);

/// <summary>
/// Journées persistées un fichier par mois, sous %LOCALAPPDATA%\7pace-auto\days\AAAA-MM.json,
/// et liste des journées closes. Toutes les mutations passent par ici.
///
/// Une journée envoyée n'est pas conservée : son détail est supprimé et seule sa date reste,
/// pour ne jamais la reproposer. 7pace est alors la seule source de vérité.
/// </summary>
public sealed class DayStore
{
    private readonly object _gate = new();
    private readonly Func<Profile> _profile;
    private readonly string _folder;
    private readonly Dictionary<string, Dictionary<string, List<Entry>>> _months = new(StringComparer.Ordinal);
    private readonly HashSet<string> _closed = new(StringComparer.Ordinal);

    public DayStore(Func<Profile> profile) : this(profile, AppPaths.Days) { }

    /// <param name="profile">Réglages actifs : les horaires de travail en dépendent.</param>
    public DayStore(Func<Profile> profile, string folder)
    {
        _profile = profile;
        _folder = folder;
        try
        {
            Directory.CreateDirectory(_folder);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Le dossier sera retenté à la première écriture.
        }

        foreach (var date in AppPaths.ReadJson<ClosedDays>(AppPaths.ClosedDays)?.Dates ?? new List<string>())
        {
            if (!string.IsNullOrWhiteSpace(date)) _closed.Add(date);
        }
    }

    // ---------- lecture ----------

    public List<Entry> Day(string date)
    {
        TimeRules.ParseDate(date);
        lock (_gate)
        {
            var day = Month(TimeRules.MonthKey(date)).TryGetValue(date, out var list) ? list : null;
            return Snapshot(day);
        }
    }

    /// <summary>
    /// Journées calendaires terminées qui portent encore des créneaux et ne sont pas closes,
    /// de la plus ancienne à la plus récente. La journée en cours n'y figure jamais : elle
    /// est encore en cours de collecte.
    /// </summary>
    public List<string> Pending(string today)
    {
        TimeRules.ParseDate(today);
        var dates = new SortedSet<string>(StringComparer.Ordinal);
        lock (_gate)
        {
            foreach (var month in MonthKeys())
            {
                foreach (var pair in Month(month))
                {
                    if (pair.Value.Count == 0) continue;
                    if (string.CompareOrdinal(pair.Key, today) >= 0) continue;
                    if (_closed.Contains(pair.Key)) continue;
                    dates.Add(pair.Key);
                }
            }
        }
        return dates.ToList();
    }

    /// <summary>Mois présents sur le disque, plus ceux déjà chargés en mémoire.</summary>
    private List<string> MonthKeys()
    {
        var months = new SortedSet<string>(_months.Keys, StringComparer.Ordinal);
        try
        {
            foreach (var file in Directory.EnumerateFiles(_folder, "*.json"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.Length == 7 && DateTime.TryParseExact(name + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                {
                    months.Add(name);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Dossier illisible pour l'instant : on se contente de ce qui est en mémoire.
        }
        return months.ToList();
    }

    // ---------- mutations demandées par l'interface ----------

    /// <summary>
    /// Crée ou corrige un créneau saisi à la main. Les chevauchements sont acceptés : 7pace
    /// admet plusieurs imputations sur la même période, et c'est l'utilisateur qui tranche.
    /// L'horodatage d'envoi appartient au magasin : une saisie ne peut pas l'effacer.
    /// </summary>
    public List<Entry> Save(string date, Entry incoming)
    {
        TimeRules.ParseDate(date);
        List<Entry> snapshot;
        lock (_gate)
        {
            var day = DayList(date, create: true)!;
            var from = TimeRules.Minutes(incoming.Start);
            var to = TimeRules.Minutes(incoming.End);
            if (to <= from) throw new DomainException("La fin doit être après le début du créneau.");

            Entry? existing = null;
            if (incoming.Id is int wanted)
            {
                existing = day.FirstOrDefault(entry => entry.Id == wanted)
                    ?? throw new DomainException("Ce créneau n’existe plus : rouvre la journée avant de l’enregistrer.");
                if (existing.SentAt is not null)
                {
                    throw new DomainException("Ce créneau est déjà envoyé dans 7pace : corrige-le dans 7pace plutôt qu’ici.");
                }
            }

            if (incoming.WorkItem is int item && item < 1)
            {
                throw new DomainException("Indique un numéro de Fix ou de Task entier et positif.");
            }

            var entry = existing ?? new Entry { Id = NextId(day) };
            entry.Start = TimeRules.AsTime(from);
            entry.End = TimeRules.AsTime(to);
            entry.WorkItem = incoming.WorkItem;
            entry.Label = (incoming.Label ?? string.Empty).Trim();
            // Un créneau corrigé à la main cesse d'être piloté par le suivi Git.
            entry.Source = "manual";
            // entry.SentAt n'est jamais touché ici : seul un envoi accepté le pose.
            if (existing is null) day.Add(entry);

            Sort(day);
            Persist(TimeRules.MonthKey(date));
            snapshot = Snapshot(day);
        }

        return snapshot;
    }

    public List<Entry> Delete(string date, int id)
    {
        TimeRules.ParseDate(date);
        List<Entry> snapshot;
        lock (_gate)
        {
            var day = DayList(date, create: false);
            var found = day?.FirstOrDefault(entry => entry.Id == id)
                ?? throw new DomainException("Ce créneau n’existe plus : la journée a déjà été modifiée.");
            if (found.SentAt is not null)
            {
                throw new DomainException("Ce créneau est déjà envoyé dans 7pace : supprime-le dans 7pace, pas ici.");
            }
            day!.Remove(found);
            Persist(TimeRules.MonthKey(date));
            snapshot = Snapshot(day);
        }

        return snapshot;
    }

    /// <summary>
    /// Verrouille les créneaux dont l'envoi a réellement abouti. Un envoi partiel laisse les
    /// autres modifiables : ils repartiront seuls, sans jamais compter deux fois le même temps.
    /// </summary>
    public List<Entry> StampSent(string date, IReadOnlyCollection<int> entryIds, string sentAt)
    {
        if (entryIds.Count == 0) return Day(date);
        var wanted = new HashSet<int>(entryIds);
        List<Entry> snapshot;
        lock (_gate)
        {
            var day = DayList(date, create: false);
            if (day is null) return new List<Entry>();
            foreach (var entry in day)
            {
                if (entry.Id is int id && wanted.Contains(id)) entry.SentAt = sentAt;
            }
            Persist(TimeRules.MonthKey(date));
            snapshot = Snapshot(day);
        }

        return snapshot;
    }

    /// <summary>
    /// Clôt une journée : son détail local disparaît et seule sa date est retenue, pour ne
    /// jamais la reproposer. Utilisé après un envoi complet comme après un abandon explicite.
    /// </summary>
    public void Close(string date)
    {
        TimeRules.ParseDate(date);
        lock (_gate)
        {
            var month = Month(TimeRules.MonthKey(date));
            month.Remove(date);
            _closed.Add(date);
            Persist(TimeRules.MonthKey(date));
            PersistClosed();
        }
    }

    public bool IsClosed(string date)
    {
        lock (_gate) return _closed.Contains(date);
    }

    // ---------- mutations demandées par le suivi ----------

    /// <summary>
    /// Écrit ou prolonge un créneau de collecte. Le suivi et le chrono rapide possèdent leur
    /// propre créneau et ne regardent pas les autres : deux imputations simultanées sont
    /// légitimes, l'utilisateur tranche le lendemain.
    /// </summary>
    /// <param name="entryId">Créneau déjà ouvert à prolonger, nul pour en créer un.</param>
    public Entry? WriteSpan(string date, int from, int to, string label, int? workItem, int? bug, string source, int? entryId)
    {
        if (to <= from) return null;

        Entry written;
        lock (_gate)
        {
            var day = DayList(date, create: true)!;
            var existing = entryId is int id ? day.FirstOrDefault(entry => entry.Id == id) : null;
            if (existing is not null && existing.SentAt is not null) return null;

            // Une correction à la main reprend la main : le suivi prolonge encore la fin,
            // mais ne réécrit plus l'attribution choisie par l'utilisateur.
            var owned = existing is null || !string.Equals(existing.Source, "manual", StringComparison.Ordinal);
            written = existing ?? new Entry { Id = NextId(day), Source = source };
            written.Start = TimeRules.AsTime(from);
            written.End = TimeRules.AsTime(Math.Max(to, existing?.EndMinutes ?? to));
            if (owned)
            {
                written.Label = label;
                written.WorkItem = workItem;
                written.Bug = bug ?? written.Bug;
                written.Source = source;
            }
            if (existing is null) day.Add(written);

            Sort(day);
            Persist(TimeRules.MonthKey(date));
        }
        return written.Clone();
    }

    // ---------- relecture de la journée ----------

    /// <summary>Périodes de travail prévues que ne couvre aucun créneau.</summary>
    public static List<Hole> Holes(Schedule schedule, IReadOnlyList<Entry> entries)
    {
        var holes = new List<Hole>();
        var covered = entries
            .Select(entry => (Start: entry.StartMinutes, End: entry.EndMinutes))
            .OrderBy(span => span.Start)
            .ToList();

        foreach (var window in schedule.Windows)
        {
            var cursor = window.Start;
            foreach (var span in covered)
            {
                if (span.End <= cursor) continue;
                if (span.Start >= window.End) break;
                if (span.Start > cursor) holes.Add(new Hole(cursor, Math.Min(span.Start, window.End)));
                cursor = Math.Max(cursor, span.End);
                if (cursor >= window.End) break;
            }
            if (cursor < window.End) holes.Add(new Hole(cursor, window.End));
        }
        return holes;
    }

    /// <summary>Périodes couvertes par plusieurs créneaux, signalées sans être corrigées.</summary>
    public static List<Overlap> Overlaps(IReadOnlyList<Entry> entries)
    {
        var overlaps = new List<Overlap>();
        var ordered = entries.OrderBy(entry => entry.StartMinutes).ToList();
        for (var index = 0; index < ordered.Count; index++)
        {
            for (var other = index + 1; other < ordered.Count; other++)
            {
                var start = Math.Max(ordered[index].StartMinutes, ordered[other].StartMinutes);
                var end = Math.Min(ordered[index].EndMinutes, ordered[other].EndMinutes);
                if (end <= start) continue;
                overlaps.Add(new Overlap(start, end));
            }
        }
        return overlaps;
    }

    // ---------- interne ----------

    private static void Sort(List<Entry> day) => day.Sort(static (left, right) =>
    {
        var byStart = left.StartMinutes.CompareTo(right.StartMinutes);
        return byStart != 0 ? byStart : (left.Id ?? 0).CompareTo(right.Id ?? 0);
    });

    private static List<Entry> Snapshot(List<Entry>? day)
    {
        if (day is null) return new List<Entry>();
        var copy = new List<Entry>(day.Count);
        foreach (var entry in day) copy.Add(entry.Clone());
        return copy;
    }

    private static int NextId(List<Entry> day)
    {
        var highest = 0;
        foreach (var entry in day)
        {
            if (entry.Id is int id && id > highest) highest = id;
        }
        return highest + 1;
    }

    private List<Entry>? DayList(string date, bool create)
    {
        var month = Month(TimeRules.MonthKey(date));
        if (month.TryGetValue(date, out var day)) return day;
        if (!create) return null;
        day = new List<Entry>();
        month[date] = day;
        return day;
    }

    private Dictionary<string, List<Entry>> Month(string month)
    {
        if (_months.TryGetValue(month, out var cached)) return cached;

        var loaded = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
        var path = Path.Combine(_folder, month + ".json");
        try
        {
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path, Encoding.UTF8);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var parsed = JsonSerializer.Deserialize<Dictionary<string, List<Entry>>>(text, Json.Wire);
                    if (parsed is not null)
                    {
                        foreach (var pair in parsed)
                        {
                            if (pair.Value is null) continue;
                            loaded[pair.Key] = Normalize(pair.Value);
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Fichier illisible : on l'écarte pour ne pas perdre les journées suivantes,
            // et il reste sur le disque pour être récupéré à la main.
            Quarantine(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Lecture impossible pour l'instant : on repart d'un mois vide en mémoire,
            // la prochaine écriture retentera le fichier.
        }

        _months[month] = loaded;
        return loaded;
    }

    private static void Quarantine(string path)
    {
        try
        {
            File.Move(path, path + ".illisible", overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Rien de plus à tenter.
        }
    }

    private static List<Entry> Normalize(List<Entry> raw)
    {
        var day = new List<Entry>(raw.Count);
        var used = new HashSet<int>();
        var highest = 0;
        foreach (var entry in raw)
        {
            if (entry is null) continue;
            if (entry.Id is int id && id > 0 && used.Add(id))
            {
                if (id > highest) highest = id;
            }
            else
            {
                entry.Id = ++highest;
                used.Add(highest);
            }
            if (string.IsNullOrWhiteSpace(entry.Source)) entry.Source = "manual";
            if (entry.WorkItem is int item && item < 1) entry.WorkItem = null;
            day.Add(entry);
        }
        Sort(day);
        return day;
    }

    private void Persist(string month)
    {
        if (!_months.TryGetValue(month, out var days)) return;
        var payload = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
        foreach (var key in days.Keys.OrderBy(static key => key, StringComparer.Ordinal))
        {
            if (days[key].Count > 0) payload[key] = days[key];
        }
        try
        {
            AppPaths.WriteAtomic(Path.Combine(_folder, month + ".json"), JsonSerializer.Serialize(payload, Json.Pretty));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new DomainException("Impossible d’écrire la journée sur le disque : vérifie l’accès à %LOCALAPPDATA%\\7pace-auto.");
        }
    }

    private void PersistClosed()
    {
        var payload = new ClosedDays { Dates = _closed.OrderBy(static date => date, StringComparer.Ordinal).ToList() };
        try
        {
            AppPaths.EnsureRoot();
            AppPaths.WriteAtomic(AppPaths.ClosedDays, JsonSerializer.Serialize(payload, Json.Pretty));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new DomainException("Impossible d’enregistrer la clôture de la journée : vérifie l’accès à %LOCALAPPDATA%\\7pace-auto.");
        }
    }
}
