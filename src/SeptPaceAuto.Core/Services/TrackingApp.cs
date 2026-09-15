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
/// travail, de l'envoi 7pace et des mises à jour du terminal.
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
    private readonly SevenPaceClient _sevenPace;
    private readonly IUpdateService _updates;

    /// <summary>Réglages actifs. Remplacés d'un bloc par un enregistrement, jamais modifiés en place.</summary>
    private volatile Profile _profile;

    private CancellationTokenSource? _life;

    public TrackingApp()
    {
        AppPaths.EnsureRoot();
        _profile = Profile.FromDisk();

        _days = new DayStore(() => _profile);
        _resolver = new WorkItemResolver(() => _profile.Settings.AzureOrganization);
        _tracker = new GitTracker(_days, _resolver, () => _profile);
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

            default:
                throw new DomainException($"Méthode inconnue : {method}.");
        }
    }

    private static string Today() => TimeRules.DateKey(DateTime.Now);

    private string Bootstrap()
    {
        var profile = _profile;
        return Write(new
        {
            today = Today(),
            tracking = _tracker.Current,
            connections = Connections(),
            configured = profile.Configured,
            pending = _days.Pending(Today()).Count,
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
            tracking,
        });
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
        _life?.Dispose();
        _sevenPaceHttp.Dispose();
    }
}
