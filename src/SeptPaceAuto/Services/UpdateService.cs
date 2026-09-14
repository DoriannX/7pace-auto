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
using System.Runtime.InteropServices;
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
/// Mise à jour depuis les publications GitHub. Rien n'est installé sans passer par
/// <see cref="StageAsync"/>, et la façon d'installer suit celle dont l'application a été
/// posée : une installation venue de 7pace-auto-setup.msi se met à jour par msiexec, pour
/// que Windows enregistre la nouvelle version ; une installation scriptée ou extraite à la
/// main télécharge l'archive et recopie le dossier préparé sur l'installation.
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

    /// <summary>Télécharge l'archive ou l'installeur ; retourne le dossier préparé.</summary>
    Task<string> StageAsync(UpdateInfo info, CancellationToken ct);

    /// <summary>Lance le script d'installation ; l'appelant doit quitter juste après.</summary>
    void LaunchUpdater(string stagedFolder);
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

    /// <summary>Nom figé de l'installeur publié, partagé avec build/pack-msi.ps1.</summary>
    public const string InstallerName = "7pace-auto-setup.msi";

    /// <summary>Le nom de l'exécutable doit se trouver à la racine de l'archive.</summary>
    private const string ExecutableName = "SeptPaceAuto.exe";

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

    /// <summary>
    /// L'application a-t-elle été posée par l'installeur ? Windows Installer n'est interrogé
    /// qu'une fois : la réponse ne change pas pendant l'exécution, et ce qui la changerait
    /// (une réinstallation) passe par un autre processus.
    /// </summary>
    private static readonly Lazy<bool> MsiManaged = new(
        static () => WindowsInstaller.Manages(Executable()),
        LazyThreadSafetyMode.ExecutionAndPublication);

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
    private static string ScriptPath => Path.Combine(UpdateFolder, "apply.cmd");
    private static string LogPath => Path.Combine(UpdateFolder, "apply.log");

    /// <summary>
    /// Journal verbatim de Windows Installer, réécrit à chaque tentative : c'est la seule
    /// trace d'une installation silencieuse, et elle explique un code de retour.
    /// </summary>
    private static string InstallerLogPath => Path.Combine(UpdateFolder, "msiexec.log");

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

    /// <summary>Lit la publication avec la façon d'installer de ce poste.</summary>
    private UpdateInfo Read(JsonElement release) => Read(release, MsiManaged.Value);

    /// <summary>
    /// Lit la publication : version, notes et fichier à télécharger. La façon d'installer
    /// arrive en paramètre, parce qu'elle ne se lit nulle part dans la publication : une
    /// installation posée par le MSI se met à jour par le MSI, recopier des fichiers
    /// par-dessus laisserait « Applications installées » sur l'ancienne version.
    /// </summary>
    internal UpdateInfo Read(JsonElement release, bool installer)
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

        var wanted = installer ? InstallerName : AssetName;
        var asset = Asset(release, wanted);
        if (asset is null)
        {
            return new UpdateInfo(false, _current, latest, null, null,
                "La version " + latest + " est publiée mais " +
                (installer ? "l’installeur " : "l’archive ") + wanted + " manque.");
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

    /// <summary>
    /// Libellés français d'un téléchargement : l'archive et l'installeur ne se nomment pas
    /// pareil dans les messages montrés à l'utilisateur.
    /// </summary>
    private sealed record Wording(string Failed, string TooSlow, string Empty)
    {
        public static readonly Wording Archive = new(
            "Téléchargement de la mise à jour impossible : ",
            "Le téléchargement de la mise à jour n’a pas abouti à temps.",
            "L’archive téléchargée est vide.");

        public static readonly Wording Installer = new(
            "Téléchargement de l’installeur impossible : ",
            "Le téléchargement de l’installeur n’a pas abouti à temps.",
            "L’installeur téléchargé est vide.");
    }

    public async Task<string> StageAsync(UpdateInfo info, CancellationToken ct)
    {
        if (info.AssetUrl is null || info.AssetUrl.Length == 0 || info.Latest is null)
        {
            throw new DomainException("Aucune archive de mise à jour à installer.");
        }

        // L'adresse porte le nom de l'élément publié : un .msi s'installe, un .zip se recopie.
        var installer = info.AssetUrl.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
        var wording = installer ? Wording.Installer : Wording.Archive;

        Directory.CreateDirectory(UpdateFolder);

        try
        {
            if (installer)
            {
                // L'installeur attend dans le dossier préparé : c'est lui qui distingue les
                // deux chemins au moment de lancer le script, et le nettoyage l'emporte.
                Erase(StagedFolder);
                Directory.CreateDirectory(StagedFolder);
                var package = Path.Combine(StagedFolder, InstallerName);
                await DownloadAsync(info.AssetUrl, package, wording, ct).ConfigureAwait(false);
                VerifyInstaller(package);
                return StagedFolder;
            }

            var archive = Path.Combine(UpdateFolder, FileNameFor(info.Latest) + ".zip");
            await DownloadAsync(info.AssetUrl, archive, wording, ct).ConfigureAwait(false);
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
            throw new DomainException(wording.Failed + Shorten(error.Message));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new DomainException("Préparation de la mise à jour impossible : " + Shorten(error.Message));
        }
    }

    private static async Task DownloadAsync(string url, string target, Wording wording, CancellationToken ct)
    {
        // Faux signifie « curl n'a pas démarré » : un refus de GitHub lève, sans repli.
        if (Curl is not null && await DownloadWithCurlAsync(url, target, wording, ct).ConfigureAwait(false))
        {
            return;
        }

        await DownloadWithHttpAsync(url, target, ct).ConfigureAwait(false);
    }

    private static async Task<bool> DownloadWithCurlAsync(string url, string target, Wording wording, CancellationToken ct)
    {
        var arguments = new[] { "-sS", "-L", "--max-time", "600", "--fail", "-o", target, url };
        var result = await ProcessRunner.RunAsync(Curl!, arguments, DownloadTimeout, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        if (!result.Started) return false;

        if (result.TimedOut || result.ExitCode == 28)
        {
            throw new DomainException(wording.TooSlow);
        }

        if (result.ExitCode != 0)
        {
            var cause = result.StdErr.Trim();
            throw new DomainException(wording.Failed +
                Shorten(cause.Length > 0 ? cause : "curl s’est arrêté (code " + result.ExitCode + ")."));
        }

        if (!File.Exists(target) || new FileInfo(target).Length == 0)
        {
            throw new DomainException(wording.Empty);
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

        var found = zip.Entries.Any(entry =>
            string.Equals(entry.FullName, ExecutableName, StringComparison.OrdinalIgnoreCase));
        if (!found)
        {
            throw new DomainException("L’archive téléchargée ne contient pas " + ExecutableName + " à sa racine.");
        }
    }

    /// <summary>Signature d'un fichier composé OLE, celle de tout paquet Windows Installer.</summary>
    private static ReadOnlySpan<byte> InstallerSignature => new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };

    /// <summary>
    /// Refuse tôt un installeur tronqué, ou une page d'erreur servie à la place : msiexec
    /// n'aurait plus qu'un code de retour à offrir, une fois l'application fermée.
    /// </summary>
    private static void VerifyInstaller(string package)
    {
        using var stream = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> head = stackalloc byte[8];
        if (stream.Length < head.Length || stream.Read(head) != head.Length || !head.SequenceEqual(InstallerSignature))
        {
            throw new DomainException("Le fichier téléchargé n’est pas un installeur Windows.");
        }
    }

    public void LaunchUpdater(string stagedFolder)
    {
        if (string.IsNullOrWhiteSpace(stagedFolder) || !Directory.Exists(stagedFolder))
        {
            throw new DomainException("Le dossier de mise à jour préparé est introuvable.");
        }

        var executable = Executable();
        var install = Path.GetDirectoryName(executable);
        if (string.IsNullOrEmpty(install))
        {
            throw new DomainException("Le dossier d’installation n’a pas pu être déterminé.");
        }

        // Le dossier préparé porte l'installeur quand la mise à jour passe par le MSI.
        var package = Path.Combine(stagedFolder, InstallerName);
        var script = File.Exists(package)
            ? InstallerScript(stagedFolder, package, executable)
            : Script(stagedFolder, install, executable);

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
    /// Script de remplacement : il attend la fin du processus courant, recopie le dossier
    /// préparé sur l'installation, relance l'application puis se nettoie.
    /// </summary>
    private static string Script(string staged, string install, string executable)
    {
        var text = Preamble(executable);
        text.Append("set \"STAGED=").Append(Trim(staged)).Append("\"\r\n");
        text.Append("set \"INSTALL=").Append(Trim(install)).Append("\"\r\n");
        Wait(text);
        text.Append("robocopy \"%STAGED%\" \"%INSTALL%\" /MIR /R:3 /W:2 /NFL /NDL /NJH /NJS /NP >>\"%LOG%\" 2>&1\r\n");
        text.Append("if errorlevel 8 (\r\n");
        // La fenêtre est masquée : la trace utile va dans le journal, pas sur la console.
        text.Append("  echo [7pace auto] copie de la mise a jour en echec, installation inchangee. >>\"%LOG%\"\r\n");
        text.Append("  start \"\" \"%EXE%\"\r\n");
        text.Append("  exit /b 1\r\n");
        text.Append(")\r\n");
        Finish(text);
        return text.ToString();
    }

    /// <summary>
    /// Script d'installation : même forme, msiexec à la place de robocopy. Le paquet est par
    /// utilisateur, donc msiexec n'élève rien ; « Applications installées » suit la version
    /// parce que c'est Windows Installer qui pose les fichiers. Un code de retour non nul
    /// laisse la version précédente en place et relance l'exécutable d'avant.
    /// </summary>
    private static string InstallerScript(string staged, string package, string executable)
    {
        var text = Preamble(executable);
        text.Append("set \"STAGED=").Append(Trim(staged)).Append("\"\r\n");
        text.Append("set \"MSI=").Append(package).Append("\"\r\n");
        text.Append("set \"MSILOG=").Append(InstallerLogPath).Append("\"\r\n");
        text.Append("set \"MSIEXEC=").Append(Msiexec).Append("\"\r\n");
        Wait(text);
        text.Append("echo [7pace auto] installation de la mise a jour en cours. >>\"%LOG%\"\r\n");
        // /qn : aucune interface, donc aucune fenêtre de Windows Installer jetée à la figure
        // de l'utilisateur — l'application dit elle-même où elle en est. /norestart : jamais
        // de redémarrage. /l*v : le détail part dans un journal, seul témoin d'un échec.
        text.Append("\"%MSIEXEC%\" /i \"%MSI%\" /qn /norestart /l*v \"%MSILOG%\"\r\n");
        text.Append("set \"CODE=%ERRORLEVEL%\"\r\n");
        // Branchement par goto, sans bloc entre parenthèses : cmd développe les variables en
        // lisant le bloc entier, et une parenthèse venue d'un message le refermerait trop tôt.
        text.Append("if \"%CODE%\"==\"0\" goto pose\r\n");
        text.Append("echo [7pace auto] installation de la mise a jour en echec, code msiexec %CODE%, version precedente conservee. >>\"%LOG%\"\r\n");
        text.Append("echo [7pace auto] detail dans \"%MSILOG%\". >>\"%LOG%\"\r\n");
        text.Append("start \"\" \"%EXE%\"\r\n");
        text.Append("exit /b %CODE%\r\n");
        text.Append(":pose\r\n");
        text.Append("echo [7pace auto] installation de la mise a jour terminee. >>\"%LOG%\"\r\n");
        Finish(text);
        return text.ToString();
    }

    /// <summary>En-tête commun aux deux scripts : encodage, processus à attendre, journal.</summary>
    private static StringBuilder Preamble(string executable)
    {
        var text = new StringBuilder();
        text.Append("@echo off\r\n");
        // Les chemins peuvent contenir des accents : le script est écrit en UTF-8.
        text.Append("chcp 65001 >nul 2>&1\r\n");
        text.Append("setlocal\r\n");
        text.Append("set \"PID=").Append(Environment.ProcessId.ToString(CultureInfo.InvariantCulture)).Append("\"\r\n");
        text.Append("set \"EXE=").Append(executable).Append("\"\r\n");
        text.Append("set \"LOG=").Append(LogPath).Append("\"\r\n");
        return text;
    }

    /// <summary>Attente de la fermeture de l'application : rien n'est touché avant.</summary>
    private static void Wait(StringBuilder text)
    {
        text.Append(":wait\r\n");
        text.Append("tasklist /FI \"PID eq %PID%\" /NH 2>nul | findstr /I /C:\"SeptPaceAuto\" >nul\r\n");
        text.Append("if not errorlevel 1 (\r\n");
        text.Append("  ping -n 2 127.0.0.1 >nul\r\n");
        text.Append("  goto wait\r\n");
        text.Append(")\r\n");
    }

    /// <summary>Relance l'application, efface le dossier préparé puis le script lui-même.</summary>
    private static void Finish(StringBuilder text)
    {
        text.Append("start \"\" \"%EXE%\"\r\n");
        text.Append("rmdir /s /q \"%STAGED%\" >nul 2>&1\r\n");
        text.Append("del /f /q \"%~f0\" >nul 2>&1\r\n");
    }

    /// <summary>msiexec nommé en entier : le script ne dépend pas du PATH de la session.</summary>
    private static string Msiexec =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe");

    /// <summary>Robocopy refuse un dossier terminé par une barre oblique inverse.</summary>
    private static string Trim(string folder) => folder.TrimEnd('\\', '/');

    private static string Executable()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        return Path.Combine(AppContext.BaseDirectory.TrimEnd('\\'), ExecutableName);
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

    /// <summary>
    /// Windows Installer, juste ce qu'il faut pour répondre à « cette installation vient-elle
    /// de l'installeur ? ». La question passe par msi.dll et non par la ruche de
    /// désinstallation : un paquet par utilisateur n'écrit rien sous
    /// HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall (vérifié sur ce poste : aucune
    /// entrée, alors que « Applications installées » montre bien le produit), Windows
    /// reconstruisant la ligne depuis son propre registre d'installations aux GUID compactés.
    /// L'API est la source documentée, stable d'une version à l'autre et lisible sans droits
    /// particuliers ; la même est déjà interrogée par build\install.ps1.
    /// </summary>
    private static class WindowsInstaller
    {
        /// <summary>UpgradeCode figé de build\installer\Package.wxs : il survit aux versions.</summary>
        private const string UpgradeCode = "{045FED9F-20D2-4660-9932-964B6A2345F4}";

        private const int Success = 0;
        private const int MoreData = 234;

        /// <summary>Un code de produit fait 38 caractères, plus le zéro final.</summary>
        private const int ProductCodeLength = 39;

        [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int MsiEnumRelatedProductsW(string upgradeCode, int reserved, int index, [Out] char[] productCode);

        [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int MsiGetProductInfoW(string productCode, string property, [Out] char[] value, ref int size);

        /// <summary>
        /// Vrai quand un produit portant notre UpgradeCode possède le dossier de
        /// <paramref name="executable"/> : c'est alors msiexec qui doit poser la suite.
        /// </summary>
        public static bool Manages(string executable)
        {
            var folder = Path.GetDirectoryName(executable);
            if (string.IsNullOrEmpty(folder)) return false;

            try
            {
                // MajorUpgrade n'en laisse qu'un, mais l'énumération est la seule forme offerte.
                var code = new char[ProductCodeLength];
                for (var index = 0; MsiEnumRelatedProductsW(UpgradeCode, 0, index, code) == Success; index++)
                {
                    if (Owns(new string(code).TrimEnd('\0'), folder)) return true;
                }

                return false;
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
            {
                // Sans Windows Installer il n'y a pas d'installation MSI : l'archive reprend la main.
                return false;
            }
        }

        private static bool Owns(string product, string folder)
        {
            // ARPINSTALLLOCATION n'est pas garanti : les paquets qui ne l'inscrivent pas rendent
            // InstallLocation vide. Le repli est le dossier figé de Package.wxs, le seul où ce
            // paquet par utilisateur s'installe.
            var location = Info(product, "InstallLocation");
            return Contains(location.Length == 0 ? PerUserFolder : location, folder);
        }

        /// <summary>%LOCALAPPDATA%\Programs\7pace auto, le dossier écrit dans Package.wxs.</summary>
        private static string PerUserFolder => Path.Combine(AppPaths.LocalAppData, "Programs", "7pace auto");

        /// <summary>Le dossier d'installation est-il celui de l'exécutable, ou au-dessus ?</summary>
        private static bool Contains(string install, string folder)
        {
            try
            {
                var owner = Path.GetFullPath(install).TrimEnd('\\', '/');
                var candidate = Path.GetFullPath(folder).TrimEnd('\\', '/');
                if (owner.Length == 0) return false;

                return string.Equals(owner, candidate, StringComparison.OrdinalIgnoreCase) ||
                    candidate.StartsWith(owner + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }

        /// <summary>
        /// Propriété d'un produit installé ; vide quand elle n'est pas inscrite. Le tampon est
        /// dimensionné large puis agrandi sur demande de Windows Installer.
        /// </summary>
        private static string Info(string product, string property)
        {
            var value = new char[512];
            var size = value.Length;
            var status = MsiGetProductInfoW(product, property, value, ref size);

            if (status == MoreData)
            {
                value = new char[size + 1];
                size = value.Length;
                status = MsiGetProductInfoW(product, property, value, ref size);
            }

            return status == Success ? new string(value, 0, Math.Min(size, value.Length)) : string.Empty;
        }
    }
}
