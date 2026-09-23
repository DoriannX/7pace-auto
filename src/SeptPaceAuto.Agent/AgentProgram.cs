using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using SeptPaceAuto.Services;

namespace SeptPaceAuto.Agent;

/// <summary>
/// Collecteur de fond : le seul processus qui relève la branche Git, tient les journées et
/// écrit sur le disque. Il n'a pas de fenêtre et survit à la fermeture de l'app — c'est tout
/// l'intérêt de l'avoir séparé.
///
/// Un collecteur par profil de données, jamais deux : deux verrous nommés le garantissent,
/// dont celui de l'ancien terminal tout-en-un, pour qu'une version 1.0.9 restée installée ne
/// puisse pas écrire dans les mêmes journées.
///
/// Sans argument, il sert. Avec « --stop », il demande l'arrêt propre du collecteur en
/// place ; avec « --status », il dit ce qui tourne. Ces deux formes écrivent dans la console
/// de l'appelant, ce qui rend les scripts d'installation lisibles.
/// </summary>
internal static class AgentProgram
{
    private const int AlreadyRunning = 2;

    public static async Task<int> RunAsync(string[] args)
    {
        var endpoint = AgentEndpoint.Default;
        using var lifetime = new CancellationTokenSource();

        if (args.Length == 0) return await ServeAsync(endpoint, lifetime);

        Speak();
        return args[0].TrimStart('-', '/').ToLowerInvariant() switch
        {
            "stop" => await StopAsync(endpoint, lifetime.Token),
            "status" => await StatusAsync(endpoint, lifetime.Token),
            "help" or "?" => Help(),
            _ => Unknown(args[0]),
        };
    }

    // ---------- service ----------

    private static async Task<int> ServeAsync(AgentEndpoint endpoint, CancellationTokenSource lifetime)
    {
        // Aucune console ne doit pouvoir emporter le collecteur : s'il en a hérité une, il
        // s'en détache avant de collecter quoi que ce soit.
        Detach();

        // Le verrou du collecteur : un second lancement se retire sans toucher aux journées.
        using var mine = new Mutex(initiallyOwned: true, endpoint.MutexName, out var owned);
        if (!owned) return AlreadyRunning;

        // Le verrou de l'ancien terminal tout-en-un : deux collectes ne se superposent pas.
        using var legacy = new Mutex(initiallyOwned: true, endpoint.LegacyMutexName, out var alone);
        if (!alone) return AlreadyRunning;

        var settled = new ManualResetEventSlim(false);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            // Fermeture de session ou arrêt du poste : on demande la sortie propre et on lui
            // laisse le temps d'écrire la dernière minute.
            lifetime.Cancel();
            settled.Wait(TimeSpan.FromSeconds(8));
        };

        try
        {
            Directory.CreateDirectory(endpoint.DataFolder);
            await using var app = TrackingAppFactory.Create();
            await app.StartAsync(lifetime.Token);

            await using var host = new AgentHost(app, endpoint, AgentInfo.Describe(endpoint));
            host.Start();

            await Task.WhenAny(host.StopRequested, Cancelled(lifetime.Token)).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        finally
        {
            settled.Set();
            settled.Dispose();
        }
    }

    private static Task Cancelled(CancellationToken ct)
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => waiting.TrySetResult());
        return waiting.Task;
    }

    // ---------- commandes d'outillage ----------

    private static async Task<int> StopAsync(AgentEndpoint endpoint, CancellationToken ct)
    {
        await using var client = new AgentClient(endpoint, new NoLauncher());
        if (!await client.ConnectAsync(launchIfMissing: false, ct))
        {
            Console.WriteLine("Aucun collecteur ne tourne pour ce profil de données.");
            return 0;
        }

        try
        {
            Console.WriteLine(await client.StopAgentAsync(ct));
            return 0;
        }
        catch (DomainException error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static async Task<int> StatusAsync(AgentEndpoint endpoint, CancellationToken ct)
    {
        await using var client = new AgentClient(endpoint, new NoLauncher());
        if (!await client.ConnectAsync(launchIfMissing: false, ct))
        {
            Console.WriteLine($"Aucun collecteur ne tourne pour {endpoint.DataFolder}.");
            return 1;
        }

        var agent = client.Agent;
        Console.WriteLine($"Collecteur actif · version {agent?.Version} · PID {agent?.Pid}");
        Console.WriteLine($"  Profil  : {endpoint.DataFolder}");
        Console.WriteLine($"  Liaison : {endpoint.PipeName} (protocole {agent?.Protocol})");
        if (agent?.Started is DateTimeOffset started)
        {
            Console.WriteLine($"  Démarré : {started.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}");
        }
        return 0;
    }

    private static int Help()
    {
        Console.WriteLine("SeptPaceAuto.Agent — collecteur de fond de 7pace auto.");
        Console.WriteLine("  (sans argument)  démarre le collecteur, sans fenêtre.");
        Console.WriteLine("  --status         indique le collecteur en place pour ce profil.");
        Console.WriteLine("  --stop           arrête proprement le collecteur en place.");
        return 0;
    }

    private static int Unknown(string argument)
    {
        Console.Error.WriteLine($"Argument inconnu : {argument}. Utilise --status, --stop ou --help.");
        return 1;
    }

    /// <summary>Collecteur déjà lancé par ailleurs : ces commandes ne doivent rien démarrer.</summary>
    private sealed class NoLauncher : IAgentLauncher
    {
        public string? Executable => null;

        public bool Launch() => false;
    }

    // ---------- console empruntée ----------

    /// <summary>
    /// Le collecteur est une application sans console : pour répondre à « --status » ou
    /// « --stop », il emprunte celle de l'appelant. Sans appelant, rien ne s'affiche, et
    /// c'est le code de sortie qui renseigne les scripts.
    /// </summary>
    private static void Speak()
    {
        if (!AttachConsole(unchecked((uint)-1))) return;
        try
        {
            var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            Console.SetOut(output);
            var errors = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true };
            Console.SetError(errors);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Sans console utilisable, le code de sortie reste le seul retour.
        }
    }

    /// <summary>Quitte la console éventuellement héritée : sa fermeture ne tuera pas le suivi.</summary>
    private static void Detach()
    {
        try
        {
            if (GetConsoleWindow() != IntPtr.Zero) FreeConsole();
        }
        catch (EntryPointNotFoundException)
        {
            // Sans console à quitter, il n'y a rien à faire.
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();
}
