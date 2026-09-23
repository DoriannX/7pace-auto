#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SeptPaceAuto.Services;

/// <summary>
/// Accès au cœur métier depuis un client .NET (outillage du collecteur, tests). Il ne collecte rien : il parle au
/// collecteur de fond, et dit clairement quand il ne l'atteint plus.
/// </summary>
public interface ITrackingChannel : ITrackingApp
{
    /// <summary>Vrai tant que le tuyau du collecteur répond.</summary>
    bool Connected { get; }

    /// <summary>Carte de visite du collecteur joint, nulle tant qu'aucun n'a répondu.</summary>
    AgentInfo? Agent { get; }

    /// <summary>Dernière raison connue d'une perte de liaison, déjà rédigée pour l'utilisateur.</summary>
    string? Trouble { get; }

    /// <param name="launchIfMissing">Démarre le collecteur en silence quand aucun ne tourne.</param>
    Task<bool> ConnectAsync(bool launchIfMissing, CancellationToken ct);

    /// <summary>Demande l'arrêt complet du suivi de fond et retourne la phrase à afficher.</summary>
    Task<string> StopAgentAsync(CancellationToken ct);
}

/// <summary>Démarrage du collecteur, isolé pour que les tests n'aient pas à lancer un processus.</summary>
public interface IAgentLauncher
{
    /// <summary>Exécutable trouvé sur ce poste, null quand il est introuvable.</summary>
    string? Executable { get; }

    /// <summary>Lance le collecteur sans fenêtre. Faux quand rien n'a pu être lancé.</summary>
    bool Launch();
}

/// <summary>
/// Collecteur installé à côté du client. SEPTPACE_AGENT permet de désigner un autre
/// exécutable : c'est ce que font les tests, sur un profil de données temporaire.
/// </summary>
public sealed class AgentLauncher : IAgentLauncher
{
    public string? Executable { get; } = Find();

    public static string? Find()
    {
        var declared = Environment.GetEnvironmentVariable("SEPTPACE_AGENT");
        if (!string.IsNullOrWhiteSpace(declared) && File.Exists(declared)) return Path.GetFullPath(declared);

        try
        {
            var beside = Path.Combine(AppContext.BaseDirectory, AgentEndpoint.AgentExecutable);
            return File.Exists(beside) ? beside : null;
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool Launch()
    {
        if (Executable is not string path) return false;

        // Lancement par le shell, volontairement : le collecteur n'hérite alors ni de la
        // console du client ni de ses tuyaux. Sans cela, il resterait accroché à la
        // fenêtre qui l'a démarré — exactement ce que cette séparation vise à supprimer —
        // et garderait ouvertes des sorties qui ne le regardent pas. Il hérite en revanche
        // de l'environnement, donc du profil SEPTPACE_DATA du client.
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(path) ?? AppContext.BaseDirectory,
        };

        try
        {
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}

/// <summary>
/// Client .NET du tuyau. Il retrouve le collecteur du profil actif, lui transmet
/// les appels du contrat et rend les réponses telles quelles.
///
/// Une liaison coupée n'est jamais cachée : la reconnexion est tentée avant chaque appel,
/// mais seuls les appels de lecture sont rejoués. Une correction ou un envoi interrompu
/// remonte à l'utilisateur plutôt que d'être renvoyé à l'aveugle, pour qu'aucune minute ne
/// soit écrite deux fois.
/// </summary>
public sealed class AgentClient : ITrackingChannel
{
    /// <summary>Attente d'un tuyau déjà ouvert : s'il existe, il répond tout de suite.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Délai laissé à un collecteur qui vient d'être lancé pour ouvrir son tuyau.</summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(25);

    private readonly AgentEndpoint _endpoint;
    private readonly IAgentLauncher _launcher;
    private readonly Func<string, TimeSpan> _patience;
    private readonly SemaphoreSlim _turn = new(1, 1);

    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private long _nextId;

    public AgentClient(AgentEndpoint? endpoint = null, IAgentLauncher? launcher = null)
        : this(endpoint, launcher, null)
    {
    }

    /// <param name="patience">Délai accordé par appel ; les tests le raccourcissent.</param>
    internal AgentClient(AgentEndpoint? endpoint, IAgentLauncher? launcher, Func<string, TimeSpan>? patience)
    {
        _endpoint = endpoint ?? AgentEndpoint.Default;
        _launcher = launcher ?? new AgentLauncher();
        _patience = patience ?? TimeoutFor;
    }

    public bool Connected => _pipe is { IsConnected: true };

    public AgentInfo? Agent { get; private set; }

    public string? Trouble { get; private set; }

    /// <summary>Le client démarre : il rejoint le collecteur, et le lance s'il n'existe pas.</summary>
    public async Task StartAsync(CancellationToken ct) => await ConnectAsync(launchIfMissing: true, ct).ConfigureAwait(false);

    public async Task<bool> ConnectAsync(bool launchIfMissing, CancellationToken ct)
    {
        await _turn.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await EnsureAsync(launchIfMissing, ct).ConfigureAwait(false);
        }
        finally
        {
            _turn.Release();
        }
    }

    public async Task<string> HandleAsync(string method, string paramsJson, CancellationToken ct)
    {
        await _turn.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!await EnsureAsync(launchIfMissing: false, ct).ConfigureAwait(false))
            {
                throw new DomainException(Unreachable());
            }

            try
            {
                return await ExchangeAsync(method, paramsJson, ct).ConfigureAwait(false);
            }
            catch (DomainException)
            {
                throw;
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException or TimeoutException)
            {
                Drop(Explain(error));
                if (!CanReplay(method)) throw new DomainException(Interrupted(method, error));
                if (!await EnsureAsync(launchIfMissing: false, ct).ConfigureAwait(false)) throw new DomainException(Unreachable());

                try
                {
                    return await ExchangeAsync(method, paramsJson, ct).ConfigureAwait(false);
                }
                catch (Exception second) when (second is IOException or ObjectDisposedException or InvalidOperationException or TimeoutException)
                {
                    Drop(Explain(second));
                    throw new DomainException(Interrupted(method, second));
                }
            }
        }
        finally
        {
            _turn.Release();
        }
    }

    public async Task<string> StopAgentAsync(CancellationToken ct)
    {
        var response = await HandleAsync("agent.stop", "{}", ct).ConfigureAwait(false);
        await WaitForSilenceAsync(ct).ConfigureAwait(false);

        try
        {
            using var document = JsonDocument.Parse(response);
            if (document.RootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            // La réponse exacte importe peu : l'arrêt est déjà accepté.
        }
        return "Suivi en arrière-plan arrêté.";
    }

    // ---------- liaison ----------

    private async Task<bool> EnsureAsync(bool launchIfMissing, CancellationToken ct)
    {
        if (Connected) return true;
        Drop(Trouble);

        if (await TryConnectAsync(ConnectTimeout, ct).ConfigureAwait(false)) return true;
        if (!launchIfMissing) return false;

        if (!_launcher.Launch())
        {
            Trouble = _launcher.Executable is null
                ? $"{AgentEndpoint.AgentExecutable} est introuvable à côté du client : réinstalle l’application avec build\\install.ps1."
                : "Le collecteur n’a pas pu être lancé sur ce poste.";
            return false;
        }

        var deadline = DateTime.UtcNow + StartTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await TryConnectAsync(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false)) return true;
            await Task.Delay(TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false);
        }

        Trouble = "Le collecteur a été lancé mais n’a pas ouvert sa liaison à temps.";
        return false;
    }

    private async Task<bool> TryConnectAsync(TimeSpan timeout, CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", _endpoint.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync((int)timeout.TotalMilliseconds, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception error) when (error is TimeoutException or IOException or UnauthorizedAccessException or ObjectDisposedException or OperationCanceledException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            Trouble = error is UnauthorizedAccessException
                ? "Le collecteur de ce profil appartient à un autre compte Windows."
                : "Aucun collecteur ne répond pour ce profil de données.";
            return false;
        }

        _pipe = pipe;
        _reader = new StreamReader(pipe, AgentWire.Utf8, detectEncodingFromByteOrderMarks: false, 1 << 14, leaveOpen: true);
        _writer = new StreamWriter(pipe, AgentWire.Utf8, 1 << 14, leaveOpen: true) { AutoFlush = false, NewLine = "\n" };

        try
        {
            var hello = await ExchangeAsync("agent.hello", "{}", ct).ConfigureAwait(false);
            Agent = JsonSerializer.Deserialize<AgentInfo>(hello, Json.Wire);
            Trouble = null;
            return true;
        }
        catch (DomainException error)
        {
            Drop(error.Message);
            return false;
        }
        catch (Exception error) when (error is IOException or JsonException or ObjectDisposedException or InvalidOperationException or TimeoutException)
        {
            Drop(Explain(error));
            return false;
        }
    }

    private async Task<string> ExchangeAsync(string method, string paramsJson, CancellationToken ct)
    {
        var writer = _writer ?? throw new IOException("Liaison fermée.");
        var reader = _reader ?? throw new IOException("Liaison fermée.");

        var identifier = Interlocked.Increment(ref _nextId);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_patience(method));

        try
        {
            await writer.WriteLineAsync(AgentWire.Encode(new AgentCall(identifier, method, paramsJson)).AsMemory(), deadline.Token).ConfigureAwait(false);
            await writer.FlushAsync(deadline.Token).ConfigureAwait(false);

            while (true)
            {
                var line = await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false);
                if (line is null) throw new IOException("Le collecteur a fermé la liaison.");
                if (line.Length == 0) continue;

                if (!AgentWire.TryReadReply(line, out var reply, out var problem))
                {
                    throw new DomainException(problem);
                }
                // Une réponse retardataire d'un appel abandonné ne doit pas être prise pour celle-ci.
                if (reply.Id != identifier && reply.Id != 0) continue;

                if (reply.Ok) return reply.Payload;
                throw new DomainException(reply.Payload);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Le collecteur n’a pas répondu à « {method} » dans le délai prévu.");
        }
    }

    /// <summary>Attend que le tuyau cesse de répondre : l'arrêt demandé est alors effectif.</summary>
    private async Task WaitForSilenceAsync(CancellationToken ct)
    {
        Drop(null);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            using var probe = new NamedPipeClientStream(".", _endpoint.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await probe.ConnectAsync(200, ct).ConfigureAwait(false);
            }
            catch (Exception error) when (error is TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                Agent = null;
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false);
        }
        Agent = null;
    }

    private void Drop(string? reason)
    {
        Trouble = reason;
        try
        {
            _writer?.Dispose();
            _reader?.Dispose();
            _pipe?.Dispose();
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // La liaison est déjà perdue : c'est exactement ce qu'on voulait.
        }
        _writer = null;
        _reader = null;
        _pipe = null;
    }

    private string Unreachable() => Trouble is { Length: > 0 } reason
        ? $"Collecteur injoignable : {reason}"
        : "Collecteur injoignable : le suivi de fond ne répond plus.";

    private static string Interrupted(string method, Exception error) =>
        $"La liaison avec le collecteur s’est coupée pendant « {method} » ({Explain(error)}). " +
        "Rouvre la journée pour voir ce qui a été enregistré avant de recommencer.";

    private static string Explain(Exception error) => error switch
    {
        TimeoutException => "pas de réponse dans le délai prévu",
        _ => "tuyau fermé",
    };

    /// <summary>
    /// Appels que l'on peut rejouer sans risque : ils ne modifient ni la journée, ni les
    /// réglages, ni 7pace.
    /// </summary>
    internal static bool CanReplay(string method) =>
        method is "bootstrap" or "currentDay" or "loadSettings" or "checkUpdate" or "probeRepo" or "probeAzure" or "probeToken"
        || method.StartsWith("agent.", StringComparison.Ordinal);

    /// <summary>
    /// Patience accordée par appel. Une lecture doit répondre tout de suite ; un envoi, une
    /// résolution Azure ou un téléchargement ont le droit d'être longs.
    /// </summary>
    private static TimeSpan TimeoutFor(string method) => method switch
    {
        "applyUpdate" => TimeSpan.FromMinutes(15),
        "pendingDay" or "saveSettings" or "submitDay" or "checkUpdate" => TimeSpan.FromMinutes(5),
        "probeRepo" or "probeAzure" or "probeToken" => TimeSpan.FromMinutes(2),
        _ when method.StartsWith("agent.", StringComparison.Ordinal) => TimeSpan.FromSeconds(15),
        _ => TimeSpan.FromSeconds(60),
    };

    public ValueTask DisposeAsync()
    {
        // Fermer le client ne touche pas au collecteur : seule la liaison se referme.
        Drop(null);
        _turn.Dispose();
        return ValueTask.CompletedTask;
    }
}
