#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace SeptPaceAuto.Services;

/// <summary>Vérification d'un dossier de dépôt : ce qui a été lu, et la phrase à afficher.</summary>
internal sealed record RepoProbe(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("branch")] string? Branch,
    [property: JsonPropertyName("ticket")] int? Ticket,
    [property: JsonPropertyName("message")] string Message);

/// <summary>Vérification d'un service extérieur : réussite ou non, et la phrase à afficher.</summary>
internal sealed record ServiceProbe(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("message")] string Message);

/// <summary>
/// Vérifications de la prise en main. Elles lisent et rien d'autre : aucun réglage n'est
/// enregistré, aucun jeton n'est écrit, aucun temps n'est envoyé et aucune attribution
/// n'est inventée. Chaque réponse porte la phrase française affichée telle quelle.
/// </summary>
internal static class Probes
{
    /// <summary>Même budget que le relevé de branche du suivi.</summary>
    private static readonly TimeSpan GitBudget = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan AzureBudget = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TokenBudget = TimeSpan.FromSeconds(12);

    /// <summary>Secours quand curl manque : un seul client pour tout le processus.</summary>
    private static readonly HttpClient Http = new() { Timeout = TokenBudget };

    /// <summary>
    /// Lit la branche du dossier indiqué, sans rien enregistrer : le dossier reste celui
    /// des réglages tant que l'utilisateur n'a pas validé.
    /// </summary>
    public static async Task<RepoProbe> RepositoryAsync(string? path, CancellationToken ct)
    {
        var text = (path ?? string.Empty).Trim().Trim('"');
        if (text.Length == 0)
        {
            return new RepoProbe(false, null, null, "Indique le dossier du dépôt Git à surveiller.");
        }

        string full;
        try
        {
            full = Path.GetFullPath(text);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new RepoProbe(false, null, null, $"« {text} » n’est pas un chemin de dossier valide.");
        }

        if (!DirectoryExists(full)) return new RepoProbe(false, null, null, "Dossier introuvable.");

        // Un dépôt cloné porte un dossier « .git » ; une copie de travail liée, un fichier.
        var marker = Path.Combine(full, ".git");
        if (!DirectoryExists(marker) && !FileExists(marker))
        {
            return new RepoProbe(false, null, null, "Ce dossier n’est pas un dépôt Git.");
        }

        var git = ProcessRunner.Git;
        if (git is null)
        {
            return new RepoProbe(false, null, null, "Git est introuvable sur ce poste : installe-le pour que la branche active puisse être lue.");
        }

        var head = await ProcessRunner.RunAsync(
            git,
            new[] { "-C", full, "rev-parse", "--abbrev-ref", "HEAD" },
            GitBudget,
            ct).ConfigureAwait(false);
        if (!head.Started) return new RepoProbe(false, null, null, "Git n’a pas pu être lancé sur ce poste.");
        if (head.TimedOut) return new RepoProbe(false, null, null, "Git n’a pas répondu en 10 secondes.");

        var name = head.Ok ? head.StdOut.Trim() : string.Empty;
        if (name.Length > 0 && !string.Equals(name, "HEAD", StringComparison.Ordinal)) return Read(name);

        // Dépôt sans aucun commit, ou HEAD détachée : la seconde lecture tranche entre les deux.
        var symbolic = await ProcessRunner.RunAsync(
            git,
            new[] { "-C", full, "symbolic-ref", "--quiet", "--short", "HEAD" },
            GitBudget,
            ct).ConfigureAwait(false);
        if (symbolic.TimedOut) return new RepoProbe(false, null, null, "Git n’a pas répondu en 10 secondes.");

        var alternate = symbolic.Ok ? symbolic.StdOut.Trim() : string.Empty;
        if (alternate.Length > 0) return Read(alternate);

        if (string.Equals(name, "HEAD", StringComparison.Ordinal))
        {
            return new RepoProbe(true, null, null, "Dépôt lu, mais HEAD est détachée : sans branche active, le temps restera à attribuer.");
        }

        var cause = Shorten(head.StdErr.Length > 0 ? head.StdErr : symbolic.StdErr);
        return new RepoProbe(false, null, null, cause.Length == 0
            ? "La branche de ce dépôt n’a pas pu être lue."
            : $"La branche de ce dépôt n’a pas pu être lue : {cause}");
    }

    /// <summary>
    /// Le numéro vient du nom de la branche, comme pendant le suivi. Sans numéro, le
    /// créneau restera à attribuer : rien n'est deviné.
    /// </summary>
    private static RepoProbe Read(string branch)
    {
        var ticket = WorkItemResolver.ExtractBug(branch);
        return ticket is int number
            ? new RepoProbe(true, branch, number, $"Dépôt lu : branche {branch}, ticket #{number}.")
            : new RepoProbe(true, branch, null, $"Branche {branch} : aucun numéro de ticket détecté, le temps restera à attribuer.");
    }

    /// <summary>
    /// Interroge Azure DevOps en lecture seule, le temps de savoir si la CLI répond pour
    /// cette organisation. Rien n'est mis en cache : la prochaine vérification repart à zéro.
    /// </summary>
    public static async Task<ServiceProbe> AzureAsync(string? organization, CancellationToken ct)
    {
        var text = (organization ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return new ServiceProbe(false, "Renseigne l’organisation pour retrouver automatiquement le Fix enfant.");
        }

        var url = Profile.OrganizationUrl(text);
        if (url is null)
        {
            return new ServiceProbe(false, "L’organisation Azure DevOps doit être une adresse http(s), par exemple https://dev.azure.com/mon-organisation.");
        }

        var az = ProcessRunner.Az;
        if (az is null)
        {
            return new ServiceProbe(false, "Azure CLI (az) est introuvable sur ce poste : le Fix enfant devra être choisi à la main.");
        }

        // Lecture la moins coûteuse qui prouve l'accès : un seul projet suffit.
        var result = await ProcessRunner.RunAsync(
            az,
            new[] { "devops", "project", "list", "--organization", url, "--top", "1", "--output", "json" },
            AzureBudget,
            ct).ConfigureAwait(false);

        if (!result.Started) return new ServiceProbe(false, "Azure CLI n’a pas pu être lancée sur ce poste.");
        if (result.TimedOut) return new ServiceProbe(false, "Azure DevOps n’a pas répondu en 15 secondes.");
        if (result.Ok)
        {
            return new ServiceProbe(true, "Organisation joignable : le Fix enfant sera retrouvé automatiquement dès qu’une branche portera un numéro.");
        }

        return new ServiceProbe(false, Explain(url, result));
    }

    /// <summary>La plainte de la CLI, ramenée à la phrase qui dit quoi faire.</summary>
    private static string Explain(string organization, ProcessResult result)
    {
        var complaint = string.Concat(result.StdErr, " ", result.StdOut).ToLowerInvariant();

        if (complaint.Contains("misspelled", StringComparison.Ordinal)
            || complaint.Contains("extension", StringComparison.Ordinal))
        {
            return "L’extension Azure DevOps de la CLI est absente : installe-la avec « az extension add --name azure-devops », puis recommence.";
        }

        if (complaint.Contains("az login", StringComparison.Ordinal)
            || complaint.Contains("az devops login", StringComparison.Ordinal)
            || complaint.Contains("please run", StringComparison.Ordinal)
            || complaint.Contains("tf400813", StringComparison.Ordinal)
            || complaint.Contains("unauthorized", StringComparison.Ordinal)
            || complaint.Contains("authentication", StringComparison.Ordinal))
        {
            return "Azure CLI n’est pas authentifiée : lance « az login » dans un terminal, puis recommence.";
        }

        // « The resource cannot be found. Operation returned a 404 status code. » : c'est
        // la réponse observée pour une organisation qui n'existe pas.
        if (complaint.Contains("tf200016", StringComparison.Ordinal)
            || complaint.Contains("vs800075", StringComparison.Ordinal)
            || complaint.Contains("does not exist", StringComparison.Ordinal)
            || complaint.Contains("cannot be found", StringComparison.Ordinal)
            || complaint.Contains("not found", StringComparison.Ordinal)
            || complaint.Contains("404", StringComparison.Ordinal)
            || complaint.Contains("resolve", StringComparison.Ordinal)
            || complaint.Contains("connect", StringComparison.Ordinal))
        {
            return $"L’organisation « {organization} » n’a pas répondu : vérifie l’adresse.";
        }

        var cause = Shorten(result.StdErr.Length > 0 ? result.StdErr : result.StdOut);
        return cause.Length == 0
            ? $"Azure DevOps n’a pas pu être interrogé (code {result.ExitCode.ToString(CultureInfo.InvariantCulture)})."
            : $"Azure DevOps n’a pas pu être interrogé : {cause}";
    }

    /// <summary>
    /// Une seule lecture sur l'API 7pace du compte indiqué : rien n'est écrit, aucun relevé
    /// n'est créé. Une lecture acceptée ne prouve pas les droits d'écriture, et la phrase
    /// rendue le dit.
    /// </summary>
    public static async Task<ServiceProbe> TokenAsync(string? account, string? token, CancellationToken ct)
    {
        var endpoint = Profile.EndpointFor(account);
        if (endpoint is null) return new ServiceProbe(false, "Renseigne d’abord ton compte 7pace.");

        var secret = (token ?? string.Empty).Trim();
        if (secret.Length == 0)
        {
            secret = TokenStore.Read() ?? string.Empty;
            if (secret.Length == 0) return new ServiceProbe(false, "Aucun jeton enregistré.");
        }

        // Un seul relevé demandé : la réponse sert uniquement à connaître le code HTTP.
        var reading = await GetAsync(endpoint + "&$top=1", secret, ct).ConfigureAwait(false);
        if (reading.Failure is string failure) return new ServiceProbe(false, failure);

        if (reading.Status is >= 200 and <= 299)
        {
            return new ServiceProbe(true, "Jeton accepté : la lecture fonctionne. Les droits d’écriture ne sont pas prouvés tant qu’un envoi n’a pas réussi.");
        }

        return new ServiceProbe(false, reading.Status switch
        {
            401 => "Jeton refusé (HTTP 401) : il est invalide ou expiré. Génère-en un nouveau dans 7pace, puis colle-le ici.",
            403 => "Jeton refusé (HTTP 403) : il n’a pas le droit de lire les relevés de ce compte 7pace.",
            404 => $"Le compte « {Profile.AccountName(account)} » ne répond pas à cette adresse (HTTP 404) : vérifie le nom du compte 7pace.",
            _ => $"7pace a répondu HTTP {reading.Status.ToString(CultureInfo.InvariantCulture)} : la lecture n’a pas abouti.",
        });
    }

    /// <summary>Réponse d'une lecture : le code HTTP obtenu, ou la raison de n'en avoir aucun.</summary>
    private readonly record struct Reading(int Status, string? Failure);

    /// <summary>
    /// Même transport que les mises à jour : le curl livré avec Windows d'abord, la pile
    /// HTTP de .NET seulement pour un poste qui n'aurait pas curl — mesuré sur ce poste,
    /// .NET n'atteint pas l'extérieur alors que curl répond.
    /// </summary>
    private static async Task<Reading> GetAsync(string address, string token, CancellationToken ct)
    {
        if (ProcessRunner.Curl is string curl)
        {
            // Null signifie « curl n'a pas démarré » : dans ce seul cas on essaie encore .NET.
            var byCurl = await GetWithCurlAsync(curl, address, token, ct).ConfigureAwait(false);
            if (byCurl is Reading reading) return reading;
        }

        return await GetWithHttpAsync(address, token, ct).ConfigureAwait(false);
    }

    private static async Task<Reading?> GetWithCurlAsync(string curl, string address, string token, CancellationToken ct)
    {
        var arguments = new[]
        {
            "-sS",
            "--max-time", "10",
            "-H", "Authorization: Bearer " + token,
            "-H", "Accept: application/json",
            "-w", "\\n%{http_code}", // curl interprète lui-même la séquence : le code finit seul sur sa ligne.
            address,
        };

        var result = await ProcessRunner.RunAsync(curl, arguments, TokenBudget, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        if (!result.Started) return null;
        if (result.TimedOut || result.ExitCode == 28) return new Reading(0, "7pace n’a pas répondu dans les 12 secondes.");

        var output = result.StdOut;
        var cut = output.LastIndexOf('\n');
        var status = (cut >= 0 ? output[(cut + 1)..] : output).Trim();
        if (int.TryParse(status, NumberStyles.None, CultureInfo.InvariantCulture, out var code) && code > 0)
        {
            return new Reading(code, null);
        }

        // curl s'est arrêté avant d'obtenir une réponse : son explication est la plus utile.
        var cause = Shorten(result.StdErr);
        return new Reading(0, "Lecture impossible : " + (cause.Length > 0
            ? cause
            : $"curl s’est arrêté (code {result.ExitCode.ToString(CultureInfo.InvariantCulture)})."));
    }

    private static async Task<Reading> GetWithHttpAsync(string address, string token, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TokenBudget);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
            return new Reading((int)response.StatusCode, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new Reading(0, "7pace n’a pas répondu dans les 12 secondes.");
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidOperationException or UriFormatException)
        {
            return new Reading(0, "Lecture impossible : " + Shorten(error.Message));
        }
    }

    private static string Shorten(string message)
    {
        var text = message.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= 200 ? text : text[..200] + "…";
    }

    private static bool DirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool FileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
