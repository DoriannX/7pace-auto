#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SeptPaceAuto.Services;

/// <summary>
/// Le domaine complet derrière le seam : réglages, magasin des journées, suivi Git,
/// résolution des work items, agenda, envoi 7pace et mises à jour de l'application.
/// Ne connaît ni fenêtre ni WebView2.
/// </summary>
internal sealed class TrackingApp : ITrackingApp
{
    private static readonly TimeSpan PushFloor = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan FirstUpdateDelay = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>
    /// Client réservé à 7pace, sans délai propre : SevenPaceClient borne lui-même relecture
    /// et écriture. Partager le client d'Outlook coupait la relecture à 30 s alors qu'elle
    /// s'accorde 45 s, et l'échec se racontait comme un silence de 7pace.
    /// </summary>
    private readonly HttpClient _sevenPaceHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly DayStore _days;
    private readonly WorkItemResolver _resolver;
    private readonly GitTracker _tracker;
    private readonly OutlookCalendar _outlook;
    private readonly SevenPaceClient _sevenPace;
    private readonly IUpdateService _updates;

    private readonly object _pushGate = new();
    private Tracking? _lastPushed;
    private DateTime _lastPushAt;

    /// <summary>Réglages actifs. Remplacés d'un bloc par un enregistrement, jamais modifiés en place.</summary>
    private volatile Profile _profile;

    private CancellationTokenSource? _life;
    private Task? _updateWatch;
    private string? _announcedVersion;

    public event Action<string, string>? Pushed;

    public TrackingApp()
    {
        AppPaths.EnsureRoot();
        _profile = Profile.FromDisk();

        _days = new DayStore(() => _profile);
        _resolver = new WorkItemResolver(() => _profile.Settings.AzureOrganization);
        _tracker = new GitTracker(_days, _resolver, () => _profile);
        _outlook = new OutlookCalendar(_http);
        _sevenPace = new SevenPaceClient(_sevenPaceHttp, () => _profile.SevenPaceEndpoint);
        _updates = UpdateServiceFactory.Create(AppVersion.Current);

        _days.DayChanged += OnDayChanged;
        _tracker.Changed += OnTracking;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _life = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await _tracker.StartAsync(ct).ConfigureAwait(false);
        _updateWatch = Task.Run(() => WatchUpdatesAsync(_life.Token), CancellationToken.None);
    }

    public async Task<string> HandleAsync(string method, string paramsJson, CancellationToken ct)
    {
        using var document = ParseParams(paramsJson);
        var parameters = document.RootElement;

        switch (method)
        {
            case "bootstrap":
                return await BootstrapAsync(ct).ConfigureAwait(false);

            case "loadRange":
            {
                var from = Text(parameters, "from");
                var to = Text(parameters, "to");
                await SyncCalendarAsync(from, to, ct).ConfigureAwait(false);
                return Write(new { days = _days.Range(from, to) });
            }

            case "saveEntry":
            {
                var date = Text(parameters, "date");
                if (!parameters.TryGetProperty("entry", out var raw) || raw.ValueKind != JsonValueKind.Object)
                {
                    throw new DomainException("Le créneau à enregistrer est incomplet.");
                }
                var incoming = raw.Deserialize<Entry>(Json.Wire) ?? throw new DomainException("Le créneau à enregistrer est illisible.");
                var entries = _days.Save(date, incoming);
                return Write(new { date, entries });
            }

            case "deleteEntry":
            {
                var date = Text(parameters, "date");
                var entries = _days.Delete(date, Number(parameters, "id"));
                return Write(new { date, entries });
            }

            case "setPaused":
            {
                if (!parameters.TryGetProperty("paused", out var flag) || (flag.ValueKind != JsonValueKind.True && flag.ValueKind != JsonValueKind.False))
                {
                    throw new DomainException("État de pause manquant.");
                }
                var tracking = await _tracker.SetPausedAsync(flag.GetBoolean(), ct).ConfigureAwait(false);
                return Write(new { tracking });
            }

            case "submitDay":
                return await SubmitAsync(Text(parameters, "date"), ct).ConfigureAwait(false);

            case "synchronize":
                return await SynchronizeAsync(
                    Text(parameters, "from"),
                    Text(parameters, "to"),
                    parameters.TryGetProperty("apply", out var applyFlag) && applyFlag.ValueKind == JsonValueKind.True,
                    ct).ConfigureAwait(false);

            case "loadSettings":
            {
                var profile = _profile;
                return Write(new
                {
                    settings = profile.Settings,
                    connections = Connections(),
                    configured = profile.Configured,
                    onboarded = profile.Settings.Onboarded,
                });
            }

            case "saveSettings":
                return await SaveSettingsAsync(parameters, ct).ConfigureAwait(false);

            // Échelle de la vue jour : une préférence d'affichage, pas une saisie. Une
            // valeur absente ou illisible vaut « pas de zoom » au lieu d'une erreur —
            // l'utilisateur n'a rien à corriger dans un formulaire.
            case "saveDayZoom":
            {
                var requested = parameters.TryGetProperty("dayHourPx", out var zoom)
                    && zoom.ValueKind == JsonValueKind.Number
                    && zoom.TryGetInt32(out var px) ? px : 0;
                _profile = _profile.WithDayZoom(requested);
                return Write(new { dayHourPx = _profile.Settings.DayHourPx });
            }

            case "saveToken":
            {
                if (!parameters.TryGetProperty("token", out var token) || token.ValueKind != JsonValueKind.String)
                {
                    throw new DomainException("Le jeton à enregistrer est manquant.");
                }
                TokenStore.Save(token.GetString());
                return Write(new { connections = Connections() });
            }

            case "checkUpdate":
                return Write(Update(await CheckUpdateAsync(ct).ConfigureAwait(false)));

            case "applyUpdate":
                return await ApplyUpdateAsync(ct).ConfigureAwait(false);

            case "probeRepo":
                return Write(await Probes.RepositoryAsync(Optional(parameters, "path"), ct).ConfigureAwait(false));

            case "probeAzure":
                return Write(await Probes.AzureAsync(Optional(parameters, "organization"), ct).ConfigureAwait(false));

            case "probeToken":
                return Write(await Probes.TokenAsync(Optional(parameters, "account"), Optional(parameters, "token"), ct).ConfigureAwait(false));

            // La fenêtre et le sélecteur de dossier sont l'affaire de l'hôte ; si l'appel
            // arrive jusqu'ici, il n'y a rien à faire.
            case "showMini":
            case "showMain":
            case "chooseFolder":
                return "{}";

            default:
                throw new DomainException($"Méthode inconnue : {method}.");
        }
    }

    private async Task<string> BootstrapAsync(CancellationToken ct)
    {
        var today = TimeRules.DateKey(DateTime.Now);
        await SyncCalendarAsync(today, today, ct).ConfigureAwait(false);

        var profile = _profile;
        return Write(new
        {
            today,
            workWindows = profile.Schedule.Windows.Select(window => new[] { window.Start, window.End }).ToArray(),
            lunch = new[] { profile.Schedule.Lunch.Start, profile.Schedule.Lunch.End },
            tracking = _tracker.Current,
            connections = Connections(),
            fixedTasks = profile.FixedTasks,
            configured = profile.Configured,
            onboarded = profile.Settings.Onboarded,
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
            onboarded = saved.Settings.Onboarded,
            tracking,
        });
    }

    private object Connections() => new
    {
        outlook = _outlook.State(),
        sevenpace = _sevenPace.State(),
    };

    private async Task<string> SubmitAsync(string date, CancellationToken ct)
    {
        var day = _days.Day(date);
        var outcome = await _sevenPace.SubmitAsync(date, day, ct).ConfigureAwait(false);
        if (outcome.Sent.Count > 0)
        {
            _days.StampSent(
                date,
                outcome.Sent.ToDictionary(item => item.EntryId, item => item.WorkLogId),
                DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture));
        }

        return Write(new
        {
            ok = outcome.Ok,
            sent = outcome.Sent.Select(item => new { workItem = item.WorkItem, seconds = item.Seconds }).ToArray(),
            message = outcome.Message,
        });
    }

    // ---------- synchronisation avec 7pace ----------

    /// <summary>Numéro de Bug tel que le suivi Git l'écrit dans le titre d'un créneau à attribuer.</summary>
    private static readonly Regex BugInTitle = new(@"Bug #(\d{1,7})", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Une seule remise à plat à la fois : deux relectures concurrentes se marcheraient dessus.</summary>
    private int _syncing;

    /// <summary>
    /// Relit 7pace sur la plage et remet le miroir en conformité. <paramref name="apply"/> faux
    /// rend les compteurs sans rien écrire : c'est ce que l'interface montre avant de demander
    /// confirmation.
    /// </summary>
    private async Task<string> SynchronizeAsync(string from, string to, bool apply, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _syncing, 1) == 1)
        {
            return Write(new { ok = false, message = "Une synchronisation est déjà en cours." });
        }
        try
        {
            var reading = await _sevenPace.ReadAsync(from, to, ct).ConfigureAwait(false);
            if (reading.Failure is not null)
            {
                // Relecture ratée : rien n'est écrasé ni supprimé, et Azure n'est pas relancé.
                return Write(new { ok = false, message = reading.Failure });
            }

            var first = TimeRules.ParseDate(from);
            var last = TimeRules.ParseDate(to);
            if (last < first) (first, last) = (last, first);

            var byDate = new Dictionary<string, List<WorkLog>>(StringComparer.Ordinal);
            foreach (var log in reading.WorkLogs)
            {
                var day = log.StartLocal.Date;
                if (day < first || day > last) continue;
                var key = TimeRules.DateKey(day);
                if (!byDate.TryGetValue(key, out var list)) byDate[key] = list = new List<WorkLog>();
                list.Add(log);
            }

            // Les journées locales non vides comptent aussi : c'est là que se trouvent les
            // créneaux envoyés dont 7pace n'a plus trace.
            var dates = new SortedSet<string>(byDate.Keys, StringComparer.Ordinal);
            foreach (var key in _days.Range(from, to).Keys) dates.Add(key);

            var replaced = 0;
            var removed = 0;
            var imported = 0;
            var delta = 0;
            foreach (var date in dates)
            {
                var mirror = byDate.TryGetValue(date, out var logs) ? (IReadOnlyList<WorkLog>)logs : Array.Empty<WorkLog>();
                var change = _days.Reconcile(date, mirror, apply);
                replaced += change.Replaced;
                removed += change.Removed;
                imported += change.Imported;
                delta += change.SecondsDelta;
            }

            if (apply) BeginAzureRefresh(from, to);

            return Write(new
            {
                ok = true,
                replaced,
                removed,
                imported,
                secondsDelta = delta,
                message = apply ? MirrorMessage(replaced, removed, imported) : null,
            });
        }
        finally
        {
            Interlocked.Exchange(ref _syncing, 0);
        }
    }

    private static string MirrorMessage(int replaced, int removed, int imported)
    {
        if (replaced == 0 && removed == 0 && imported == 0) return "Rien à changer : le planning correspond déjà à 7pace.";

        var parts = new List<string>();
        Tally(parts, replaced, "remplacé");
        Tally(parts, removed, "supprimé");
        Tally(parts, imported, "importé");
        return $"Miroir 7pace à jour : {string.Join(", ", parts)}.";

        static void Tally(List<string> parts, int count, string adjective)
        {
            if (count == 0) return;
            var plural = count > 1 ? "s" : string.Empty;
            parts.Add(parts.Count == 0
                ? $"{count} créneau{(count > 1 ? "x" : string.Empty)} {adjective}{plural}"
                : $"{count} {adjective}{plural}");
        }
    }

    /// <summary>
    /// Relance la résolution Azure des créneaux brouillon restés à attribuer, cache ignoré.
    /// En tâche de fond : az peut prendre plusieurs minutes, le pont n'attend pas.
    /// </summary>
    private void BeginAzureRefresh(string from, string to)
    {
        var token = _life?.Token ?? CancellationToken.None;
        if (token.IsCancellationRequested) return;
        _ = Task.Run(() => RefreshAzureAsync(from, to, token), CancellationToken.None);
    }

    private async Task RefreshAzureAsync(string from, string to, CancellationToken ct)
    {
        try
        {
            var targets = new Dictionary<int, List<(string Date, Entry Entry)>>();
            foreach (var pair in _days.Range(from, to))
            {
                foreach (var entry in pair.Value)
                {
                    // Un créneau envoyé est un miroir de 7pace : Azure n'a rien à y changer.
                    if (entry.SentAt is not null) continue;
                    if (!string.Equals(entry.Source, "git", StringComparison.Ordinal) && !string.Equals(entry.Source, "gap", StringComparison.Ordinal)) continue;

                    /* Tout créneau non envoyé est revérifié, déjà attribué ou non : une
                       attribution fausse doit pouvoir être corrigée par une synchronisation,
                       sans quoi elle tient jusqu'à une reprise à la main. Le numéro vient du
                       créneau ; les créneaux écrits avant ce champ ne l'ont que dans leur
                       titre, ou plus du tout dès qu'une attribution l'a remplacé — le cache
                       de résolution sait alors de quel Bug vient l'élément de travail. */
                    var bug = entry.Bug;
                    if (bug is null)
                    {
                        var match = BugInTitle.Match(entry.Title ?? string.Empty);
                        bug = match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var found)
                            ? found
                            : entry.WorkItem is int item ? _resolver.BugOf(item) : null;
                        if (bug is null) continue;
                    }
                    if (!targets.TryGetValue(bug.Value, out var list)) targets[bug.Value] = list = new List<(string, Entry)>();
                    list.Add((pair.Key, entry));
                }
            }
            if (targets.Count == 0) return;

            var resolved = 0;
            var failed = 0;
            foreach (var pair in targets)
            {
                if (ct.IsCancellationRequested) return;

                var resolution = await _resolver.ResolveBugAsync(pair.Key, force: true, ct).ConfigureAwait(false);
                if (!resolution.Resolved || resolution.WorkItem is not int item)
                {
                    failed++;
                    continue;
                }

                var title = string.IsNullOrWhiteSpace(resolution.Title) ? $"Fix #{item}" : resolution.Title!;
                foreach (var (date, entry) in pair.Value)
                {
                    // Déjà juste : ne rien réécrire, et ne pas le compter comme une correction.
                    if (entry.WorkItem == item && string.Equals(entry.Title, title, StringComparison.Ordinal)) continue;

                    // WriteTracked plutôt que Save : le créneau reste piloté par le suivi Git,
                    // qui doit pouvoir continuer à prolonger sa fin.
                    var written = _days.WriteTracked(date, entry.StartMinutes, entry.EndMinutes, "ticket", title, item, pair.Key, entry.Source, entry.Id);
                    if (written is not null) resolved++;
                }
            }

            Pushed?.Invoke("sync", Write(new { resolved, failed, message = AzureMessage(resolved, failed) }));
        }
        catch (OperationCanceledException)
        {
            // Fermeture en cours : rien à rapporter.
        }
    }

    private static string AzureMessage(int resolved, int failed)
    {
        var done = resolved == 0
            ? "aucune attribution à corriger"
            : $"{resolved} attribution{(resolved > 1 ? "s" : string.Empty)} mise{(resolved > 1 ? "s" : string.Empty)} à jour";
        return failed == 0
            ? $"Azure : {done}."
            : $"Azure : {done}, {failed} toujours à faire.";
    }

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
    /// Prépare la nouvelle version puis lance le programme de mise à jour. La coquille ferme
    /// l'application dès que le résultat est positif : c'est lui qui remplace les fichiers.
    /// </summary>
    private async Task<string> ApplyUpdateAsync(CancellationToken ct)
    {
        var info = await CheckUpdateAsync(ct).ConfigureAwait(false);
        if (info.Error is not null) return Write(new { ok = false, message = info.Error });
        if (!info.Available || info.Latest is null)
        {
            return Write(new { ok = false, message = $"Aucune mise à jour à installer : la version {info.Current} est déjà la plus récente." });
        }

        try
        {
            var staged = await _updates.StageAsync(info, ct).ConfigureAwait(false);
            _updates.LaunchUpdater(staged);
        }
        catch (DomainException error)
        {
            return Write(new { ok = false, message = error.Message });
        }

        return Write(new { ok = true, message = $"Version {info.Latest} téléchargée : l’application se ferme pour l’installer, puis redémarre." });
    }

    /// <summary>
    /// Vérification au démarrage puis toutes les six heures. Une version plus récente est
    /// annoncée une seule fois par exécution, et rien n'est téléchargé sans demande.
    /// </summary>
    private async Task WatchUpdatesAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(FirstUpdateDelay, ct).ConfigureAwait(false);
            using var timer = new PeriodicTimer(UpdateInterval);
            do
            {
                await AnnounceUpdateAsync(ct).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Arrêt demandé.
        }
    }

    private async Task AnnounceUpdateAsync(CancellationToken ct)
    {
        if (!_profile.Settings.CheckUpdates) return;

        var info = await CheckUpdateAsync(ct).ConfigureAwait(false);
        if (!info.Available || info.Latest is null) return;
        if (string.Equals(_announcedVersion, info.Latest, StringComparison.Ordinal)) return;

        _announcedVersion = info.Latest;
        Pushed?.Invoke("update", Write(Update(info)));
    }

    /// <summary>
    /// Recopie les réunions de l'agenda en créneaux. Tant que l'approbation administrateur
    /// n'est pas accordée, l'agenda ne rend aucun évènement et cette méthode ne fait rien :
    /// aucune réunion n'est inventée.
    /// </summary>
    private async Task SyncCalendarAsync(string from, string to, CancellationToken ct)
    {
        if (!string.Equals(_outlook.State().Status, "connected", StringComparison.Ordinal)) return;

        var first = TimeRules.ParseDate(from);
        var last = TimeRules.ParseDate(to);
        if (last < first) (first, last) = (last, first);
        if ((last - first).TotalDays > 31) last = first.AddDays(31);

        for (var cursor = first; cursor <= last; cursor = cursor.AddDays(1))
        {
            foreach (var meeting in await _outlook.EventsAsync(cursor, ct).ConfigureAwait(false))
            {
                if (meeting.Start.Date != cursor.Date) continue;
                var profile = _profile;
                var activity = ActivityFor(meeting.Subject);
                _days.WriteTracked(
                    TimeRules.DateKey(cursor),
                    TimeRules.MinuteOfDay(meeting.Start),
                    meeting.End.Date == cursor.Date ? TimeRules.MinuteOfDay(meeting.End) : 24 * 60,
                    activity,
                    string.IsNullOrWhiteSpace(meeting.Subject) ? profile.Label(activity) : meeting.Subject,
                    profile.WorkItemFor(activity),
                    null,
                    "manual",
                    null);
            }
        }
    }

    /// <summary>
    /// Rattachement d'une réunion à l'une des activités récurrentes. Le numéro de tâche, lui,
    /// vient des réglages : sans tâche configurée, le créneau reste à attribuer.
    /// </summary>
    private static string ActivityFor(string subject)
    {
        var text = (subject ?? string.Empty).ToLowerInvariant();
        if (text.Contains("stand-up", StringComparison.Ordinal) || text.Contains("standup", StringComparison.Ordinal) || text.Contains("daily", StringComparison.Ordinal)) return "standup";
        if (text.Contains("rétro", StringComparison.Ordinal) || text.Contains("retro", StringComparison.Ordinal) || text.Contains("planning", StringComparison.Ordinal) || text.Contains("poker", StringComparison.Ordinal)) return "planning";
        if (text.Contains("revue de sprint", StringComparison.Ordinal) || text.Contains("sprint review", StringComparison.Ordinal) || text.Contains("démo", StringComparison.Ordinal)) return "review";
        if (text.Contains("formation", StringComparison.Ordinal)) return "training";
        return "meeting";
    }

    private void OnDayChanged(string date, List<Entry> entries) => Pushed?.Invoke("day", Write(new { date, entries }));

    /// <summary>Poussée à chaque changement d'état, et au plus une fois par demi-minute sinon.</summary>
    private void OnTracking(Tracking tracking)
    {
        var comparable = tracking with { ElapsedSeconds = 0 };
        bool push;
        lock (_pushGate)
        {
            push = _lastPushed is null || !_lastPushed.Equals(comparable) || DateTime.UtcNow - _lastPushAt >= PushFloor;
            if (push)
            {
                _lastPushed = comparable;
                _lastPushAt = DateTime.UtcNow;
            }
        }
        if (push) Pushed?.Invoke("tracking", Write(tracking));
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
        _tracker.Changed -= OnTracking;
        _days.DayChanged -= OnDayChanged;

        _life?.Cancel();
        if (_updateWatch is not null)
        {
            try
            {
                await _updateWatch.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Attendu.
            }
        }

        await _tracker.DisposeAsync().ConfigureAwait(false);
        _life?.Dispose();
        _http.Dispose();
        _sevenPaceHttp.Dispose();
    }
}
