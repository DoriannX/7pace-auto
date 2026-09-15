#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SeptPaceAuto.Services;

/// <summary>Ce qu'une remise à plat sur 7pace a changé, sans le détail créneau par créneau.</summary>
public sealed record MirrorChange(int Replaced, int Removed, int Imported, int SecondsDelta);

/// <summary>
/// Journées persistées un fichier par mois, sous %LOCALAPPDATA%\7pace-auto\days\AAAA-MM.json.
/// Toutes les mutations passent par ici : c'est le seul endroit qui valide les créneaux.
/// </summary>
public sealed class DayStore
{
    private readonly object _gate = new();
    private readonly Func<Profile> _profile;
    private readonly string _folder;
    private readonly Dictionary<string, Dictionary<string, List<Entry>>> _months = new(StringComparer.Ordinal);

    /// <summary>Journée modifiée : (date, créneaux). Déclenché hors verrou.</summary>
    public event Action<string, List<Entry>>? DayChanged;

    public DayStore(Func<Profile> profile) : this(profile, AppPaths.Days) { }

    /// <param name="profile">Réglages actifs : les créneaux de travail et les tâches fixes en dépendent.</param>
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

    /// <summary>Journées non vides de l'intervalle, bornes incluses.</summary>
    public Dictionary<string, List<Entry>> Range(string from, string to)
    {
        var first = TimeRules.ParseDate(from);
        var last = TimeRules.ParseDate(to);
        if (last < first) (first, last) = (last, first);

        var result = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
        lock (_gate)
        {
            for (var cursor = new DateTime(first.Year, first.Month, 1); cursor <= last; cursor = cursor.AddMonths(1))
            {
                var month = Month(cursor.ToString("yyyy-MM", CultureInfo.InvariantCulture));
                foreach (var pair in month)
                {
                    if (pair.Value.Count == 0) continue;
                    if (!DateTime.TryParseExact(pair.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
                    if (day < first || day > last) continue;
                    result[pair.Key] = Snapshot(pair.Value);
                }
            }
        }
        return result;
    }

    // ---------- mutations demandées par l'interface ----------

    /// <summary>
    /// Crée ou remplace un créneau saisi à la main. Les refus reprennent mot pour mot
    /// les phrases de la maquette validée. L'horodatage d'envoi appartient au magasin :
    /// il n'est posé que par un envoi accepté, et une saisie ne peut pas l'effacer.
    /// </summary>
    public List<Entry> Save(string date, Entry incoming)
    {
        TimeRules.ParseDate(date);
        var profile = _profile();
        List<Entry> snapshot;
        lock (_gate)
        {
            var day = DayList(date, create: true)!;
            var from = TimeRules.Minutes(incoming.Start);
            var to = TimeRules.Minutes(incoming.End);

            if (to <= from) throw new DomainException("La fin doit être après le début du créneau.");
            if (!profile.Schedule.InsideWindow(from, to))
            {
                throw new DomainException($"Choisis un créneau {profile.Schedule.Describe()}.");
            }

            var activity = (incoming.Activity ?? string.Empty).Trim();
            if (!Activities.Known(activity)) throw new DomainException("Ce type d’activité n’existe pas dans l’application.");

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

            foreach (var other in day)
            {
                if (other.Id == existing?.Id) continue;
                if (from < other.EndMinutes && to > other.StartMinutes)
                {
                    throw new DomainException("Ce créneau chevauche une autre activité. Ajuste les horaires pour ne pas compter deux fois le même temps.");
                }
            }

            int? workItem = string.Equals(activity, "ticket", StringComparison.Ordinal)
                ? incoming.WorkItem
                : profile.WorkItemFor(activity);
            if (string.Equals(activity, "ticket", StringComparison.Ordinal) && (workItem is null || workItem < 1))
            {
                throw new DomainException("Indique un numéro de Fix ou de Task entier et positif.");
            }

            var entry = existing ?? new Entry { Id = NextId(day) };
            entry.Start = TimeRules.AsTime(from);
            entry.End = TimeRules.AsTime(to);
            entry.Activity = activity;
            entry.Title = string.IsNullOrWhiteSpace(incoming.Title) ? profile.Label(activity) : incoming.Title.Trim();
            entry.WorkItem = workItem;
            // Un créneau corrigé à la main cesse d'être piloté par le suivi Git : celui-ci ne
            // pourra plus réécrire son activité, seulement prolonger sa fin s'il l'a créé.
            entry.Source = "manual";
            // entry.SentAt n'est jamais touché ici : seul un envoi accepté le pose.
            if (existing is null) day.Add(entry);

            Sort(day);
            Persist(TimeRules.MonthKey(date));
            snapshot = Snapshot(day);
        }
        Raise(date, snapshot);
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
                throw new DomainException("Ce créneau est déjà envoyé dans 7pace : corrige-le dans 7pace, le supprimer ici ferait perdre la trace de l’envoi.");
            }
            day!.Remove(found);
            Persist(TimeRules.MonthKey(date));
            snapshot = Snapshot(day);
        }
        Raise(date, snapshot);
        return snapshot;
    }

    /// <summary>
    /// Marque comme envoyés seulement les créneaux dont l'envoi a réellement abouti, et
    /// retient l'identifiant que 7pace leur a donné : sans lui, la relecture ne peut pas
    /// reconnaître le créneau et le remplacerait par un bloc regroupé.
    /// </summary>
    public List<Entry> StampSent(string date, IReadOnlyDictionary<int, string?> workLogs, string sentAt)
    {
        if (workLogs.Count == 0) return Day(date);
        List<Entry> snapshot;
        lock (_gate)
        {
            var day = DayList(date, create: false);
            if (day is null) return new List<Entry>();
            foreach (var entry in day)
            {
                if (entry.Id is not int id || !workLogs.TryGetValue(id, out var workLogId)) continue;
                entry.SentAt = sentAt;
                entry.WorkLogId = workLogId;
            }
            Persist(TimeRules.MonthKey(date));
            snapshot = Snapshot(day);
        }
        Raise(date, snapshot);
        return snapshot;
    }

    /// <summary>
    /// Remet la partie « miroir » de la journée en conformité avec 7pace. Les créneaux non
    /// envoyés sont des brouillons qui appartiennent à l'application : ils ne sont jamais
    /// touchés. Les créneaux envoyés, eux, ne sont qu'un reflet : 7pace tranche seul.
    ///
    /// Les garde-fous de <see cref="Save"/> — créneau de travail, chevauchement, refus de
    /// modifier un envoi — ne s'appliquent pas ici : 7pace décrit un fait extérieur, le
    /// refléter n'est pas une saisie.
    /// </summary>
    /// <param name="mirror">Worklogs 7pace de cette journée, heure de début déjà locale.</param>
    /// <param name="apply">Faux : rien n'est écrit, seuls les compteurs sont calculés.</param>
    public MirrorChange Reconcile(string date, IReadOnlyList<WorkLog> mirror, bool apply)
    {
        TimeRules.ParseDate(date);
        var profile = _profile();
        List<Entry>? snapshot = null;
        MirrorChange change;

        lock (_gate)
        {
            var stored = DayList(date, create: apply && mirror.Count > 0);
            if (stored is null && mirror.Count == 0) return new MirrorChange(0, 0, 0, 0);

            // En aperçu on raisonne sur une copie : aucun état visible ne bouge.
            var day = apply ? stored! : Snapshot(stored);
            var before = Total(day);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var replaced = 0;
            var removed = 0;
            var imported = 0;

            for (var index = day.Count - 1; index >= 0; index--)
            {
                var entry = day[index];
                if (entry.SentAt is null) continue;   // brouillon : propriété de l'application

                var match = entry.WorkLogId is null
                    ? null
                    : mirror.FirstOrDefault(log => string.Equals(log.Id, entry.WorkLogId, StringComparison.Ordinal));
                if (match is null)
                {
                    day.RemoveAt(index);
                    removed++;
                    continue;
                }

                seen.Add(match.Id);
                var (start, end) = Span(match);
                if (string.Equals(entry.Start, start, StringComparison.Ordinal)
                    && string.Equals(entry.End, end, StringComparison.Ordinal)
                    && entry.WorkItem == match.WorkItem)
                {
                    continue;
                }

                entry.Start = start;
                entry.End = end;
                entry.WorkItem = match.WorkItem;
                replaced++;
            }

            var stamp = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
            foreach (var log in mirror)
            {
                if (!seen.Add(log.Id)) continue;

                var (start, end) = Span(log);
                var activity = ActivityFor(profile, log.WorkItem);
                day.Add(new Entry
                {
                    Id = NextId(day),
                    Start = start,
                    End = end,
                    Activity = activity,
                    Title = string.Equals(activity, "ticket", StringComparison.Ordinal) ? $"Fix #{log.WorkItem}" : profile.Label(activity),
                    WorkItem = log.WorkItem,
                    Source = "7pace",
                    SentAt = stamp,
                    WorkLogId = log.Id,
                });
                imported++;
            }

            change = new MirrorChange(replaced, removed, imported, Total(day) - before);
            if (!apply || (replaced == 0 && removed == 0 && imported == 0)) return change;

            Sort(day);
            Persist(TimeRules.MonthKey(date));
            snapshot = Snapshot(day);
        }

        if (snapshot is not null) Raise(date, snapshot);
        return change;
    }

    /// <summary>Bornes locales d'un worklog, tronquées à la journée : l'application n'a pas de créneau à cheval.</summary>
    private static (string Start, string End) Span(WorkLog log)
    {
        var from = TimeRules.MinuteOfDay(log.StartLocal);
        var to = from + (log.Seconds + 59) / 60;
        return (TimeRules.AsTime(from), TimeRules.AsTime(Math.Min(to, 24 * 60 - 1)));
    }

    /// <summary>Activité locale déduite du numéro : les tâches fixes se reconnaissent, le reste est du développement.</summary>
    private static string ActivityFor(Profile profile, int workItem)
    {
        foreach (var pair in profile.FixedTasks)
        {
            if (pair.Value == workItem) return pair.Key;
        }
        return "ticket";
    }

    private static int Total(List<Entry> day)
    {
        var minutes = 0;
        foreach (var entry in day) minutes += entry.EndMinutes - entry.StartMinutes;
        return minutes * 60;
    }

    // ---------- mutation demandée par le suivi Git ----------

    /// <summary>
    /// Écrit ou prolonge le bloc suivi. Le créneau est rogné sur le créneau de travail et sur
    /// les activités déjà présentes : le suivi ne double jamais un temps saisi à la main.
    /// Retourne le créneau écrit (null si rien n'était écrivable).
    /// </summary>
    public Entry? WriteTracked(string date, int from, int to, string activity, string title, int? workItem, int? bug, string source, int? entryId)
    {
        List<Entry> snapshot;
        Entry written;
        lock (_gate)
        {
            var window = _profile().Schedule.WindowAt(from);
            if (window is null) return null;
            to = Math.Min(to, window.Value.End);
            if (to <= from) return null;

            var day = DayList(date, create: true)!;
            Entry? existing = entryId is int id ? day.FirstOrDefault(entry => entry.Id == id) : null;
            if (existing is not null && existing.SentAt is not null) return null;

            foreach (var other in day)
            {
                if (other.Id == existing?.Id) continue;
                if (other.StartMinutes <= from && other.EndMinutes > from) return null;   // le début est déjà occupé
                if (other.StartMinutes > from && other.StartMinutes < to) to = other.StartMinutes;
            }
            if (to <= from) return null;

            var trackerOwned = existing is null || string.Equals(existing.Source, "git", StringComparison.Ordinal) || string.Equals(existing.Source, "gap", StringComparison.Ordinal);
            written = existing ?? new Entry { Id = NextId(day), Source = source };
            written.Start = TimeRules.AsTime(from);
            written.End = TimeRules.AsTime(Math.Max(to, existing?.EndMinutes ?? to));
            if (trackerOwned)
            {
                written.Activity = activity;
                written.Title = title;
                written.WorkItem = workItem;
                written.Bug = bug ?? written.Bug;
                written.Source = source;
            }
            if (existing is null) day.Add(written);

            Sort(day);
            Persist(TimeRules.MonthKey(date));
            snapshot = Snapshot(day);
        }
        Raise(date, snapshot);
        return written.Clone();
    }

    // ---------- interne ----------

    private void Raise(string date, List<Entry> snapshot) => DayChanged?.Invoke(date, snapshot);

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

    private List<Entry> Normalize(List<Entry> raw)
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
            if (!Activities.Known(entry.Activity)) entry.Activity = "unknown";
            if (string.IsNullOrWhiteSpace(entry.Source)) entry.Source = "manual";
            if (string.IsNullOrWhiteSpace(entry.Title)) entry.Title = _profile().Label(entry.Activity);
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
}
