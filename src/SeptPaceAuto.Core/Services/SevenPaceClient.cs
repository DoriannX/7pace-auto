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

/// <summary>Un créneau réellement accepté par 7pace : il est verrouillé et ne repartira pas.</summary>
public sealed record SentEntry(int EntryId, int WorkItem, int Seconds);

/// <summary>
/// Résultat d'un envoi. <paramref name="Sent"/> ne contient que les créneaux dont
/// l'écriture a réellement été acceptée par 7pace.
/// </summary>
public sealed record SubmitOutcome(bool Ok, IReadOnlyList<SentEntry> Sent, string Message);

/// <summary>
/// Écritures vers 7pace. L'application n'y lit jamais rien : une journée envoyée appartient
/// à 7pace, qui devient sa seule source de vérité. Les créneaux d'un même élément de travail
/// qui se touchent partent en un seul worklog ; le reste du découpage horaire est préservé.
/// </summary>
public sealed class SevenPaceClient
{
    /// <summary>
    /// Budget d'une écriture. C'est ce client qui borne ses appels, pas le HttpClient :
    /// un délai porté par le client masquerait la vraie cause dans le message d'échec.
    /// </summary>
    private static readonly TimeSpan WriteBudget = TimeSpan.FromSeconds(20);

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

        if (day.Count == 0)
        {
            return Refused($"Aucun temps à envoyer pour le {readable} : rien n’a été envoyé.");
        }

        var unassigned = day.FirstOrDefault(entry => entry.Unassigned);
        if (unassigned is not null)
        {
            return Refused($"Le créneau de {unassigned.Start} à {unassigned.End} n’est pas attribué : rien n’a été envoyé.");
        }

        // Un créneau déjà accepté par 7pace n'est jamais renvoyé : pas de double comptage,
        // et un envoi partiellement refusé peut être terminé plus tard.
        var pending = day
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

        foreach (var run in Contiguous(pending))
        {
            var first = run[0];
            var last = run[^1];
            var workItem = first.WorkItem!.Value;
            var seconds = (last.EndMinutes - first.StartMinutes) * 60;

            var timestamp = Stamp(day0.AddMinutes(first.StartMinutes));
            var failure = await PostAsync(endpoint, token, timestamp, seconds, workItem, ct).ConfigureAwait(false);
            if (failure is null)
            {
                foreach (var entry in run)
                {
                    sent.Add(new SentEntry(entry.Id!.Value, workItem, (entry.EndMinutes - entry.StartMinutes) * 60));
                }
            }
            else
            {
                failures.Add($"{first.Start}–{last.End} ({failure})");
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
            : $"Envoi partiel du {readable} : {sent.Count} créneau(x) acceptés, refus sur {detail}. Les créneaux refusés restent modifiables et repartiront seuls.";
        return new SubmitOutcome(false, sent, message);
    }

    /// <summary>
    /// Suites de créneaux du même élément de travail où chacun commence à la fin du
    /// précédent : une suite devient un seul worklog. Un chevauchement ou un écart, même
    /// d'une minute, garde des worklogs distincts pour ne rien fausser.
    /// </summary>
    private static List<List<Entry>> Contiguous(IEnumerable<Entry> ordered)
    {
        var runs = new List<List<Entry>>();
        var open = new Dictionary<(int WorkItem, int End), List<Entry>>();
        foreach (var entry in ordered)
        {
            if (entry.EndMinutes <= entry.StartMinutes) continue;
            var workItem = entry.WorkItem!.Value;
            if (open.Remove((workItem, entry.StartMinutes), out var run))
            {
                run.Add(entry);
            }
            else
            {
                run = new List<Entry> { entry };
                runs.Add(run);
            }
            open[(workItem, entry.EndMinutes)] = run;
        }
        return runs;
    }

    /// <summary>Null = 7pace a accepté l'écriture.</summary>
    private async Task<string?> PostAsync(string endpoint, string token, string timestamp, int seconds, int workItem, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { timestamp, length = seconds, workItemId = workItem }, Json.Wire);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(WriteBudget);

        var (reply, failure) = await SendAsync(
            () => Signed(
                new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(payload, new UTF8Encoding(false), "application/json"),
                },
                token),
            budget.Token).ConfigureAwait(false);
        if (failure is not null) return failure;

        using var response = reply!;
        return response.IsSuccessStatusCode ? null : Explain(response);
    }

    /// <summary>
    /// Envoi avec patience sur le quota 7pace. Response non nulle quand Failure est nul.
    /// </summary>
    private async Task<(HttpResponseMessage? Response, string? Failure)> SendAsync(Func<HttpRequestMessage> build, CancellationToken ct)
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
                return (null, "aucune réponse dans le délai imparti");
            }
            catch (HttpRequestException error)
            {
                return (null, $"réseau indisponible ({error.Message})");
            }

            if (response.StatusCode != HttpStatusCode.TooManyRequests) return (response, null);

            var pause = Pause(response, Backoff[Math.Min(attempt, Backoff.Length - 1)]);
            response.Dispose();
            if (attempt >= Backoff.Length - 1)
            {
                return (null, "HTTP 429, quota 7pace atteint : réessaie dans quelques minutes");
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
                return (null, "HTTP 429, quota 7pace atteint : réessaie dans quelques minutes");
            }
        }
    }

    private static HttpRequestMessage Signed(HttpRequestMessage request, string token)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    /// <summary>
    /// Horodatage tel que 7pace le range : l'heure murale locale, sans décalage ni suffixe.
    /// Mesuré sur le compte réel — un envoi converti en UTC puis suffixé « Z » ressortait
    /// deux heures plus tôt en septembre (Paris UTC+2). 7pace prend la valeur au pied de la
    /// lettre ; lui donner de l'UTC déplace donc tous les créneaux.
    /// </summary>
    private static string Stamp(DateTime local) =>
        local.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private static TimeSpan Pause(HttpResponseMessage response, int fallbackSeconds)
    {
        var asked = response.Headers.RetryAfter?.Delta;
        return asked is { } delta && delta > TimeSpan.Zero && delta <= TimeSpan.FromSeconds(60)
            ? delta
            : TimeSpan.FromSeconds(fallbackSeconds);
    }

    private static string Explain(HttpResponseMessage response) => response.StatusCode switch
    {
        HttpStatusCode.Unauthorized => "HTTP 401, jeton refusé : enregistre un nouveau jeton dans les réglages",
        HttpStatusCode.Forbidden => "HTTP 403, droits d’écriture refusés",
        _ => $"HTTP {((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)}",
    };

    private static SubmitOutcome Refused(string message) =>
        new(false, Array.Empty<SentEntry>(), message);
}
