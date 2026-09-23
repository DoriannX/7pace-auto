#nullable enable
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace SeptPaceAuto.Services;

/// <summary>
/// Résultat d'une vérification de mise à jour. Les noms JSON sont ceux du seam
/// (<c>checkUpdate</c>) : l'URL de l'archive reste interne à l'hôte.
/// </summary>
public sealed record UpdateInfo(
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("current")] string Current,
    [property: JsonPropertyName("latest")] string? Latest,
    [property: JsonPropertyName("notes")] string? Notes,
    [property: JsonIgnore] string? AssetUrl,
    [property: JsonPropertyName("error")] string? Error);

/// <summary>
/// Mise à jour de l'application depuis l’archive publiée sur GitHub. L’installation est réservée
/// au dossier posé par <c>build/install.ps1</c> : une compilation locale n’est jamais
/// remplacée par une publication.
/// </summary>
public interface IUpdateService
{
    /// <summary>
    /// Interroge la dernière publication de <paramref name="repository"/> (forme
    /// <c>compte/dépôt</c>). Une panne réseau ou une archive absente remplit
    /// <see cref="UpdateInfo.Error"/> sans lever d'exception ; seule une annulation
    /// demandée par l'appelant remonte.
    /// </summary>
    Task<UpdateInfo> CheckAsync(string repository, CancellationToken ct);

    /// <summary>Télécharge l'archive et retourne le dossier préparé.</summary>
    Task<string> StageAsync(UpdateInfo info, CancellationToken ct);

    /// <summary>
    /// Lance le script d'installation ; l'appelant doit quitter juste après. Le script
    /// attend la fin du collecteur et de l'app demandeuse avant de toucher aux fichiers.
    /// </summary>
    /// <param name="clientPid">App à attendre en plus du collecteur, nulle quand il n'y en a pas.</param>
    /// <param name="reopenApp">Rouvre l'app, en widget, après l'installation.</param>
    void LaunchUpdater(string stagedFolder, int? clientPid, bool reopenApp);
}

/// <summary>Point d'entrée unique du service de mise à jour.</summary>
public static class UpdateServiceFactory
{
    public static IUpdateService Create(string currentVersion) => new GitHubUpdateService(currentVersion);
}

/// <summary>
/// Implémentation GitHub : une requête publique sans jeton. Le réseau passe par le
/// curl.exe livré avec Windows, la pile HTTP de .NET ne sert que de secours.
/// </summary>
internal sealed class GitHubUpdateService : IUpdateService
{
    /// <summary>Nom figé de l'archive publiée, partagé avec build/publish.ps1.</summary>
    public const string AssetName = "SeptPaceAuto-win-x64.zip";

    /// <summary>
    /// L'archive doit porter les deux exécutables à sa racine : le collecteur de fond et
    /// l'app. Une archive incomplète est refusée plutôt qu'installée à moitié.
    /// </summary>
    private static readonly string[] Executables = { AgentEndpoint.AgentExecutable, AgentEndpoint.AppExecutable };

    /// <summary>
    /// Une vérification ne bloque jamais l'interface : le pont abandonne à 20 s, donc la
    /// requête abandonne avant.
    /// </summary>
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(12);

    /// <summary>curl s'arrête de lui-même à 600 s ; la marge sert à le récupérer proprement.</summary>
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(620);

    /// <summary>
    /// Mesuré sur ce poste : la pile HTTP de .NET n'atteint pas api.github.com (la connexion
    /// reste muette jusqu'à expiration, y compris en forçant l'IPv4 et sans proxy) alors que
    /// le curl livré avec Windows répond en 0,14 s. Le réseau passe donc par curl.exe dès
    /// qu'il est présent ; la pile .NET n'est gardée que pour un poste qui n'en aurait pas.
    /// </summary>
    private static readonly string? Curl = ProcessRunner.Curl;

    /// <summary>Secours : un seul client pour tout le processus, un téléchargement est long.</summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };


    private readonly string _current;
    private readonly string _agent;

    public GitHubUpdateService(string currentVersion)
    {
        _current = Normalize(currentVersion);
        // Un produit sans version reste un User-Agent valide ; « produit/ » ne l'est pas.
        _agent = _current.Length == 0 ? "7pace-auto" : "7pace-auto/" + _current;
    }

    private static string UpdateFolder => Path.Combine(AppPaths.Root, "update");
    private static string StagedFolder => Path.Combine(UpdateFolder, "staged");

    /// <summary>Copie de l'installation avant remplacement : elle sert au retour arrière.</summary>
    private static string BackupFolder => Path.Combine(UpdateFolder, "backup");
    private static string ScriptPath => Path.Combine(UpdateFolder, "apply.cmd");
    private static string LogPath => Path.Combine(UpdateFolder, "apply.log");


    public async Task<UpdateInfo> CheckAsync(string repository, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repository))
        {
            return Failed("Aucun dépôt de mise à jour n’est renseigné dans les réglages.");
        }

        var address = "https://api.github.com/repos/" + repository.Trim().Trim('/') + "/releases/latest";

        if (Curl is not null)
        {
            // Null signifie « curl n'a pas démarré » : dans ce seul cas on essaie encore .NET.
            var byCurl = await CheckWithCurlAsync(address, ct).ConfigureAwait(false);
            if (byCurl is not null) return byCurl;
        }

        return await CheckWithHttpAsync(address, ct).ConfigureAwait(false);
    }

    /// <summary>Vérification par curl ; retourne null si l'outil n'a pas pu être lancé.</summary>
    private async Task<UpdateInfo?> CheckWithCurlAsync(string address, CancellationToken ct)
    {
        var arguments = new[]
        {
            "-sS", "-L",
            "--max-time", "10",
            "-H", "User-Agent: " + _agent, // GitHub refuse les appels anonymes sans User-Agent.
            "-H", "Accept: application/vnd.github+json",
            "-w", "\\n%{http_code}", // curl interprète lui-même la séquence : le code finit seul sur sa ligne.
            address,
        };

        var result = await ProcessRunner.RunAsync(Curl!, arguments, CheckTimeout, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        if (!result.Started) return null;
        if (result.TimedOut || result.ExitCode == 28)
        {
            return Failed("Le serveur des mises à jour n’a pas répondu à temps.");
        }

        var output = result.StdOut;
        var cut = output.LastIndexOf('\n');
        var status = (cut >= 0 ? output[(cut + 1)..] : output).Trim();
        var body = cut >= 0 ? output[..cut] : string.Empty;

        if (!int.TryParse(status, NumberStyles.None, CultureInfo.InvariantCulture, out var code) || code == 0)
        {
            // curl a échoué avant d'obtenir une réponse : sa propre explication est la plus utile.
            var cause = result.StdErr.Trim();
            return Failed("Vérification impossible : " +
                Shorten(cause.Length > 0 ? cause : "curl s’est arrêté (code " + result.ExitCode + ")."));
        }

        if (code is < 200 or > 299) return Failed(Explain((HttpStatusCode)code));

        try
        {
            using var document = JsonDocument.Parse(body);
            return Read(document.RootElement);
        }
        catch (JsonException error)
        {
            return Failed("Vérification impossible : " + Shorten(error.Message));
        }
    }

    /// <summary>Secours sans curl : la pile HTTP de .NET, mêmes messages.</summary>
    private async Task<UpdateInfo> CheckWithHttpAsync(string address, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(CheckTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.UserAgent.ParseAdd(_agent);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return Failed(Explain(response.StatusCode));
            }

            using var payload = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(payload, default, deadline.Token).ConfigureAwait(false);
            return Read(document.RootElement);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failed("Le serveur des mises à jour n’a pas répondu à temps.");
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or IOException or InvalidOperationException or UriFormatException)
        {
            return Failed("Vérification impossible : " + Shorten(error.Message));
        }
    }

    /// <summary>Lit la version et l’archive de la publication.</summary>
    private UpdateInfo Read(JsonElement release)
    {
        var latest = Normalize(Text(release, "tag_name"));
        if (latest.Length == 0)
        {
            return Failed("La dernière publication du dépôt n’a pas de numéro de version.");
        }

        if (!IsNewer(latest, _current))
        {
            return new UpdateInfo(false, _current, latest, null, null, null);
        }

        var asset = Asset(release, AssetName);
        if (asset is null)
        {
            return new UpdateInfo(false, _current, latest, null, null,
                "La version " + latest + " est publiée mais l’archive " + AssetName + " manque.");
        }

        var notes = Text(release, "body").Trim();
        return new UpdateInfo(true, _current, latest, notes.Length == 0 ? null : Clip(notes), asset, null);
    }

    private static string? Asset(JsonElement release, string name)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            if (!string.Equals(Text(asset, "name"), name, StringComparison.OrdinalIgnoreCase)) continue;
            var url = Text(asset, "browser_download_url");
            if (url.Length > 0) return url;
        }

        return null;
    }


    public async Task<string> StageAsync(UpdateInfo info, CancellationToken ct)
    {
        if (info.AssetUrl is null || info.AssetUrl.Length == 0 || info.Latest is null)
        {
            throw new DomainException("Aucune archive de mise à jour à installer.");
        }

        _ = Installation();
        Directory.CreateDirectory(UpdateFolder);

        try
        {
            var archive = Path.Combine(UpdateFolder, FileNameFor(info.Latest) + ".zip");
            await DownloadAsync(info.AssetUrl, archive, ct).ConfigureAwait(false);
            Verify(archive);

            Erase(StagedFolder);
            ZipFile.ExtractToDirectory(archive, StagedFolder);
            return StagedFolder;
        }
        catch (DomainException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
        {
            throw new DomainException("Téléchargement de la mise à jour impossible : " + Shorten(error.Message));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new DomainException("Préparation de la mise à jour impossible : " + Shorten(error.Message));
        }
    }

    private static async Task DownloadAsync(string url, string target, CancellationToken ct)
    {
        // Faux signifie « curl n'a pas démarré » : un refus de GitHub lève, sans repli.
        if (Curl is not null && await DownloadWithCurlAsync(url, target, ct).ConfigureAwait(false))
        {
            return;
        }

        await DownloadWithHttpAsync(url, target, ct).ConfigureAwait(false);
    }

    private static async Task<bool> DownloadWithCurlAsync(string url, string target, CancellationToken ct)
    {
        var arguments = new[] { "-sS", "-L", "--max-time", "600", "--fail", "-o", target, url };
        var result = await ProcessRunner.RunAsync(Curl!, arguments, DownloadTimeout, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        if (!result.Started) return false;

        if (result.TimedOut || result.ExitCode == 28)
        {
            throw new DomainException("Le téléchargement de la mise à jour n’a pas abouti à temps.");
        }

        if (result.ExitCode != 0)
        {
            var cause = result.StdErr.Trim();
            throw new DomainException("Téléchargement de la mise à jour impossible : " +
                Shorten(cause.Length > 0 ? cause : "curl s’est arrêté (code " + result.ExitCode + ")."));
        }

        if (!File.Exists(target) || new FileInfo(target).Length == 0)
        {
            throw new DomainException("L’archive téléchargée est vide.");
        }

        return true;
    }

    /// <summary>Secours sans curl : téléchargement par la pile HTTP de .NET.</summary>
    private static async Task DownloadWithHttpAsync(string url, string archive, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("7pace-auto");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new DomainException("Téléchargement refusé par GitHub (" + (int)response.StatusCode + ").");
        }

        using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var target = new FileStream(archive, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        await source.CopyToAsync(target, 1 << 16, ct).ConfigureAwait(false);
    }

    /// <summary>Refuse tôt une archive tronquée ou étrangère à l'application.</summary>
    private static void Verify(string archive)
    {
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count == 0)
        {
            throw new DomainException("L’archive de mise à jour est vide.");
        }

        foreach (var name in Executables)
        {
            var found = zip.Entries.Any(entry =>
                string.Equals(entry.FullName, name, StringComparison.OrdinalIgnoreCase));
            if (!found)
            {
                throw new DomainException("L’archive téléchargée ne contient pas " + name + " à sa racine.");
            }
        }
    }


    public void LaunchUpdater(string stagedFolder, int? clientPid, bool reopenApp)
    {
        if (string.IsNullOrWhiteSpace(stagedFolder) || !Directory.Exists(stagedFolder))
        {
            throw new DomainException("Le dossier de mise à jour préparé est introuvable.");
        }

        var installation = Installation();
        var script = Script(stagedFolder, installation, clientPid, reopenApp);

        Directory.CreateDirectory(UpdateFolder);
        File.WriteAllText(ScriptPath, script, new UTF8Encoding(false));

        var start = new ProcessStartInfo
        {
            FileName = ScriptPath,
            WorkingDirectory = UpdateFolder,
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        using var process = Process.Start(start);
        if (process is null)
        {
            throw new DomainException("Le script de mise à jour n’a pas pu être lancé.");
        }
    }

    /// <summary>
    /// Script de remplacement. Il attend que le collecteur et l'app demandeuse soient
    /// réellement sortis — sans quoi les binaires resteraient verrouillés —, garde une copie
    /// de l'installation, recopie le dossier préparé, relance le collecteur puis se nettoie.
    /// Une copie qui échoue est annulée : l'installation précédente est remise en place et
    /// le suivi repart dessus, plutôt que de laisser un dossier à moitié remplacé.
    /// </summary>
    internal static string Script(string staged, (string Agent, string App, string Folder) installation, int? clientPid, bool reopenApp)
    {
        var text = Preamble(installation, clientPid);
        text.Append("set \"STAGED=").Append(Trim(staged)).Append("\"\r\n");
        text.Append("set \"INSTALL=").Append(Trim(installation.Folder)).Append("\"\r\n");
        text.Append("set \"BACKUP=").Append(Trim(BackupFolder)).Append("\"\r\n");
        Wait(text);
        text.Append("rmdir /s /q \"%BACKUP%\" >nul 2>&1\r\n");
        text.Append("robocopy \"%INSTALL%\" \"%BACKUP%\" /MIR /R:2 /W:1 /NFL /NDL /NJH /NJS /NP >>\"%LOG%\" 2>&1\r\n");
        text.Append("robocopy \"%STAGED%\" \"%INSTALL%\" /MIR /R:3 /W:2 /NFL /NDL /NJH /NJS /NP >>\"%LOG%\" 2>&1\r\n");
        text.Append("if errorlevel 8 (\r\n");
        // La fenêtre est masquée : la trace utile va dans le journal, pas sur la console.
        text.Append("  echo [7pace auto] copie en echec, retour a la version precedente. >>\"%LOG%\"\r\n");
        text.Append("  robocopy \"%BACKUP%\" \"%INSTALL%\" /MIR /R:3 /W:2 /NFL /NDL /NJH /NJS /NP >>\"%LOG%\" 2>&1\r\n");
        text.Append("  start \"\" \"%AGENT%\"\r\n");
        text.Append("  exit /b 1\r\n");
        text.Append(")\r\n");
        Finish(text, reopenApp);
        return text.ToString();
    }


    /// <summary>En-tête commun aux deux scripts : encodage, processus à attendre, journal.</summary>
    private static StringBuilder Preamble((string Agent, string App, string Folder) installation, int? clientPid)
    {
        var text = new StringBuilder();
        text.Append("@echo off\r\n");
        // Les chemins peuvent contenir des accents : le script est écrit en UTF-8.
        text.Append("chcp 65001 >nul 2>&1\r\n");
        text.Append("setlocal\r\n");
        var pids = clientPid is int client && client != Environment.ProcessId
            ? string.Concat(Environment.ProcessId.ToString(CultureInfo.InvariantCulture), " ", client.ToString(CultureInfo.InvariantCulture))
            : Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        text.Append("set \"PIDS=").Append(pids).Append("\"\r\n");
        text.Append("set \"AGENT=").Append(installation.Agent).Append("\"\r\n");
        text.Append("set \"APP=").Append(installation.App).Append("\"\r\n");
        text.Append("set \"LOG=").Append(LogPath).Append("\"\r\n");
        // Le dossier de travail peut avoir disparu entre la préparation et l'exécution :
        // sans lui, les redirections vers le journal échoueraient et rien ne serait copié.
        text.Append("if not exist \"").Append(Trim(UpdateFolder)).Append("\" mkdir \"").Append(Trim(UpdateFolder)).Append("\" >nul 2>&1\r\n");
        return text;
    }

    /// <summary>
    /// Attente de la fermeture du collecteur et de l'app : rien n'est touché avant. Le
    /// nom de l'image est vérifié en plus du numéro, pour qu'un PID recyclé par un autre
    /// programme ne bloque pas l'installation indéfiniment.
    /// </summary>
    private static void Wait(StringBuilder text)
    {
        text.Append(":wait\r\n");
        text.Append("set \"RESTE=\"\r\n");
        text.Append("for %%P in (%PIDS%) do (\r\n");
        text.Append("  tasklist /FI \"PID eq %%P\" /NH 2>nul | findstr /I /C:\"SeptPaceAuto\" >nul && set \"RESTE=1\"\r\n");
        text.Append(")\r\n");
        text.Append("if defined RESTE (\r\n");
        text.Append("  ping -n 2 127.0.0.1 >nul\r\n");
        text.Append("  goto wait\r\n");
        text.Append(")\r\n");
    }

    /// <summary>
    /// Relance le collecteur — le suivi est la promesse à tenir —, rouvre l'app en widget
    /// quand c'est elle qui a demandé la mise à jour, puis efface ses dossiers et lui-même.
    /// </summary>
    private static void Finish(StringBuilder text, bool reopenApp)
    {
        text.Append("start \"\" \"%AGENT%\"\r\n");
        if (reopenApp) text.Append("start \"\" \"%APP%\" --widget\r\n");
        text.Append("rmdir /s /q \"%STAGED%\" >nul 2>&1\r\n");
        text.Append("rmdir /s /q \"%BACKUP%\" >nul 2>&1\r\n");
        text.Append("del /f /q \"%~f0\" >nul 2>&1\r\n");
    }


    /// <summary>Robocopy refuse un dossier terminé par une barre oblique inverse.</summary>
    private static string Trim(string folder) => folder.TrimEnd('\\', '/');

    /// <summary>
    /// Installation posée par build/install.ps1, seule cible autorisée. Les deux exécutables
    /// doivent y être : remplacer un collecteur sans son app, ou l'inverse, laisserait
    /// deux versions face à face.
    /// </summary>
    private static (string Agent, string App, string Folder) Installation()
    {
        var folder = Home();
        var expected = Path.Combine(AppPaths.LocalAppData, "Programs", "7pace auto");
        var agent = Path.Combine(folder, AgentEndpoint.AgentExecutable);
        var app = Path.Combine(folder, AgentEndpoint.AppExecutable);

        if (!string.Equals(Path.GetFullPath(folder).TrimEnd('\\', '/'),
                Path.GetFullPath(expected).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
            || !File.Exists(agent)
            || !File.Exists(app))
        {
            throw new DomainException(
                "La mise à jour automatique est réservée à l’application installée. " +
                "Lance build/install.ps1 avant de mettre à jour.");
        }

        return (agent, app, folder);
    }

    /// <summary>Dossier du processus courant : le collecteur porte la mise à jour.</summary>
    private static string Home()
    {
        var path = Environment.ProcessPath;
        var folder = string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(path);
        return string.IsNullOrEmpty(folder) ? AppContext.BaseDirectory.TrimEnd('\\') : folder;
    }

    /// <summary>Comparaison numérique composant par composant, jamais textuelle.</summary>
    internal static bool IsNewer(string candidate, string current)
    {
        var left = Parts(candidate);
        var right = Parts(current);
        for (var index = 0; index < left.Length; index++)
        {
            if (left[index] != right[index]) return left[index] > right[index];
        }

        return false;
    }

    /// <summary>« v1.2.3-beta+42 » devient 1.2.3.0 ; un champ illisible vaut zéro.</summary>
    private static int[] Parts(string version)
    {
        var parts = new int[4];
        var pieces = version.Split('.');
        for (var index = 0; index < parts.Length && index < pieces.Length; index++)
        {
            parts[index] = int.TryParse(pieces[index], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                ? value
                : 0;
        }

        return parts;
    }

    /// <summary>Retire le « v » d'un tag et tout suffixe de pré-version ou de compilation.</summary>
    private static string Normalize(string? version)
    {
        var text = (version ?? string.Empty).Trim();
        if (text.Length > 0 && (text[0] == 'v' || text[0] == 'V')) text = text.Substring(1);

        var cut = text.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0) text = text.Substring(0, cut);
        return text.Trim();
    }

    private static string FileNameFor(string version)
    {
        var safe = new StringBuilder(version.Length);
        foreach (var character in version)
        {
            safe.Append(char.IsLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '_');
        }

        return safe.Length == 0 ? "update" : safe.ToString();
    }

    private static void Erase(string folder)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!Directory.Exists(folder)) return;
                Directory.Delete(folder, recursive: true);
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (attempt == 2) throw;
                Thread.Sleep(200);
            }
        }
    }

    private UpdateInfo Failed(string message) => new(false, _current, null, null, null, message);

    private static string Explain(HttpStatusCode status) => status switch
    {
        HttpStatusCode.NotFound => "Dépôt ou publication introuvable : vérifie le dépôt des mises à jour.",
        HttpStatusCode.Forbidden => "GitHub a refusé la vérification (quota d’appels atteint), réessaie plus tard.",
        HttpStatusCode.Unauthorized => "GitHub a refusé la vérification : le dépôt n’est pas public.",
        _ => "GitHub a répondu " + (int)status + " à la vérification.",
    };

    private static string Clip(string notes) => notes.Length <= 4000 ? notes : notes.Substring(0, 4000) + "…";

    private static string Shorten(string message)
    {
        var text = message.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= 200 ? text : text.Substring(0, 200) + "…";
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

}
