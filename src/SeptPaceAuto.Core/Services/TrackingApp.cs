#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SeptPaceAuto.Services;

/// <summary>
/// Coordination du suivi Git, des journées en attente, de la résolution des éléments de
/// travail, de l'envoi 7pace et des mises à jour de l'application.
///
/// L'application écrit dans 7pace et n'en lit jamais rien : une journée envoyée est close,
/// son détail local est supprimé, et 7pace fait seul autorité ensuite.
/// </summary>
internal sealed class TrackingApp : ITrackingApp
{
    /// <summary>
    /// Client dédié à 7pace, sans délai propre : SevenPaceClient borne lui-même ses écritures.
    /// </summary>
    private readonly HttpClient _sevenPaceHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly DayStore _days;
    private readonly WorkItemResolver _resolver;
    private readonly GitTracker _tracker;
    private readonly OutlookCalendarFeed _calendar;
    private readonly SevenPaceClient _sevenPace;
    private readonly IUpdateService _updates;

    /// <summary>Horloge du domaine. Injectable pour vérifier le passage de minuit.</summary>
    private readonly Func<DateTime> _now;

    /// <summary>Réglages actifs. Remplacés d'un bloc par un enregistrement, jamais modifiés en place.</summary>
    private volatile Profile _profile;

    private CancellationTokenSource? _life;

    public TrackingApp() : this(static () => DateTime.Now) { }

    internal TrackingApp(Func<DateTime> now)
    {
        _now = now;
        AppPaths.EnsureRoot();
        _profile = Profile.FromDisk();

        _days = new DayStore(() => _profile);
        _resolver = new WorkItemResolver(() => _profile.Settings.AzureOrganization);
        _calendar = new OutlookCalendarFeed(CalendarLinkStore.Read);
        _tracker = new GitTracker(_days, _resolver, () => _profile, new GitBranchReader(), now, new FileTrackerState(), _calendar);
        _sevenPace = new SevenPaceClient(_sevenPaceHttp, () => _profile.SevenPaceEndpoint);
        _updates = UpdateServiceFactory.Create(AppVersion.Current);
    }

    public Task StartAsync(CancellationToken ct)
    {
        _life = CancellationTokenSource.CreateLinkedTokenSource(ct);
        return _tracker.StartAsync(_life.Token);
    }

    public async Task<string> HandleAsync(string method, string paramsJson, CancellationToken ct)
    {
        using var document = ParseParams(paramsJson);
        var parameters = document.RootElement;

        switch (method)
        {
            case "bootstrap":
                return Bootstrap();

            case "pendingDay":
                return await PendingDayAsync(ct).ConfigureAwait(false);

            case "currentDay":
                return CurrentDay();

            case "saveEntry":
            {
                var date = Text(parameters, "date");
                if (!parameters.TryGetProperty("entry", out var raw) || raw.ValueKind != JsonValueKind.Object)
                {
                    throw new DomainException("Le créneau à enregistrer est incomplet.");
                }
                var incoming = raw.Deserialize<Entry>(Json.Wire) ?? throw new DomainException("Le créneau à enregistrer est illisible.");
                Editable(date);
                return Write(Review(date, _days.Save(date, incoming)));
            }

            case "deleteEntry":
            {
                var date = Text(parameters, "date");
                Editable(date);
                return Write(Review(date, _days.Delete(date, Number(parameters, "id"))));
            }

            case "setQuick":
            {
                if (!parameters.TryGetProperty("running", out var flag) || (flag.ValueKind != JsonValueKind.True && flag.ValueKind != JsonValueKind.False))
                {
                    throw new DomainException("État du chrono rapide manquant.");
                }
                var tracking = await _tracker.SetQuickAsync(flag.GetBoolean(), ct).ConfigureAwait(false);
                return Write(new { tracking });
            }

            case "submitDay":
                return await SubmitAsync(Text(parameters, "date"), ct).ConfigureAwait(false);

            case "discardDay":
            {
                var date = Text(parameters, "date");
                Editable(date);
                _days.Close(date);
                return Write(new
                {
                    message = $"Journée du {TimeRules.FrenchDate(TimeRules.ParseDate(date))} ignorée : rien n’a été envoyé dans 7pace.",
                    pending = _days.Pending(Today()).Count,
                });
            }

            case "loadSettings":
            {
                var profile = _profile;
                return Write(new
                {
                    settings = profile.Settings,
                    connections = Connections(),
                    configured = profile.Configured,
                    calendarConfigured = CalendarLinkStore.Configured,
                });
            }

            case "saveSettings":
                return await SaveSettingsAsync(parameters, ct).ConfigureAwait(false);

            case "saveToken":
            {
                if (!parameters.TryGetProperty("token", out var token) || token.ValueKind != JsonValueKind.String)
                {
                    throw new DomainException("Le jeton à enregistrer est manquant.");
                }
                TokenStore.Save(token.GetString());
                return Write(new { connections = Connections() });
            }

            case "saveCalendarLink":
            {
                var link = Optional(parameters, "link");
                CalendarLinkStore.Save(link);
                _calendar.Invalidate();
                var tracking = await _tracker.ReconfigureAsync(ct).ConfigureAwait(false);
                return Write(new { calendarConfigured = CalendarLinkStore.Configured, tracking });
            }

            case "probeCalendar":
                return Write(await CalendarProbeAsync(Optional(parameters, "link"), ct).ConfigureAwait(false));

            case "checkUpdate":
                return Write(Update(await CheckUpdateAsync(ct).ConfigureAwait(false)));

            case "applyUpdate":
                return await ApplyUpdateAsync(parameters, ct).ConfigureAwait(false);

            case "probeRepo":
                return Write(await Probes.RepositoryAsync(Optional(parameters, "path"), ct).ConfigureAwait(false));

            case "probeAzure":
                return Write(await Probes.AzureAsync(Optional(parameters, "organization"), ct).ConfigureAwait(false));

            case "probeToken":
                return Write(await Probes.TokenAsync(Optional(parameters, "account"), Optional(parameters, "token"), ct).ConfigureAwait(false));

            default:
                throw new DomainException($"Méthode inconnue : {method}.");
        }
    }

    private string Today() => TimeRules.DateKey(_now());

    private string Bootstrap()
    {
        var profile = _profile;
        var windows = profile.Schedule.Windows;
        return Write(new
        {
            today = Today(),
            tracking = _tracker.Current,
            connections = Connections(),
            configured = profile.Configured,
            pending = _days.Pending(Today()).Count,
            // Début des horaires : le collecteur s'en sert pour ne pas notifier en pleine nuit.
            workStart = windows.Count > 0 ? windows[0].Start : 0,
        });
    }

    /// <summary>
    /// Journée à traiter : la plus ancienne journée terminée encore en attente. Azure est
    /// relancé avant de rendre la journée, sans quoi un Fix enfant créé après le relevé
    /// obligerait à ressaisir un numéro déjà connu du dépôt.
    /// </summary>
    private async Task<string> PendingDayAsync(CancellationToken ct)
    {
        var pending = _days.Pending(Today());
        if (pending.Count == 0)
        {
            return Write(new { date = (string?)null, pending = 0, message = "Aucune journée à envoyer : tout est à jour." });
        }

        var date = pending[0];
        var azure = await RefreshAzureAsync(date, ct).ConfigureAwait(false);
        var payload = Review(date, _days.Day(date));
        return Write(new
        {
            payload.date,
            payload.entries,
            payload.holes,
            payload.overlaps,
            payload.totalMinutes,
            payload.plannedMinutes,
            payload.unassigned,
            payload.locked,
            pending = pending.Count,
            azure,
        });
    }

    /// <summary>
    /// Relance az pour les créneaux de collecte encore à attribuer dont le Bug est connu,
    /// cache ignoré : c'est le cas fréquent d'un Fix enfant créé après le relevé.
    /// </summary>
    private async Task<object> RefreshAzureAsync(string date, CancellationToken ct)
    {
        var bugs = new Dictionary<int, List<Entry>>();
        foreach (var entry in _days.Day(date))
        {
            if (entry.SentAt is not null || !entry.Unassigned) continue;
            if (entry.Bug is not int bug) continue;
            if (!bugs.TryGetValue(bug, out var list)) bugs[bug] = list = new List<Entry>();
            list.Add(entry);
        }
        if (bugs.Count == 0) return new { resolved = 0, failed = 0, message = (string?)null };

        var resolved = 0;
        var failed = 0;
        foreach (var pair in bugs)
        {
            ct.ThrowIfCancellationRequested();

            var resolution = await _resolver.ResolveBugAsync(pair.Key, force: true, ct).ConfigureAwait(false);
            if (!resolution.Resolved || resolution.WorkItem is not int item)
            {
                failed++;
                continue;
            }

            var label = string.IsNullOrWhiteSpace(resolution.Title) ? $"Fix #{item}" : resolution.Title!;
            foreach (var entry in pair.Value)
            {
                var written = _days.WriteSpan(date, entry.StartMinutes, entry.EndMinutes, label, item, pair.Key, entry.Source, entry.Id);
                if (written is not null) resolved++;
            }
        }

        return new { resolved, failed, message = AzureMessage(resolved, failed) };
    }

    private static string AzureMessage(int resolved, int failed)
    {
        var done = resolved == 0
            ? "aucune attribution trouvée"
            : $"{resolved} créneau{(resolved > 1 ? "x" : string.Empty)} attribué{(resolved > 1 ? "s" : string.Empty)}";
        return failed == 0
            ? $"Azure : {done}."
            : $"Azure : {done}, {failed} Bug{(failed > 1 ? "s" : string.Empty)} sans Fix exploitable.";
    }

    /// <summary>
    /// Consultation de la journée calendaire en cours. Diagnostic en lecture seule : rien
    /// n'est écrit, ni sur le disque ni ailleurs, et ni Azure ni 7pace ne sont appelés. La
    /// journée en cours reste non modifiable et non envoyable ; la file du matin garde la
    /// priorité, et cette vue rappelle seulement combien de journées y attendent.
    /// </summary>
    private string CurrentDay()
    {
        var date = Today();
        var review = Review(date, _days.Day(date));
        return Write(new
        {
            review.date,
            review.entries,
            review.holes,
            review.overlaps,
            review.totalMinutes,
            review.plannedMinutes,
            review.unassigned,
            readOnly = true,
            notice = "Journée en cours : consultation seule, elle se corrige et s’envoie demain matin.",
            tracking = _tracker.Current,
            health = _tracker.Health,
            pending = _days.Pending(date).Count,
        });
    }

    /// <summary>
    /// Enregistre des réglages venus de l'interface. Le suivi est réaligné tout de suite :
    /// un nouveau dépôt ou de nouveaux horaires s'appliquent sans redémarrer l'application.
    /// </summary>
    private async Task<string> SaveSettingsAsync(JsonElement parameters, CancellationToken ct)
    {
        if (!parameters.TryGetProperty("settings", out var raw) || raw.ValueKind != JsonValueKind.Object)
        {
            throw new DomainException("Les réglages à enregistrer sont manquants.");
        }

        AppSettings? incoming;
        try
        {
            incoming = raw.Deserialize<AppSettings>(Json.Wire);
        }
        catch (JsonException)
        {
            throw new DomainException("Les réglages reçus sont illisibles : vérifie les valeurs saisies.");
        }
        if (incoming is null) throw new DomainException("Les réglages reçus sont illisibles : vérifie les valeurs saisies.");

        var saved = Profile.Save(incoming);
        _profile = saved;

        var tracking = await _tracker.ReconfigureAsync(ct).ConfigureAwait(false);
        return Write(new
        {
            settings = saved.Settings,
            connections = Connections(),
            configured = saved.Configured,
            calendarConfigured = CalendarLinkStore.Configured,
            tracking,
        });
    }

    private async Task<ServiceProbe> CalendarProbeAsync(string link, CancellationToken ct)
    {
        var value = string.IsNullOrWhiteSpace(link) ? CalendarLinkStore.Read() : link.Trim();
        if (string.IsNullOrWhiteSpace(value)) return new ServiceProbe(false, "Colle le lien ICS publié par Outlook.");
        if (!CalendarLinkStore.ValidUrl(value)) return new ServiceProbe(false, "Colle le lien ICS publié par Outlook.");
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(8) };
        try
        {
            var ics = await OutlookCalendarFeed.DownloadAsync(http, value, ct).ConfigureAwait(false);
            var slots = CalendarSlots.Parse(ics, DateOnly.FromDateTime(_now()), TimeZoneInfo.Local);
            return new ServiceProbe(true, $"Calendrier accessible : {slots.Count} créneau{(slots.Count > 1 ? "x" : string.Empty)} occupé{(slots.Count > 1 ? "s" : string.Empty)} aujourd’hui.");
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or DomainException or FormatException or ArgumentException or InvalidOperationException)
        {
            return new ServiceProbe(false, "Lecture du calendrier impossible : vérifie le lien ICS Outlook.");
        }
    }

    private object Connections() => new { sevenpace = _sevenPace.State() };

    /// <summary>Une journée en cours ou close n'est ni modifiable ni envoyable.</summary>
    private void Editable(string date)
    {
        TimeRules.ParseDate(date);
        if (string.CompareOrdinal(date, Today()) >= 0)
        {
            throw new DomainException("La journée en cours est encore en collecte : elle se traite demain matin.");
        }
        if (_days.IsClosed(date))
        {
            throw new DomainException("Cette journée est close : corrige-la directement dans 7pace.");
        }
    }

    private async Task<string> SubmitAsync(string date, CancellationToken ct)
    {
        Editable(date);
        if (_tracker.QuickRunning)
        {
            throw new DomainException("Le chrono rapide tourne encore : arrête-le, attribue son créneau, puis envoie.");
        }

        var day = _days.Day(date);
        var outcome = await _sevenPace.SubmitAsync(date, day, ct).ConfigureAwait(false);
        if (outcome.Sent.Count > 0)
        {
            _days.StampSent(
                date,
                outcome.Sent.Select(item => item.EntryId).ToList(),
                DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture));
        }

        // Journée entièrement acceptée : son détail local n'a plus de raison d'exister.
        var closed = outcome.Ok && _days.Day(date).All(entry => entry.SentAt is not null);
        if (closed) _days.Close(date);

        return Write(new
        {
            ok = outcome.Ok,
            closed,
            sent = outcome.Sent.Select(item => new { workItem = item.WorkItem, seconds = item.Seconds }).ToArray(),
            message = outcome.Message,
            pending = _days.Pending(Today()).Count,
        });
    }

    /// <summary>Journée telle que l'interface la montre : créneaux, trous, chevauchements, totaux.</summary>
    private DayReview Review(string date, List<Entry> entries)
    {
        var schedule = _profile.Schedule;
        var total = 0;
        foreach (var entry in entries) total += entry.EndMinutes - entry.StartMinutes;

        return new DayReview(
            date,
            entries,
            DayStore.Holes(schedule, entries).Select(hole => new[] { hole.Start, hole.End }).ToArray(),
            DayStore.Overlaps(entries).Select(overlap => new[] { overlap.Start, overlap.End }).ToArray(),
            total,
            schedule.PlannedMinutes(),
            entries.Count(entry => entry.Unassigned),
            entries.Count(entry => entry.SentAt is not null));
    }

    /// <summary>Charge rendue à l'interface pour une journée : aucune règle n'y est cachée.</summary>
    private sealed record DayReview(
        string date,
        List<Entry> entries,
        int[][] holes,
        int[][] overlaps,
        int totalMinutes,
        int plannedMinutes,
        int unassigned,
        int locked);

    // ---------- mises à jour de l'application ----------

    private Task<UpdateInfo> CheckUpdateAsync(CancellationToken ct) =>
        _updates.CheckAsync(_profile.Settings.UpdateRepository, ct);

    /// <summary>Charge du contrat <c>checkUpdate</c> ; l'adresse de l'archive reste interne.</summary>
    private static object Update(UpdateInfo info) => new
    {
        available = info.Available,
        current = info.Current,
        latest = info.Latest,
        notes = info.Notes,
        error = info.Error,
    };

    /// <summary>
    /// Prépare la nouvelle version puis lance le programme de mise à jour. Celui-ci attend
    /// la fermeture du collecteur et de l'app demandeuse avant de toucher aux fichiers :
    /// aucun binaire n'est verrouillé pendant le remplacement, et il relance ensuite le
    /// collecteur. Le collecteur et l'app se ferment dès que le résultat est positif.
    /// </summary>
    private async Task<string> ApplyUpdateAsync(JsonElement parameters, CancellationToken ct)
    {
        var info = await CheckUpdateAsync(ct).ConfigureAwait(false);
        if (info.Error is not null) return Write(new { ok = false, message = info.Error });
        if (!info.Available || info.Latest is null)
        {
            return Write(new { ok = false, message = $"Aucune mise à jour à installer : la version {info.Current} est déjà la plus récente." });
        }

        var client = parameters.TryGetProperty("clientPid", out var pid)
            && pid.ValueKind == JsonValueKind.Number && pid.TryGetInt32(out var number) && number > 0
            ? number
            : (int?)null;
        var reopen = !parameters.TryGetProperty("reopenApp", out var flag) || flag.ValueKind != JsonValueKind.False;

        try
        {
            var staged = await _updates.StageAsync(info, ct).ConfigureAwait(false);
            _updates.LaunchUpdater(staged, client, reopen);
        }
        catch (DomainException error)
        {
            return Write(new { ok = false, message = error.Message });
        }

        return Write(new
        {
            ok = true,
            message = $"Version {info.Latest} téléchargée : le suivi s’arrête le temps de l’installer, puis redémarre tout seul.",
        });
    }

    private static JsonDocument ParseParams(string paramsJson)
    {
        if (string.IsNullOrWhiteSpace(paramsJson)) return JsonDocument.Parse("{}");
        try
        {
            var document = JsonDocument.Parse(paramsJson);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document : JsonDocument.Parse("{}");
        }
        catch (JsonException)
        {
            throw new DomainException("Requête illisible reçue depuis l’interface.");
        }
    }

    private static string Text(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new DomainException($"Paramètre « {name} » manquant.");

    /// <summary>Texte facultatif : une vérification ne doit pas échouer sur un champ vide.</summary>
    private static string Optional(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int Number(JsonElement parameters, string name)
    {
        if (parameters.TryGetProperty(name, out var value))
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        }
        throw new DomainException($"Paramètre « {name} » manquant.");
    }

    private static string Write(object payload) => JsonSerializer.Serialize(payload, Json.Wire);

    public async ValueTask DisposeAsync()
    {
        _life?.Cancel();
        await _tracker.DisposeAsync().ConfigureAwait(false);
        _calendar.Dispose();
        _life?.Dispose();
        _sevenPaceHttp.Dispose();
    }
}
