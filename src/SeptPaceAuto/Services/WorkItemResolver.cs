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
}

/// <summary>
/// Extrait le numéro de Bug/PBI de la branche puis cherche le Fix enfant via az boards.
/// az absent, lent ou en échec : le créneau reste « à attribuer », jamais une supposition.
/// </summary>
public sealed class WorkItemResolver
{
    private static readonly Regex Number = new(@"(?<!\d)(\d{4,6})(?!\d)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);
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
    /// Réponse immédiate depuis le cache ; sinon une seule interrogation az à la fois,
    /// bornée à 20 s au total, et un échec n'est retenté qu'au bout de dix minutes.
    /// </summary>
    public async Task<Resolution> ResolveAsync(string? branch, CancellationToken ct)
    {
        var bug = ExtractBug(branch);
        if (bug is null) return new Resolution(null, null, null, false, "Aucun numéro de Bug ou de PBI dans le nom de la branche.");

        if (Organization() is null)
        {
            return new Resolution(bug, null, null, false, "L’organisation Azure DevOps n’est pas renseignée dans les réglages.");
        }

        if (TryCache(bug.Value, out var cached)) return cached;

        var az = ProcessRunner.Az;
        if (az is null)
        {
            Remember(bug.Value, null, null, null, resolved: false);
            return new Resolution(bug, null, null, false, "Azure CLI (az) est introuvable sur ce poste.");
        }

        if (!await _oneAtATime.WaitAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false))
        {
            return new Resolution(bug, null, null, false, "Résolution déjà en cours.");
        }
        try
        {
            if (TryCache(bug.Value, out cached)) return cached;

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(Budget);
            try
            {
                return await LookupAsync(az, bug.Value, deadline.Token, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Remember(bug.Value, null, null, null, resolved: false, transient: true);
                return new Resolution(bug, null, null, false, "Azure DevOps n’a pas répondu en 20 secondes.");
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException)
            {
                Remember(bug.Value, null, null, null, resolved: false, transient: true);
                return new Resolution(bug, null, null, false, "Réponse d’Azure DevOps inexploitable.");
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
            return Failed(bug, shutdown, ct, $"Le work item #{bug} n’a pas pu être lu.", transient: true);
        }

        var candidates = new List<(int Id, string Type, string? Title)>();
        using (parent)
        {
            foreach (var child in Children(parent.RootElement))
            {
                ct.ThrowIfCancellationRequested();
                using var detail = await ShowAsync(az, child, ct).ConfigureAwait(false);
                if (detail is null) continue;
                var (type, title) = Fields(detail.RootElement);
                if (type is null) continue;

                // Un Fix tranche tout de suite : c'est l'élément d'imputation par convention.
                if (string.Equals(type, "Fix", StringComparison.OrdinalIgnoreCase))
                {
                    Remember(bug, child, title, type, resolved: true);
                    return new Resolution(bug, child, title, true, null);
                }
                candidates.Add((child, type, title));
            }
        }

        // Sans Fix, l'équipe impute sur la tâche enfant — mais seulement s'il n'y a pas de
        // doute : plusieurs tâches, c'est à l'utilisateur de choisir, pas à l'application.
        var tasks = candidates.Where(candidate => Imputable.Contains(candidate.Type)).ToList();
        if (tasks.Count == 1)
        {
            Remember(bug, tasks[0].Id, tasks[0].Title, tasks[0].Type, resolved: true);
            return new Resolution(bug, tasks[0].Id, tasks[0].Title, true, null);
        }

        return Failed(bug, shutdown, ct, tasks.Count > 1
            ? $"Plusieurs tâches enfants sous #{bug} : l’attribution reste à faire à la main."
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
        Remember(bug, null, null, null, resolved: false, transient: transient || deadline.IsCancellationRequested);
        return new Resolution(bug, null, null, false, deadline.IsCancellationRequested
            ? "Azure DevOps n’a pas répondu en 20 secondes."
            : reason);
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
            Budget,
            ct).ConfigureAwait(false);

        if (!result.Ok || string.IsNullOrWhiteSpace(result.StdOut)) return null;
        try
        {
            return JsonDocument.Parse(result.StdOut);
        }
        catch (JsonException)
        {
            return null;
        }
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

    private static (string? Type, string? Title) Fields(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }
        string? Read(string name) => fields.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return (Read("System.WorkItemType"), Read("System.Title"));
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
                    resolution = new Resolution(bug, null, null, false, "Attribution non résolue lors de la dernière tentative.");
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

    private void Remember(int bug, int? workItem, string? title, string? type, bool resolved, bool transient = false)
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
