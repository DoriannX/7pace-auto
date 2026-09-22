#nullable enable
using System.IO.Pipes;
using SeptPaceAuto.Services;

namespace SeptPaceAuto.Tests;

/// <summary>Cœur métier scénarisé : le test décide de chaque réponse et mesure l'entrelacement.</summary>
internal sealed class ScriptedApp : ITrackingApp
{
    private readonly object _gate = new();
    private int _inside;

    public Func<string, string, CancellationToken, Task<string>> Handler { get; set; } =
        static (method, _, _) => Task.FromResult("{\"echo\":\"" + method + "\"}");

    public List<string> Seen { get; } = new();

    /// <summary>Plus grand nombre d'appels servis en même temps : la sérialisation s'y voit.</summary>
    public int Peak { get; private set; }

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task<string> HandleAsync(string method, string paramsJson, CancellationToken ct)
    {
        lock (_gate)
        {
            Seen.Add(method);
            _inside++;
            if (_inside > Peak) Peak = _inside;
        }
        try
        {
            return await Handler(method, paramsJson, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) _inside--;
        }
    }

    public int Count(string method)
    {
        lock (_gate) return Seen.Count(seen => string.Equals(seen, method, StringComparison.Ordinal));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Collecteur monté en mémoire sur un profil de données temporaire : vrai tuyau nommé, vrai
/// protocole, mais aucun processus à lancer.
/// </summary>
internal sealed class HostHarness : IAsyncDisposable
{
    private readonly List<AgentClient> _clients = new();

    public HostHarness()
    {
        Folder = Path.Combine(Path.GetTempPath(), "7pace-auto-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
        Endpoint = AgentEndpoint.For(Folder);
        Host = Serve();
    }

    public string Folder { get; }

    public AgentEndpoint Endpoint { get; }

    public AgentHost Host { get; private set; }

    public ScriptedApp App { get; } = new();

    private AgentHost Serve()
    {
        var host = new AgentHost(App, Endpoint, AgentInfo.Describe(Endpoint));
        host.Start();
        return host;
    }

    /// <summary>Redémarre le collecteur sur le même profil, comme après une mise à jour.</summary>
    public async Task RestartAsync()
    {
        await Host.DisposeAsync();
        Host = Serve();
    }

    public AgentClient Client(TimeSpan? patience = null)
    {
        var client = new AgentClient(Endpoint, new DeadLauncher(), patience is TimeSpan delay ? _ => delay : null);
        _clients.Add(client);
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients) await client.DisposeAsync();
        await Host.DisposeAsync();
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Un dossier temporaire qui survit ne fait échouer aucun test.
        }
    }
}

/// <summary>Lanceur qui ne lance rien : le test contrôle seul la présence d'un collecteur.</summary>
internal sealed class DeadLauncher : IAgentLauncher
{
    public string? Executable => null;

    public bool Launch() => false;
}

/// <summary>
/// Faux collecteur qui répond à la poignée de main puis coupe la liaison au premier appel
/// utile : de quoi éprouver une coupure survenue en plein échange.
/// </summary>
internal sealed class RudeHost : IAsyncDisposable
{
    private readonly CancellationTokenSource _life = new();
    private readonly Task _loop;

    public RudeHost(AgentEndpoint endpoint)
    {
        _loop = Task.Run(() => ServeAsync(endpoint.PipeName, _life.Token));
    }

    private static async Task ServeAsync(string pipeName, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try
            {
                await pipe.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(pipe, AgentWire.Utf8, false, 4096, leaveOpen: true);
                var writer = new StreamWriter(pipe, AgentWire.Utf8, 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };

                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line is null) break;
                    if (!AgentWire.TryReadCall(line, out var call, out _)) break;

                    if (!call.Method.StartsWith("agent.", StringComparison.Ordinal)) break;
                    await writer.WriteLineAsync(AgentWire.Encode(new AgentReply(call.Id, true, "{}", string.Empty)));
                }
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
            {
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _life.Cancel();
        try
        {
            await _loop;
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Arrêt demandé.
        }
        _life.Dispose();
    }
}
