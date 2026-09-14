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
        _sevenPace = new SevenPaceClient(_http, () => _profile.SevenPaceEndpoint);
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
        if (outcome.SentEntryIds.Count > 0)
        {
            _days.StampSent(date, outcome.SentEntryIds, DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture));
        }

        return Write(new
        {
            ok = outcome.Ok,
            sent = outcome.Sent.Select(group => new { workItem = group.WorkItem, seconds = group.Seconds }).ToArray(),
            message = outcome.Message,
        });
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
    }
}
