#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SeptPaceAuto.Services;

/// <summary>Un créneau réellement accepté par 7pace, avec l'identifiant que 7pace lui a donné.</summary>
public sealed record SentEntry(int EntryId, int WorkItem, int Seconds, string? WorkLogId);

/// <summary>
/// Résultat d'un envoi. <paramref name="Sent"/> ne contient que les créneaux dont
/// l'écriture a réellement été acceptée par 7pace.
/// </summary>
public sealed record SubmitOutcome(bool Ok, IReadOnlyList<SentEntry> Sent, string Message);

/// <summary>Un worklog tel que 7pace le rend, l'heure de début déjà ramenée au fuseau local.</summary>
public sealed record WorkLog(string Id, DateTime StartLocal, int Seconds, int WorkItem);

/// <summary>Lecture d'une plage. <paramref name="Failure"/> non nul = rien n'est exploitable.</summary>
public sealed record ReadOutcome(IReadOnlyList<WorkLog> WorkLogs, string? Failure);

/// <summary>
/// Échanges avec 7pace. L'authentification est vérifiée mais les droits d'écriture ne sont
/// pas prouvés : un refus est rapporté comme un échec, jamais avalé. L'envoi écrit un worklog
/// par créneau — c'est ce qui permet à la relecture de rester fidèle au découpage horaire.
/// </summary>
public sealed class SevenPaceClient
{
    /// <summary>Taille de page maximale admise par l'API v3.2.</summary>
    private const int PageSize = 500;

    /// <summary>Au-delà, la plage affichée est déraisonnable : mieux vaut le dire que boucler.</summary>
    private const int MaxPages = 20;

    /// <summary>
    /// Budget de la relecture entière, pagination comprise. 15 s ne suffisaient pas : une
    /// plage d'un mois demande plusieurs pages et 7pace répond lentement au premier appel,
    /// si bien qu'une synchronisation normale échouait sur notre propre délai. Le pont JS
    /// accorde plus de temps à cet appel (voir LONG_CALL dans web/app.js).
    /// </summary>
    private static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(45);

    private static readonly int[] Backoff = { 5, 15, 30 };

    private readonly HttpClient _http;
    private readonly Func<string?> _endpoint;

    /// <param name="endpoint">Adresse dérivée du compte réglé, nulle tant qu'il est vide.</param>
    public SevenPaceClient(HttpClient http, Func<string?> endpoint)
    {
        _http = http;
        _endpoint = endpoint;
    }

    public ConnectionState State()
    {
        if (_endpoint() is null) return new ConnectionState("not-configured", "7pace · compte non renseigné, ouvre les réglages");
        return TokenStore.Read() is null
            ? new ConnectionState("no-token", "7pace · jeton absent, colle-le dans les réglages")
            : new ConnectionState("ready", "7pace · authentifié, droits d’écriture non prouvés");
    }

    public async Task<SubmitOutcome> SubmitAsync(string date, IReadOnlyList<Entry> day, CancellationToken ct)
    {
        var readable = TimeRules.FrenchDate(TimeRules.ParseDate(date));

        var endpoint = _endpoint();
        if (endpoint is null)
        {
            return Refused($"Le compte 7pace n’est pas renseigné dans les réglages : rien n’a été envoyé pour le {readable}.");
        }

        var token = TokenStore.Read();
        if (token is null)
        {
            return Refused($"Aucun jeton 7pace utilisable : enregistre-le dans les réglages puis recommence. Rien n’a été envoyé pour le {readable}.");
        }

        var billable = day.Where(entry => !entry.Excluded).ToList();
        if (billable.Count == 0)
        {
            return Refused($"Aucun temps à envoyer pour le {readable} : rien n’a été envoyé.");
        }

        var unassigned = billable.FirstOrDefault(entry => entry.Unassigned);
        if (unassigned is not null)
        {
            return Refused($"Le créneau de {unassigned.Start} à {unassigned.End} n’est pas attribué : rien n’a été envoyé.");
        }

        // Un créneau déjà accepté par 7pace n'est jamais renvoyé : pas de double comptage,
        // et un envoi partiellement refusé peut être terminé plus tard.
        var pending = billable
            .Where(entry => entry.SentAt is null && entry.Id is not null)
            .OrderBy(entry => entry.StartMinutes)
            .ToList();
        if (pending.Count == 0)
        {
            return Refused($"Le {readable} est déjà envoyé dans 7pace : rien n’a été renvoyé, pour ne pas compter le temps deux fois.");
        }

        var sent = new List<SentEntry>();
        var failures = new List<string>();
        var day0 = TimeRules.ParseDate(date);

        // Un worklog par créneau : le regroupement par élément de travail écraserait le
        // découpage horaire, que la relecture doit pouvoir retrouver à l'identique.
        foreach (var entry in pending)
        {
            var seconds = (entry.EndMinutes - entry.StartMinutes) * 60;
            if (seconds <= 0) continue;

            var timestamp = Stamp(day0.AddMinutes(entry.StartMinutes));
            var (failure, workLogId) = await PostAsync(endpoint, token, timestamp, seconds, entry.WorkItem!.Value, ct).ConfigureAwait(false);
            if (failure is null)
            {
                sent.Add(new SentEntry(entry.Id!.Value, entry.WorkItem!.Value, seconds, workLogId));
            }
            else
            {
                failures.Add($"{entry.Start}–{entry.End} ({failure})");
            }
        }

        if (failures.Count == 0)
        {
            var total = TimeRules.Readable(sent.Sum(item => item.Seconds) / 60);
            var count = sent.Count;
            return new SubmitOutcome(true, sent, $"{total} envoyé dans 7pace pour le {readable}, sur {count} créneau{(count > 1 ? "x" : string.Empty)}.");
        }

        var detail = string.Join(", ", failures);
        var message = sent.Count == 0
            ? $"7pace a refusé l’envoi du {readable} : {detail}. Aucun temps n’a été marqué comme envoyé — les droits d’écriture ne sont pas prouvés."
            : $"Envoi partiel du {readable} : {sent.Count} créneau(x) acceptés, refus sur {detail}. Les créneaux refusés ne sont pas marqués comme envoyés.";
        return new SubmitOutcome(false, sent, message);
    }

    /// <summary>
    /// Relit les worklogs de la plage, bornes incluses. Les filtres 7pace sont stricts :
    /// les bornes sont donc élargies d'une seconde d'un côté et d'un jour de l'autre.
    /// </summary>
    public async Task<ReadOutcome> ReadAsync(string from, string to, CancellationToken ct)
    {
        var endpoint = _endpoint();
        if (endpoint is null) return Unreadable("Le compte 7pace n’est pas renseigné dans les réglages.");

        var token = TokenStore.Read();
        if (token is null) return Unreadable("Aucun jeton 7pace utilisable : enregistre-le dans les réglages.");

        var first = TimeRules.ParseDate(from);
        var last = TimeRules.ParseDate(to);
        if (last < first) (first, last) = (last, first);
        var after = Uri.EscapeDataString(Stamp(first.AddSeconds(-1)));
        var before = Uri.EscapeDataString(Stamp(last.AddDays(1)));

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(ReadBudget);

        var worklogs = new List<WorkLog>();
        var skip = 0;

        for (var page = 0; page < MaxPages; page++)
        {
            var address = string.Concat(
                endpoint,
                "&$fromTimestamp=", after,
                "&$toTimestamp=", before,
                "&$count=", PageSize.ToString(CultureInfo.InvariantCulture),
                "&$skip=", skip.ToString(CultureInfo.InvariantCulture));

            var (reply, failure, cut) = await SendAsync(() => Signed(new HttpRequestMessage(HttpMethod.Get, address), token), budget.Token).ConfigureAwait(false);
            if (failure is not null)
            {
                /* Un échec nommé — quota, réseau, refus — garde son nom : seule une coupure
                   de notre propre budget se raconte comme un délai dépassé. */
                var expired = cut ? Expired(budget, ct) : null;
                return Unreadable(expired ?? $"Relecture 7pace impossible : {failure}.");
            }

            using var response = reply!;
            if (!response.IsSuccessStatusCode)
            {
                return Unreadable($"Relecture 7pace impossible : {Explain(response)}.");
            }

            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Unreadable(Expired(budget, ct) ?? "Relecture 7pace impossible : lecture interrompue.");
            }

            if (!TryHarvest(body, worklogs, out var read)) return Unreadable("Réponse de 7pace inexploitable.");
            if (read < PageSize) return new ReadOutcome(worklogs, null);
            skip += read;
        }

        return Unreadable("Trop de worklogs sur cette période pour être relus en une fois : réduis la plage affichée.");
    }

    /// <summary>Failure null = 7pace a accepté ; l'identifiant peut manquer sans que ce soit un échec.</summary>
    private async Task<(string? Failure, string? WorkLogId)> PostAsync(string endpoint, string token, string timestamp, int seconds, int workItem, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { timestamp, length = seconds, workItemId = workItem }, Json.Wire);

        var (reply, failure, _) = await SendAsync(
            () => Signed(
                new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(payload, new UTF8Encoding(false), "application/json"),
                },
                token),
            ct).ConfigureAwait(false);
        if (failure is not null) return (failure, null);

        using var response = reply!;
        if (!response.IsSuccessStatusCode) return (Explain(response), null);

        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return (null, WorkLogIdOf(body));
        }
        catch (OperationCanceledException)
        {
            // L'écriture est passée : ne pas la rapporter comme un échec parce que la
            // lecture de la réponse a été coupée.
            return (null, null);
        }
    }

    /// <summary>
    /// Envoi avec patience sur le quota 7pace. Response non nulle quand Failure est nul.
    /// <c>Cut</c> vrai signale une annulation pendant l'appel lui-même : c'est le seul cas
    /// que l'appelant peut présenter comme un délai dépassé.
    /// </summary>
    private async Task<(HttpResponseMessage? Response, string? Failure, bool Cut)> SendAsync(Func<HttpRequestMessage> build, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var request = build();
                response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return (null, "aucune réponse dans le délai imparti", true);
            }
            catch (HttpRequestException error)
            {
                return (null, $"réseau indisponible ({error.Message})", false);
            }

            if (response.StatusCode != HttpStatusCode.TooManyRequests) return (response, null, false);

            var pause = Pause(response, Backoff[Math.Min(attempt, Backoff.Length - 1)]);
            response.Dispose();
            if (attempt >= Backoff.Length - 1)
            {
                return (null, "HTTP 429, quota 7pace atteint : réessaie dans quelques minutes", false);
            }

            try
            {
                await Task.Delay(pause, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                /* Coupé pendant l'attente imposée par 7pace : c'est le quota qui a bloqué,
                   pas un silence du service. Le dire tel quel, sinon le message envoie
                   chercher une panne réseau qui n'existe pas. */
                return (null, "HTTP 429, quota 7pace atteint : réessaie dans quelques minutes", false);
            }
        }
    }

    private static HttpRequestMessage Signed(HttpRequestMessage request, string token)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    /// <summary>Horodatage ISO 8601 en UTC, seule forme que l'API v3.2 documente.</summary>
    private static string Stamp(DateTime local) =>
        DateTime.SpecifyKind(local, DateTimeKind.Local)
            .ToUniversalTime()
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static TimeSpan Pause(HttpResponseMessage response, int fallbackSeconds)
    {
        var asked = response.Headers.RetryAfter?.Delta;
        return asked is { } delta && delta > TimeSpan.Zero && delta <= TimeSpan.FromSeconds(60)
            ? delta
            : TimeSpan.FromSeconds(fallbackSeconds);
    }

    /// <summary>Message dédié quand c'est notre propre budget qui a coupé, pas l'appelant.</summary>
    private static string? Expired(CancellationTokenSource budget, CancellationToken ct) =>
        budget.IsCancellationRequested && !ct.IsCancellationRequested
            ? $"7pace n’a pas répondu en {ReadBudget.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s : rien n’a été modifié. Réessaie, ou réduis la plage affichée avant de synchroniser."
            : null;

    private static string Explain(HttpResponseMessage response) => response.StatusCode switch
    {
        HttpStatusCode.Unauthorized => "HTTP 401, jeton refusé : enregistre un nouveau jeton dans les réglages",
        HttpStatusCode.Forbidden => "HTTP 403, droits d’écriture refusés",
        _ => $"HTTP {((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)}",
    };

    /// <summary>Identifiant du worklog créé, absent si 7pace ne rend pas son objet.</summary>
    private static string? WorkLogIdOf(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object) root = data;
            return root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>False = enveloppe inconnue. <paramref name="read"/> compte les lignes vues, pas les lignes retenues.</summary>
    private static bool TryHarvest(string body, List<WorkLog> into, out int read)
    {
        read = 0;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            if (!TryRows(document.RootElement, out var rows)) return false;
            foreach (var row in rows.EnumerateArray())
            {
                read++;
                // Un worklog sans rattachement à un élément de travail n'a rien à refléter
                // dans un planning organisé par ticket : il est ignoré.
                if (TryWorkLog(row, out var log)) into.Add(log);
            }
            return true;
        }
    }

    private static bool TryRows(JsonElement root, out JsonElement rows)
    {
        rows = default;
        if (root.ValueKind == JsonValueKind.Array)
        {
            rows = root;
            return true;
        }
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (!root.TryGetProperty("data", out var data)) return false;
        if (data.ValueKind == JsonValueKind.Array)
        {
            rows = data;
            return true;
        }
        if (data.ValueKind != JsonValueKind.Object) return false;
        foreach (var name in new[] { "workLogs", "value", "data" })
        {
            if (data.TryGetProperty(name, out var nested) && nested.ValueKind == JsonValueKind.Array)
            {
                rows = nested;
                return true;
            }
        }
        return false;
    }

    private static bool TryWorkLog(JsonElement row, out WorkLog log)
    {
        log = null!;
        if (row.ValueKind != JsonValueKind.Object) return false;
        if (!row.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) return false;
        if (!row.TryGetProperty("timestamp", out var stamp) || stamp.ValueKind != JsonValueKind.String) return false;
        if (!DateTimeOffset.TryParse(stamp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var moment)) return false;
        if (!row.TryGetProperty("length", out var length) || length.ValueKind != JsonValueKind.Number || !length.TryGetInt32(out var seconds) || seconds <= 0) return false;
        if (!row.TryGetProperty("workItemId", out var item) || item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var workItem) || workItem < 1) return false;

        log = new WorkLog(id.GetString()!, moment.ToLocalTime().DateTime, seconds, workItem);
        return true;
    }

    private static SubmitOutcome Refused(string message) =>
        new(false, Array.Empty<SentEntry>(), message);

    private static ReadOutcome Unreadable(string failure) =>
        new(Array.Empty<WorkLog>(), failure);
}
