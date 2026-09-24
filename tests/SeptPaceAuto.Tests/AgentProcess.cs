#nullable enable
using System.Diagnostics;
using System.Text.Json;
using SeptPaceAuto.Services;

namespace SeptPaceAuto.Tests;

/// <summary>
/// Profil de données temporaire servi par un vrai collecteur, lancé comme sur le poste de
/// l'utilisateur. Rien n'y touche l'installation réelle : SEPTPACE_DATA est imposé au
/// processus fils, et le dossier disparaît avec le test.
/// </summary>
internal sealed class AgentProcessProfile : IAsyncDisposable
{
    private readonly List<Process> _started = new();

    public AgentProcessProfile(int pollSeconds = 10)
    {
        Folder = Path.Combine(Path.GetTempPath(), "7pace-auto-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
        Endpoint = AgentEndpoint.For(Folder);

        // Cadence la plus vive autorisée : les preuves de vie arrivent sans faire traîner les tests.
        File.WriteAllText(
            Path.Combine(Folder, "settings.json"),
            JsonSerializer.Serialize(new AppSettings { PollSeconds = pollSeconds }, Json.Pretty));
    }

    public string Folder { get; }

    public AgentEndpoint Endpoint { get; }

    /// <summary>Collecteur publié à côté des tests par build/AgentOutput.targets.</summary>
    public static string AgentExecutable { get; } =
        Path.Combine(AppContext.BaseDirectory, AgentEndpoint.AgentExecutable);

    public AgentClient Client() => new(Endpoint, new ProcessLauncher(this));

    /// <summary>Lance un processus du projet sur ce profil, sans fenêtre, sorties captées.</summary>
    public Process Start(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["SEPTPACE_DATA"] = Folder;
        start.Environment["SEPTPACE_AGENT"] = AgentExecutable;
        // Sans marque d'ordre d'octets à l'écriture, en UTF-8 à la lecture : les accents de
        // --status arrivent tels quels.
        start.StandardInputEncoding = new System.Text.UTF8Encoding(false);
        start.StandardOutputEncoding = new System.Text.UTF8Encoding(false);

        var process = Process.Start(start) ?? throw new InvalidOperationException($"{executable} n’a pas démarré.");
        lock (_started) _started.Add(process);
        return process;
    }

    public List<Entry> Day(string date) =>
        new DayStore(static () => Profile.Create(new AppSettings(), strict: false), Path.Combine(Folder, "days")).Day(date);

    /// <summary>Contenu brut d'un fichier du profil, vide quand il n'existe pas encore.</summary>
    public string Read(string fileName)
    {
        var path = Path.Combine(Folder, fileName);
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    /// <summary>Contenu brut du battement : il change à chaque relevé mené à son terme.</summary>
    public string Heartbeat() => Read("heartbeat.json");

    /// <summary>Attend une condition en scrutant : les preuves de vie sont asynchrones.</summary>
    public static async Task<bool> WaitFor(Func<bool> until, TimeSpan patience)
    {
        var deadline = DateTime.UtcNow + patience;
        while (DateTime.UtcNow < deadline)
        {
            if (until()) return true;
            await Task.Delay(250);
        }
        return until();
    }

    public static async Task<bool> Exited(Process process, TimeSpan patience)
    {
        try
        {
            using var deadline = new CancellationTokenSource(patience);
            await process.WaitForExitAsync(deadline.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Un collecteur démarré par le client n'est pas dans la liste : on le congédie par
        // son propre protocole, sinon il survit au test et verrouille les binaires.
        try
        {
            var reste = new AgentClient(Endpoint, new ProcessLauncher(this));
            await using (reste)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                if (await reste.ConnectAsync(launchIfMissing: false, deadline.Token))
                {
                    await reste.StopAgentAsync(deadline.Token);
                }
            }
        }
        catch (Exception error) when (error is DomainException or OperationCanceledException or IOException)
        {
            // Le collecteur était déjà parti : c'est le résultat recherché.
        }

        foreach (var process in _started)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
            }
            catch (Exception error) when (error is InvalidOperationException or NotSupportedException or OperationCanceledException or System.ComponentModel.Win32Exception)
            {
                // Le processus est déjà parti : c'est ce qu'on voulait.
            }
            process.Dispose();
        }

        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Un dossier temporaire qui survit ne fait échouer aucun test.
        }
    }

    /// <summary>Lanceur du client, reproduit à l'identique mais cadré sur le profil du test.</summary>
    private sealed class ProcessLauncher : IAgentLauncher
    {
        private readonly AgentProcessProfile _profile;

        public ProcessLauncher(AgentProcessProfile profile) => _profile = profile;

        public string? Executable => File.Exists(AgentExecutable) ? AgentExecutable : null;

        public bool Launch()
        {
            if (Executable is null) return false;
            _profile.Start(AgentExecutable);
            return true;
        }
    }
}
