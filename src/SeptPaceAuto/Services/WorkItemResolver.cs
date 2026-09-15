#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SeptPaceAuto.Services;

/// <summary>
/// Rattachement d'une branche à l'élément sur lequel imputer le temps.
/// <paramref name="Resolved"/> reste faux tant que le Fix n'est pas connu avec certitude :
/// on n'invente jamais une attribution.
/// </summary>
public sealed record Resolution(int? Bug, int? WorkItem, string? Title, bool Resolved, string? Reason);

internal sealed class WorkItemCacheRow
{
    [JsonPropertyName("bug")] public int Bug { get; set; }
    [JsonPropertyName("workItem")] public int? WorkItem { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("resolved")] public bool Resolved { get; set; }
    [JsonPropertyName("checkedAt")] public string? CheckedAt { get; set; }

    /// <summary>Échec passager (az lent, jeton en cours de renouvellement) : à retenter vite.</summary>
    [JsonPropertyName("transient")] public bool Transient { get; set; }

    /// <summary>Cause du dernier échec, conservée pour pouvoir diagnostiquer une non-attribution.</summary>
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

/// <summary>
/// Extrait le numéro de Bug/PBI de la branche puis cherche le Fix enfant via az boards.
/// az absent, lent ou en échec : le créneau reste « à attribuer », jamais une supposition.
/// </summary>
public sealed class WorkItemResolver
{
    private static readonly Regex Number = new(@"(?<!\d)(\d{4,6})(?!\d)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Délai d'un appel az isolé. Mesuré sur ce poste : 2,5 s à chaud, mais le premier appel
    /// après le démarrage rafraîchit le jeton et dépasse la minute — à 20 s, la toute première
    /// résolution de la journée échouait alors qu'az allait répondre.
    /// </summary>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Temps total accordé à une résolution : le parent, chaque enfant et le compte connecté
    /// font autant d'appels az. La résolution tourne en fond, elle ne retient jamais le chrono.
    /// </summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RetryAfterHiccup = TimeSpan.FromMinutes(2);
    private const string ChildLink = "System.LinkTypes.Hierarchy-Forward";

    /// <summary>
    /// Types d'enfants sur lesquels le temps s'impute réellement : le Fix quand il existe,
    /// sinon l'unique tâche enfant.
    /// </summary>
    private static readonly HashSet<string> Imputable = new(StringComparer.OrdinalIgnoreCase) { "Task", "Tâche", "Tache" };

    private readonly object _gate = new();
    private readonly Dictionary<int, WorkItemCacheRow> _cache = new();
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly Func<string> _organization;
    private readonly string _cachePath;

    // Lu sous _oneAtATime : une seule résolution interroge az à la fois.
    private string? _identity;
    private bool _identityRead;
    private string? _lastAzError;

    /// <param name="organization">Organisation Azure DevOps réglée par l'utilisateur, relue à chaque appel.</param>
    public WorkItemResolver(Func<string> organization) : this(organization, AppPaths.WorkItems) { }

    public WorkItemResolver(Func<string> organization, string cachePath)
    {
        _organization = organization;
        _cachePath = cachePath;
        var stored = AppPaths.ReadJson<Dictionary<string, WorkItemCacheRow>>(_cachePath);
        if (stored is null) return;
        foreach (var pair in stored)
        {
            if (pair.Value is null) continue;
            if (int.TryParse(pair.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bug)) _cache[bug] = pair.Value;
        }
    }

    public static int? ExtractBug(string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch)) return null;
        var match = Number.Match(branch);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bug) ? bug : null;
    }

    /// <summary>
    /// Réponse déjà acquise, sans lancer az. Rend faux quand seule une interrogation
    /// d'Azure DevOps pourrait répondre : le relevé de branche ne doit jamais attendre.
    /// </summary>
    public bool TryCached(string? branch, out Resolution resolution)
    {
        var bug = ExtractBug(branch);
        resolution = new Resolution(bug, null, null, false, bug is null ? "Aucun numéro de Bug ou de PBI dans le nom de la branche." : null);
        if (bug is null) return true;

        // Sans organisation réglée, aucune interrogation n'est possible : le créneau reste
        // « à attribuer » et rien n'est lancé en fond pour autant.
        if (Organization() is null)
        {
            resolution = new Resolution(bug, null, null, false, "L’organisation Azure DevOps n’est pas renseignée dans les réglages.");
            return true;
        }

        lock (_gate)
        {
            if (_cache.TryGetValue(bug.Value, out var row) && row.Resolved && row.WorkItem is int item)
            {
                resolution = new Resolution(bug, item, row.Title, true, null);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Bug d'où vient une attribution déjà posée, retrouvé dans le cache. Sert aux créneaux
    /// écrits avant que le numéro ne soit conservé sur le créneau lui-même : sans lui, une
    /// attribution fausse n'aurait plus aucun point d'entrée pour être revérifiée.
    /// </summary>
    public int? BugOf(int workItem)
    {
        lock (_gate)
        {
            foreach (var pair in _cache)
            {
                if (pair.Value.Resolved && pair.Value.WorkItem == workItem) return pair.Key;
            }
        }
        return null;
    }

    /// <summary>
    /// Réponse immédiate depuis le cache ; sinon une seule interrogation az à la fois,
    /// bornée par le budget ci-dessus, et un échec n'est retenté qu'au bout de dix minutes.
    /// </summary>
    public async Task<Resolution> ResolveAsync(string? branch, CancellationToken ct)
    {
        var bug = ExtractBug(branch);
        if (bug is null) return new Resolution(null, null, null, false, "Aucun numéro de Bug ou de PBI dans le nom de la branche.");
        return await ResolveBugAsync(bug.Value, force: false, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Résolution d'un numéro connu. <paramref name="force"/> ignore le cache — y compris une
    /// réponse déjà acquise : un Fix rattaché après coup doit pouvoir la remplacer sans
    /// attendre la fenêtre de retente ni un redémarrage.
    /// </summary>
    public async Task<Resolution> ResolveBugAsync(int bug, bool force, CancellationToken ct)
    {
        if (Organization() is null)
        {
            return new Resolution(bug, null, null, false, "L’organisation Azure DevOps n’est pas renseignée dans les réglages.");
        }

        if (!force && TryCache(bug, out var cached)) return cached;

        var az = ProcessRunner.Az;
        if (az is null)
        {
            Remember(bug, null, null, null, resolved: false);
            return new Resolution(bug, null, null, false, "Azure CLI (az) est introuvable sur ce poste.");
        }

        // Une synchronisation manuelle a le droit de faire la queue derrière le suivi Git ;
        // le relevé automatique, lui, ne doit jamais attendre.
        var patience = force ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(1);
        if (!await _oneAtATime.WaitAsync(patience, ct).ConfigureAwait(false))
        {
            return new Resolution(bug, null, null, false, "Résolution déjà en cours.");
        }
        try
        {
            if (!force && TryCache(bug, out cached)) return cached;

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(Budget);
            try
            {
                return await LookupAsync(az, bug, deadline.Token, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                const string reason = "Azure DevOps n’a pas répondu dans le temps imparti.";
                Remember(bug, null, null, null, resolved: false, transient: true, reason: reason);
                return new Resolution(bug, null, null, false, reason);
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException)
            {
                const string reason = "Réponse d’Azure DevOps inexploitable.";
                Remember(bug, null, null, null, resolved: false, transient: true, reason: reason);
                return new Resolution(bug, null, null, false, reason);
            }
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    private async Task<Resolution> LookupAsync(string az, int bug, CancellationToken ct, CancellationToken shutdown)
    {
        var parent = await ShowAsync(az, bug, ct).ConfigureAwait(false);
        if (parent is null)
        {
            return Failed(bug, shutdown, ct, $"Le work item #{bug} n’a pas pu être lu : {_lastAzError ?? "az n’a rien renvoyé"}.", transient: true);
        }

        var candidates = new List<(int Id, string Type, string? Title, string? Assignee)>();
        var unread = 0;
        using (parent)
        {
            foreach (var child in Children(parent.RootElement))
            {
                ct.ThrowIfCancellationRequested();
                using var detail = await ShowAsync(az, child, ct).ConfigureAwait(false);
                if (detail is null)
                {
                    unread++;
                    continue;
                }
                var (type, title, assignee) = Fields(detail.RootElement);
                if (type is null)
                {
                    unread++;
                    continue;
                }

                // Un Fix tranche tout de suite : c'est l'élément d'imputation par convention.
                if (string.Equals(type, "Fix", StringComparison.OrdinalIgnoreCase))
                {
                    Remember(bug, child, title, type, resolved: true);
                    return new Resolution(bug, child, title, true, null);
                }
                candidates.Add((child, type, title, assignee));
            }
        }

        // Sans Fix, l'équipe impute sur la tâche enfant — mais seulement s'il n'y a pas de
        // doute : plusieurs tâches, c'est à l'utilisateur de choisir, pas à l'application.
        var tasks = candidates.Where(candidate => Imputable.Contains(candidate.Type)).ToList();

        /* Un parent partagé (Roadmap, PBI) porte souvent une tâche par développeur. Le compte
           connecté à az départage — y compris quand une seule tâche a pu être lue : une tâche
           affectée à quelqu'un d'autre n'est jamais la nôtre, et l'imputer silencieusement
           faisait apparaître le titre d'un collègue sur les créneaux de la journée. */
        var identity = tasks.Count > 0 ? await IdentityAsync(az, ct).ConfigureAwait(false) : null;
        if (identity is not null)
        {
            var mine = tasks
                .Where(candidate => string.Equals(candidate.Assignee, identity, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (mine.Count == 1)
            {
                Remember(bug, mine[0].Id, mine[0].Title, mine[0].Type, resolved: true);
                return new Resolution(bug, mine[0].Id, mine[0].Title, true, null);
            }
            if (mine.Count == 0)
            {
                var foreign = tasks.Where(candidate => !string.IsNullOrWhiteSpace(candidate.Assignee)).ToList();
                if (foreign.Count == tasks.Count && tasks.Count > 0)
                {
                    return Failed(bug, shutdown, ct, $"Aucune tâche enfant de #{bug} n’est affectée à {identity} : l’attribution reste à faire à la main.");
                }
                tasks = tasks.Where(candidate => string.IsNullOrWhiteSpace(candidate.Assignee)).ToList();
            }
            else
            {
                tasks = mine;
            }
        }

        // Une réponse partielle n'autorise aucune conclusion : la tâche manquante peut être
        // celle qu'il fallait choisir. C'est passager, donc retenté deux minutes plus tard.
        if (unread > 0)
        {
            return Failed(bug, shutdown, ct, $"{unread} tâche{(unread > 1 ? "s" : string.Empty)} enfant de #{bug} n’a pas pu être lue : {_lastAzError ?? "az n’a rien renvoyé"}.", transient: true);
        }

        if (tasks.Count == 1)
        {
            Remember(bug, tasks[0].Id, tasks[0].Title, tasks[0].Type, resolved: true);
            return new Resolution(bug, tasks[0].Id, tasks[0].Title, true, null);
        }

        return Failed(bug, shutdown, ct, tasks.Count > 1
            ? $"Plusieurs tâches enfants sous #{bug} pour le même compte : l’attribution reste à faire à la main."
            : $"Aucun Fix ni tâche enfant sous #{bug}.");
    }

    /// <summary>
    /// Échec de résolution : le numéro de Bug est conservé, rien n'est deviné. L'échec est
    /// mis en cache dix minutes — deux minutes seulement s'il est passager (az lent ou muet) —
    /// sauf si c'est l'arrêt de l'application qui l'a provoqué.
    /// </summary>
    private Resolution Failed(int bug, CancellationToken shutdown, CancellationToken deadline, string reason, bool transient = false)
    {
        if (shutdown.IsCancellationRequested) return new Resolution(bug, null, null, false, "Résolution interrompue.");
        var message = deadline.IsCancellationRequested ? "Azure DevOps n’a pas répondu dans le temps imparti." : reason;
        Remember(bug, null, null, null, resolved: false, transient: transient || deadline.IsCancellationRequested, reason: message);
        return new Resolution(bug, null, null, false, message);
    }

    /// <summary>Organisation réglée, ou null quand elle est vide : aucune interrogation n'est alors tentée.</summary>
    private string? Organization()
    {
        var organization = _organization();
        return string.IsNullOrWhiteSpace(organization) ? null : organization;
    }

    private async Task<JsonDocument?> ShowAsync(string az, int id, CancellationToken ct)
    {
        var organization = Organization();
        if (organization is null) return null;

        var result = await ProcessRunner.RunAsync(
            az,
            new[] { "boards", "work-item", "show", "--id", id.ToString(CultureInfo.InvariantCulture), "--organization", organization, "--output", "json" },
            CallTimeout,
            ct).ConfigureAwait(false);

        if (!result.Ok || string.IsNullOrWhiteSpace(result.StdOut))
        {
            _lastAzError = Detail(result);
            return null;
        }
        try
        {
            return JsonDocument.Parse(result.StdOut);
        }
        catch (JsonException)
        {
            _lastAzError = "réponse JSON illisible";
            return null;
        }
    }

    /// <summary>Résumé lisible d'un appel az manqué : de quoi comprendre sans relancer la commande.</summary>
    private static string Detail(ProcessResult result)
    {
        if (!result.Started) return "az n’a pas pu être lancé";
        if (result.TimedOut) return "az a dépassé le délai";
        var error = result.StdErr.Trim().Replace('\r', ' ').Replace('\n', ' ');
        if (error.Length > 200) error = error[..200];
        return error.Length > 0 ? $"code {result.ExitCode} — {error}" : $"code {result.ExitCode}, sortie vide";
    }

    private static IEnumerable<int> Children(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("relations", out var relations) || relations.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }
        foreach (var relation in relations.EnumerateArray())
        {
            if (relation.ValueKind != JsonValueKind.Object) continue;
            if (!relation.TryGetProperty("rel", out var rel) || rel.ValueKind != JsonValueKind.String) continue;
            if (!string.Equals(rel.GetString(), ChildLink, StringComparison.OrdinalIgnoreCase)) continue;
            if (!relation.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String) continue;
            var id = TrailingId(url.GetString());
            if (id is int value) yield return value;
        }
    }

    private static int? TrailingId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var last = url.TrimEnd('/');
        var slash = last.LastIndexOf('/');
        var tail = slash >= 0 ? last[(slash + 1)..] : last;
        return int.TryParse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null;
    }

    private static (string? Type, string? Title, string? Assignee) Fields(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
        {
            return (null, null, null);
        }
        string? Read(string name) => fields.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return (Read("System.WorkItemType"), Read("System.Title"), Assignee(fields));
    }

    /// <summary>Compte de la personne affectée, tel qu'az le renvoie : l'adresse de connexion.</summary>
    private static string? Assignee(JsonElement fields)
    {
        if (!fields.TryGetProperty("System.AssignedTo", out var assigned) || assigned.ValueKind != JsonValueKind.Object) return null;
        return assigned.TryGetProperty("uniqueName", out var unique) && unique.ValueKind == JsonValueKind.String ? unique.GetString() : null;
    }

    /// <summary>
    /// Compte connecté à az, lu une seule fois par session. Introuvable : aucune tâche n'est
    /// choisie à sa place, l'attribution reste à faire à la main.
    /// </summary>
    private async Task<string?> IdentityAsync(string az, CancellationToken ct)
    {
        if (_identityRead) return _identity;

        var result = await ProcessRunner.RunAsync(
            az,
            new[] { "account", "show", "--query", "user.name", "--output", "tsv" },
            CallTimeout,
            ct).ConfigureAwait(false);
        var name = result.Ok ? result.StdOut.Trim() : null;
        _identity = string.IsNullOrWhiteSpace(name) ? null : name;
        _identityRead = true;
        return _identity;
    }

    private bool TryCache(int bug, out Resolution resolution)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(bug, out var row))
            {
                if (row.Resolved && row.WorkItem is int item)
                {
                    resolution = new Resolution(bug, item, row.Title, true, null);
                    return true;
                }
                if (Fresh(row))
                {
                    resolution = new Resolution(bug, null, null, false, row.Reason ?? "Attribution non résolue lors de la dernière tentative.");
                    return true;
                }
            }
        }
        resolution = new Resolution(bug, null, null, false, null);
        return false;
    }

    private static bool Fresh(WorkItemCacheRow row) =>
        DateTimeOffset.TryParse(row.CheckedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var moment)
        && DateTimeOffset.Now - moment < (row.Transient ? RetryAfterHiccup : RetryAfterFailure);

    private void Remember(int bug, int? workItem, string? title, string? type, bool resolved, bool transient = false, string? reason = null)
    {
        Dictionary<string, WorkItemCacheRow> payload;
        lock (_gate)
        {
            _cache[bug] = new WorkItemCacheRow
            {
                Bug = bug,
                WorkItem = workItem,
                Title = title,
                Type = type,
                Resolved = resolved,
                Transient = transient,
                Reason = reason,
                CheckedAt = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
            };
            payload = new Dictionary<string, WorkItemCacheRow>(_cache.Count, StringComparer.Ordinal);
            foreach (var pair in _cache) payload[pair.Key.ToString(CultureInfo.InvariantCulture)] = pair.Value;
        }
        try
        {
            AppPaths.EnsureRoot();
            AppPaths.WriteAtomic(_cachePath, JsonSerializer.Serialize(payload, Json.Pretty));
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            // Le cache mémoire suffit pour cette session.
        }
    }
}
