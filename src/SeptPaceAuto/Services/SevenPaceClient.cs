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

/// <summary>Un envoi regroupé : un work item, la durée totale envoyée.</summary>
public sealed record SentGroup(int WorkItem, int Seconds);

/// <summary>
/// Résultat d'un envoi. <paramref name="SentEntryIds"/> ne contient que les créneaux dont
/// l'écriture a réellement été acceptée par 7pace.
/// </summary>
public sealed record SubmitOutcome(bool Ok, IReadOnlyList<SentGroup> Sent, IReadOnlyList<int> SentEntryIds, string Message);

/// <summary>
/// Envoi des imputations dans 7pace. L'authentification est vérifiée mais les droits
/// d'écriture ne sont pas prouvés : un refus est rapporté comme un échec, jamais avalé.
/// </summary>
public sealed class SevenPaceClient
{
    private readonly HttpClient _http;
    private readonly Func<string?> _endpoint;

    /// <param name="endpoint">Adresse d'écriture dérivée du compte réglé, nulle tant qu'il est vide.</param>
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
        var pending = billable.Where(entry => entry.SentAt is null && entry.Id is not null).ToList();
        if (pending.Count == 0)
        {
            return Refused($"Le {readable} est déjà envoyé dans 7pace : rien n’a été renvoyé, pour ne pas compter le temps deux fois.");
        }

        var groups = pending
            .GroupBy(entry => entry.WorkItem!.Value)
            .Select(group => new
            {
                WorkItem = group.Key,
                Seconds = group.Sum(entry => (entry.EndMinutes - entry.StartMinutes) * 60),
                Start = group.Min(entry => entry.StartMinutes),
                Ids = group.Select(entry => entry.Id!.Value).ToList(),
            })
            .OrderBy(group => group.Start)
            .ToList();

        var sent = new List<SentGroup>();
        var sentIds = new List<int>();
        var failures = new List<string>();
        var day0 = TimeRules.ParseDate(date);

        foreach (var group in groups)
        {
            var timestamp = day0.AddMinutes(group.Start).ToString("MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
            var failure = await PostAsync(endpoint, token, timestamp, group.Seconds, group.WorkItem, ct).ConfigureAwait(false);
            if (failure is null)
            {
                sent.Add(new SentGroup(group.WorkItem, group.Seconds));
                sentIds.AddRange(group.Ids);
            }
            else
            {
                failures.Add($"#{group.WorkItem} ({failure})");
            }
        }

        if (failures.Count == 0)
        {
            var total = TimeRules.Readable(sent.Sum(group => group.Seconds) / 60);
            var count = sent.Count;
            return new SubmitOutcome(true, sent, sentIds, $"{total} envoyé dans 7pace pour le {readable}, sur {count} élément{(count > 1 ? "s" : string.Empty)} de travail.");
        }

        var detail = string.Join(", ", failures);
        var message = sent.Count == 0
            ? $"7pace a refusé l’envoi du {readable} : {detail}. Aucun temps n’a été marqué comme envoyé — les droits d’écriture ne sont pas prouvés."
            : $"Envoi partiel du {readable} : {sent.Count} élément(s) acceptés, refus sur {detail}. Les créneaux refusés ne sont pas marqués comme envoyés.";
        return new SubmitOutcome(false, sent, sentIds, message);
    }

    /// <summary>Retourne null si 7pace a accepté, sinon la cause à afficher.</summary>
    private async Task<string?> PostAsync(string endpoint, string token, string timestamp, int seconds, int workItem, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { timestamp, length = seconds, workItemId = workItem }, Json.Wire);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, new UTF8Encoding(false), "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return null;
            var status = (int)response.StatusCode;
            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "HTTP 401, jeton refusé : enregistre un nouveau jeton dans les réglages",
                HttpStatusCode.Forbidden => "HTTP 403, droits d’écriture refusés",
                _ => $"HTTP {status.ToString(CultureInfo.InvariantCulture)}",
            };
        }
        catch (TaskCanceledException)
        {
            return "aucune réponse dans le délai imparti";
        }
        catch (HttpRequestException error)
        {
            return $"réseau indisponible ({error.Message})";
        }
    }

    private static SubmitOutcome Refused(string message) =>
        new(false, Array.Empty<SentGroup>(), Array.Empty<int>(), message);
}
