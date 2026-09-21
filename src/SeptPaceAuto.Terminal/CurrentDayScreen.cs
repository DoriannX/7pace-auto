using System.Globalization;
using System.Text;
using System.Text.Json;
using SeptPaceAuto.Services;

namespace SeptPaceAuto.Terminal;

/// <summary>Suivi de la journée en cours, tel que l'écran de consultation le reçoit.</summary>
internal sealed record TrackingView(
    string State,
    string Label,
    string? Branch,
    int? Bug,
    int? WorkItem,
    bool QuickRunning,
    DateTimeOffset? ObservedAt,
    DateTimeOffset? BranchAt,
    DateTimeOffset? WrittenAt,
    string? SpanDate,
    int? SpanStart,
    int? SpanEnd,
    string? Error,
    DateTimeOffset? ErrorAt,
    bool GitAvailable,
    int StaleAfterSeconds);

/// <summary>Journée calendaire en cours telle qu'elle est consultée. Rien n'y est modifiable.</summary>
internal sealed record CurrentDayView(
    string Date,
    List<Entry> Entries,
    int[][] Holes,
    int[][] Overlaps,
    int TotalMinutes,
    int PlannedMinutes,
    int Unassigned,
    int Pending,
    TrackingView Tracking);

/// <summary>Surface d'affichage : une image entière remplace la précédente, clavier surveillé.</summary>
internal interface IScreenSurface
{
    /// <summary>Faux quand l'entrée est redirigée : l'écran ne rend alors qu'une seule image.</summary>
    bool Interactive { get; }

    void Draw(string frame);

    /// <summary>
    /// Attend au plus <paramref name="delay"/>. Vrai si l'utilisateur a frappé une touche,
    /// déjà consommée : elle ferme l'écran sans être relue par le menu.
    /// </summary>
    Task<bool> WaitForKeyAsync(TimeSpan delay, CancellationToken ct);
}

/// <summary>Surface réelle : la console est effacée avant chaque image, jamais déroulée.</summary>
internal sealed class ConsoleSurface : IScreenSurface
{
    private static readonly TimeSpan Slice = TimeSpan.FromMilliseconds(120);

    public bool Interactive => !Console.IsInputRedirected;

    public void Draw(string frame)
    {
        try
        {
            Console.Clear();
        }
        catch (IOException)
        {
            // Console sans écran attaché : l'image s'écrit à la suite, sans effacement.
        }
        Console.Out.Write(frame);
        Console.Out.Flush();
    }

    public async Task<bool> WaitForKeyAsync(TimeSpan delay, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + delay;
        while (DateTime.UtcNow < deadline)
        {
            if (Console.KeyAvailable)
            {
                Console.ReadKey(intercept: true);
                return true;
            }
            try
            {
                await Task.Delay(Slice, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// Consultation de la journée en cours : ce qui a déjà été collecté aujourd'hui et l'état
/// réel du suivi, pour savoir tout de suite si la collecte fonctionne.
///
/// L'écran est en lecture seule et le reste : la journée en cours ne se corrige ni ne
/// s'envoie ici, la file du matin garde la priorité, et aucune donnée 7pace n'est lue.
/// Rien n'est écrit sur le disque pour rafraîchir : chaque image relit l'état en mémoire.
/// </summary>
internal sealed class CurrentDayScreen
{
    /// <summary>Cadence du rafraîchissement : assez vive pour voir vivre le suivi, assez lente pour se lire.</summary>
    public static readonly TimeSpan Refresh = TimeSpan.FromSeconds(2);

    private static readonly JsonElement Empty = JsonDocument.Parse("{}").RootElement;

    private readonly Func<CancellationToken, Task<CurrentDayView>> _load;
    private readonly IScreenSurface _surface;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _refresh;

    public CurrentDayScreen(
        Func<CancellationToken, Task<CurrentDayView>> load,
        IScreenSurface surface,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? refresh = null)
    {
        _load = load;
        _surface = surface;
        _clock = clock ?? (static () => DateTimeOffset.Now);
        _refresh = refresh ?? Refresh;
    }

    /// <summary>Nombre d'images réellement dessinées : une vue inchangée n'en produit aucune.</summary>
    public int Frames { get; private set; }

    /// <summary>Dernière vue obtenue, pour que le menu reprenne un état à jour.</summary>
    public CurrentDayView? Last { get; private set; }

    public async Task RunAsync(CancellationToken ct)
    {
        string? drawn = null;
        while (!ct.IsCancellationRequested)
        {
            var view = await _load(ct).ConfigureAwait(false);
            Last = view;

            var frame = Render(view, _clock(), _surface.Interactive, _refresh);
            if (!string.Equals(frame, drawn, StringComparison.Ordinal))
            {
                _surface.Draw(frame);
                drawn = frame;
                Frames++;
            }

            // Entrée redirigée : le clavier n'est pas interrogeable, et consommer la saisie
            // en attente volerait le choix suivant du menu.
            if (!_surface.Interactive) return;
            if (await _surface.WaitForKeyAsync(_refresh, ct).ConfigureAwait(false)) return;
        }
    }

    // ---------- image ----------

    internal static string Render(CurrentDayView view, DateTimeOffset now, bool live, TimeSpan refresh)
    {
        var text = new StringBuilder();
        var tracking = view.Tracking;

        text.AppendLine($"Journée en cours — {TimeRules.FrenchDate(TimeRules.ParseDate(view.Date))} ({view.Date})");
        text.AppendLine("Consultation seule : cette journée se corrige et s’envoie demain matin.");
        text.AppendLine();

        text.AppendLine($"Suivi : {TrackingLabels.For(tracking.State)} · {tracking.Label}");
        text.AppendLine($"  Branche : {(string.IsNullOrEmpty(tracking.Branch) ? "aucune branche lue" : tracking.Branch)}");
        text.AppendLine($"  Ticket : {Ticket(tracking)}");
        text.AppendLine($"  Créneau en cours : {ActiveSpan(view, tracking)}");
        text.AppendLine($"  Dernier relevé : {Freshness(tracking.ObservedAt, now, tracking.StaleAfterSeconds)}");
        text.AppendLine($"  Dernière lecture de branche : {Freshness(tracking.BranchAt, now, tracking.StaleAfterSeconds)}");
        text.AppendLine($"  Dernière écriture : {Freshness(tracking.WrittenAt, now, tracking.StaleAfterSeconds)}");
        text.AppendLine($"  Chrono rapide : {(tracking.QuickRunning ? "en cours" : "arrêté")}");
        if (!tracking.GitAvailable)
        {
            text.AppendLine("  Git est introuvable sur ce poste : aucun relevé n’est possible.");
        }
        if (!string.IsNullOrEmpty(tracking.Error))
        {
            text.AppendLine($"  Dernière erreur : {tracking.Error} ({Moment(tracking.ErrorAt)})");
        }
        text.AppendLine();

        if (view.Entries.Count == 0)
        {
            text.AppendLine("Aucun créneau collecté pour l’instant.");
        }
        else
        {
            text.AppendLine("Créneaux déjà enregistrés :");
            foreach (var entry in view.Entries.OrderBy(item => item.StartMinutes).ThenBy(item => item.Id ?? 0))
            {
                var duration = TimeRules.Readable(Math.Max(0, entry.EndMinutes - entry.StartMinutes));
                var ticket = entry.WorkItem is int item
                    ? string.Concat("#", item.ToString(CultureInfo.InvariantCulture))
                    : "à attribuer";
                var label = entry.Label.Length > 0 ? $" · {entry.Label}" : string.Empty;
                var sent = entry.SentAt is not null ? " · envoyé" : string.Empty;
                text.AppendLine($"  {entry.Start}–{entry.End} · {duration} · {ticket}{label} · {SourceLabel(entry.Source)}{sent}");
            }
        }

        var unassigned = view.Entries
            .Where(entry => entry.Unassigned)
            .OrderBy(entry => entry.StartMinutes)
            .Select(entry => $"{entry.Start}–{entry.End}")
            .ToArray();
        if (unassigned.Length > 0)
        {
            text.AppendLine($"  Intervalles non attribués : {string.Join(", ", unassigned)}");
        }
        // Les horaires à venir ne sont pas des trous : sur la journée en cours, seul le temps
        // déjà écoulé peut manquer. Annoncer l'après-midi ferait croire à une collecte en panne.
        var holes = ElapsedHoles(view, now);
        if (holes.Length > 0) text.AppendLine($"  Trous déjà constatés : {Spans(holes)}");
        if (view.Overlaps.Length > 0) text.AppendLine($"  Chevauchements : {Spans(view.Overlaps)}");
        text.AppendLine($"  Total collecté : {TimeRules.Readable(view.TotalMinutes)} sur {TimeRules.Readable(view.PlannedMinutes)} prévues");
        text.AppendLine();

        text.AppendLine(view.Pending switch
        {
            0 => "Aucune journée terminée n’attend d’être envoyée.",
            1 => "1 journée terminée attend d’être corrigée puis envoyée : reviens au menu.",
            _ => $"{view.Pending} journées terminées attendent d’être corrigées puis envoyées : reviens au menu.",
        });
        text.AppendLine(live
            ? $"Rafraîchissement toutes les {Seconds(refresh)} · appuie sur une touche pour revenir au menu."
            : "Entrée redirigée : image unique, sans rafraîchissement.");

        return text.ToString();
    }

    private static string Seconds(TimeSpan delay) =>
        string.Concat(Math.Max(1, (int)Math.Round(delay.TotalSeconds)).ToString(CultureInfo.InvariantCulture), " s");

    /// <summary>Trous ramenés au temps déjà écoulé de la journée consultée.</summary>
    private static int[][] ElapsedHoles(CurrentDayView view, DateTimeOffset now)
    {
        var date = TimeRules.ParseDate(view.Date);
        var limit = now.Date > date
            ? 24 * 60
            : now.Date < date ? 0 : now.Hour * 60 + now.Minute;

        return view.Holes
            .Where(span => span.Length == 2 && span[0] < limit)
            .Select(span => new[] { span[0], Math.Min(span[1], limit) })
            .Where(span => span[1] > span[0])
            .ToArray();
    }

    private static string Ticket(TrackingView tracking)
    {
        if (tracking.WorkItem is int item)
        {
            var origin = tracking.Bug is int bug ? $" (depuis le Bug ou PBI #{bug})" : string.Empty;
            return string.Concat("#", item.ToString(CultureInfo.InvariantCulture), origin);
        }
        return tracking.Bug is int pending
            ? $"#{pending} lu dans la branche, Fix ou Task pas encore résolu"
            : "aucun, attribution à compléter demain";
    }

    private static string ActiveSpan(CurrentDayView view, TrackingView tracking)
    {
        if (tracking.SpanStart is not int start) return "aucun : rien n’est compté en ce moment";
        if (!string.Equals(tracking.SpanDate, view.Date, StringComparison.Ordinal))
        {
            return $"ouvert sur le {tracking.SpanDate} : la journée a changé depuis le dernier relevé";
        }

        // Un bloc vient de s'ouvrir : la première minute n'est pas encore écrite.
        if (tracking.SpanEnd is not int end) return $"ouvert à {TimeRules.AsTime(start)}, aucune minute encore écrite";

        return $"{TimeRules.AsTime(start)}–{TimeRules.AsTime(end)} ({TimeRules.Readable(Math.Max(0, end - start))})";
    }

    /// <summary>
    /// Âge d'un horodatage. Au-delà du délai annoncé par le cœur, le suivi est déclaré figé :
    /// c'est le seul signal qui distingue une collecte vivante d'une collecte arrêtée.
    /// </summary>
    private static string Freshness(DateTimeOffset? moment, DateTimeOffset now, int staleAfterSeconds)
    {
        if (moment is not DateTimeOffset at) return "jamais — le suivi n’a encore rien relevé";
        var elapsed = now - at;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        var age = $"{Moment(at)} · il y a {Elapsed(elapsed)}";
        return elapsed.TotalSeconds > staleAfterSeconds ? string.Concat(age, " — suivi figé") : age;
    }

    private static string Moment(DateTimeOffset? moment) =>
        moment is DateTimeOffset at ? at.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "jamais";

    private static string Elapsed(TimeSpan span)
    {
        if (span.TotalSeconds < 60) return string.Concat(((int)span.TotalSeconds).ToString(CultureInfo.InvariantCulture), " s");
        return TimeRules.Readable((int)span.TotalMinutes);
    }

    private static string Spans(int[][] spans) => string.Join(
        ", ",
        spans.Where(span => span.Length == 2).Select(span => $"{TimeRules.AsTime(span[0])}–{TimeRules.AsTime(span[1])}"));

    private static string SourceLabel(string source) => source switch
    {
        "git" => "suivi Git",
        "quick" => "chrono rapide",
        "gap" => "intervalle à préciser",
        _ => "saisie manuelle",
    };

    // ---------- lecture du contrat ----------

    internal static CurrentDayView Parse(JsonElement root, JsonSerializerOptions wire)
    {
        var tracking = Child(root, "tracking");
        var health = Child(root, "health");

        return new CurrentDayView(
            Text(root, "date"),
            root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array
                ? entries.Deserialize<List<Entry>>(wire) ?? new List<Entry>()
                : new List<Entry>(),
            SpanArray(root, "holes", wire),
            SpanArray(root, "overlaps", wire),
            Number(root, "totalMinutes"),
            Number(root, "plannedMinutes"),
            Number(root, "unassigned"),
            Number(root, "pending"),
            new TrackingView(
                Text(tracking, "state"),
                Text(tracking, "label"),
                Optional(tracking, "branch"),
                OptionalNumber(tracking, "bug"),
                OptionalNumber(tracking, "workItem"),
                tracking.TryGetProperty("quickRunning", out var quick) && quick.ValueKind == JsonValueKind.True,
                Instant(health, "observedAt"),
                Instant(health, "branchAt"),
                Instant(health, "writtenAt"),
                Optional(health, "spanDate"),
                OptionalNumber(health, "spanStart"),
                OptionalNumber(health, "spanEnd"),
                Optional(health, "error"),
                Instant(health, "errorAt"),
                !health.TryGetProperty("gitAvailable", out var git) || git.ValueKind != JsonValueKind.False,
                Math.Max(30, Number(health, "staleAfterSeconds"))));
    }

    private static JsonElement Child(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : Empty;

    private static int[][] SpanArray(JsonElement root, string name, JsonSerializerOptions wire) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.Deserialize<int[][]>(wire) ?? Array.Empty<int[]>()
            : Array.Empty<int[]>();

    private static DateTimeOffset? Instant(JsonElement element, string name) =>
        Optional(element, name) is string raw
            && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var moment)
            ? moment
            : null;

    private static string? Optional(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private static int? OptionalNumber(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static int Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : 0;

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
