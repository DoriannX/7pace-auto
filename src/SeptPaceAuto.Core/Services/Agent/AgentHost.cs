#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SeptPaceAuto.Services;

/// <summary>
/// Façade du collecteur sur le tuyau nommé : elle reçoit les appels des terminaux et les
/// passe au cœur métier, seul propriétaire des écritures.
///
/// Le tuyau n'est ouvert qu'au compte Windows qui l'a créé : la liste d'accès ne contient
/// que son identifiant, et l'identité du client est revérifiée à chaque connexion. Deux
/// sessions Windows différentes ne se voient donc jamais.
///
/// Les appels qui écrivent sont sérialisés : deux terminaux ouverts en même temps ne
/// peuvent pas entrelacer une correction et un envoi. Les appels de lecture passent sans
/// attendre, pour que la consultation de la journée en cours reste vivante pendant un
/// téléchargement de mise à jour.
/// </summary>
internal sealed class AgentHost : IAsyncDisposable
{
    /// <summary>Terminaux servis en parallèle. Au-delà, une connexion attend son tour.</summary>
    private const int MaxClients = 8;

    /// <summary>Méthodes qui ne touchent ni le disque ni 7pace : elles ne prennent pas le verrou.</summary>
    private static readonly HashSet<string> ReadOnly = new(StringComparer.Ordinal)
    {
        "bootstrap", "currentDay", "loadSettings", "checkUpdate", "probeRepo", "probeAzure", "probeToken",
    };

    private readonly ITrackingApp _app;
    private readonly AgentEndpoint _endpoint;
    private readonly AgentInfo _identity;
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly CancellationTokenSource _life = new();
    private readonly TaskCompletionSource<bool> _stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Task> _workers = new();
    private readonly string _owner;

    private int _disposed;

    public AgentHost(ITrackingApp app, AgentEndpoint endpoint, AgentInfo identity)
    {
        _app = app;
        _endpoint = endpoint;
        _identity = identity;
        _owner = Owner();
    }

    /// <summary>Aboutit quand un terminal a demandé l'arrêt complet du suivi.</summary>
    public Task<bool> StopRequested => _stop.Task;

    /// <summary>Ouvre le tuyau et dépose la présence du collecteur dans le profil.</summary>
    public void Start()
    {
        PublishInfo();
        for (var index = 0; index < MaxClients; index++)
        {
            _workers.Add(Task.Run(() => AcceptAsync(_life.Token), CancellationToken.None));
        }
    }

    // ---------- accueil des terminaux ----------

    private async Task AcceptAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = Listen();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Le tuyau est saturé ou momentanément indisponible : on retente sans bruit.
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException)
            {
                pipe.Dispose();
                return;
            }
            catch (IOException)
            {
                // Client parti avant d'être servi : l'instance est perdue, on en refait une.
                pipe.Dispose();
                continue;
            }

            await ServeAsync(pipe, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Instance de tuyau ouverte au seul compte Windows qui héberge le collecteur.</summary>
    private NamedPipeServerStream Listen()
    {
        var security = new PipeSecurity();
        var user = WindowsIdentity.GetCurrent().User;
        if (user is not null)
        {
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
            security.SetOwner(user);
        }

        return NamedPipeServerStreamAcl.Create(
            _endpoint.PipeName,
            PipeDirection.InOut,
            MaxClients,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            inBufferSize: 0,
            outBufferSize: 0,
            security);
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using (pipe)
        {
            if (!Allowed(pipe)) return;

            using var reader = new StreamReader(pipe, AgentWire.Utf8, detectEncodingFromByteOrderMarks: false, 1 << 14, leaveOpen: true);
            var writer = new StreamWriter(pipe, AgentWire.Utf8, 1 << 14, leaveOpen: true) { AutoFlush = false, NewLine = "\n" };

            while (!ct.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
                {
                    return;
                }
                if (line is null) return;
                if (line.Length == 0) continue;

                var (reply, stopping) = await ExecuteAsync(line, ct).ConfigureAwait(false);
                try
                {
                    await writer.WriteLineAsync(AgentWire.Encode(reply).AsMemory(), ct).ConfigureAwait(false);
                    await writer.FlushAsync(ct).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
                {
                    return;
                }

                if (stopping)
                {
                    // La réponse est partie : le terminal sait que l'arrêt est accepté.
                    _stop.TrySetResult(true);
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Le client doit être le même compte Windows. La liste d'accès du tuyau l'impose déjà ;
    /// cette vérification la redouble, et ne bloque pas quand Windows refuse de nommer le
    /// client — la liste d'accès reste alors la garantie.
    /// </summary>
    private bool Allowed(NamedPipeServerStream pipe)
    {
        if (_owner.Length == 0) return true;
        try
        {
            var client = pipe.GetImpersonationUserName();
            return string.IsNullOrEmpty(client) || string.Equals(client, _owner, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or NotSupportedException)
        {
            return true;
        }
    }

    private static string Owner()
    {
        try
        {
            return WindowsIdentity.GetCurrent().Name ?? string.Empty;
        }
        catch (Exception error) when (error is SystemException)
        {
            return string.Empty;
        }
    }

    // ---------- exécution d'un appel ----------

    private async Task<(AgentReply Reply, bool Stopping)> ExecuteAsync(string line, CancellationToken ct)
    {
        if (!AgentWire.TryReadCall(line, out var call, out var problem))
        {
            return (new AgentReply(0, false, problem, AgentWire.KindProtocol), false);
        }

        try
        {
            switch (call.Method)
            {
                case "agent.hello":
                    return (new AgentReply(call.Id, true, JsonSerializer.Serialize(_identity, Json.Wire), string.Empty), false);

                case "agent.stop":
                    return (new AgentReply(
                        call.Id,
                        true,
                        JsonSerializer.Serialize(new { ok = true, message = "Suivi en arrière-plan arrêté : plus aucune minute n’est collectée." }, Json.Wire),
                        string.Empty), true);

                default:
                    return (new AgentReply(call.Id, true, await DispatchAsync(call, ct).ConfigureAwait(false), string.Empty), false);
            }
        }
        catch (DomainException error)
        {
            return (new AgentReply(call.Id, false, error.Message, AgentWire.KindDomain), false);
        }
        catch (OperationCanceledException)
        {
            return (new AgentReply(call.Id, false, "Le collecteur s’arrête : l’appel n’a pas été traité.", AgentWire.KindProtocol), false);
        }
        catch (Exception error)
        {
            return (new AgentReply(call.Id, false, $"Panne du collecteur : {error.Message}", AgentWire.KindInternal), false);
        }
    }

    private async Task<string> DispatchAsync(AgentCall call, CancellationToken ct)
    {
        if (ReadOnly.Contains(call.Method))
        {
            return await _app.HandleAsync(call.Method, call.Parameters, ct).ConfigureAwait(false);
        }

        await _writes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await _app.HandleAsync(call.Method, call.Parameters, ct).ConfigureAwait(false);
        }
        finally
        {
            _writes.Release();
        }
    }

    // ---------- présence sur le disque ----------

    private void PublishInfo()
    {
        try
        {
            Directory.CreateDirectory(_endpoint.DataFolder);
            AppPaths.WriteAtomic(_endpoint.InfoPath, JsonSerializer.Serialize(_identity, Json.Pretty));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Sans fichier de présence, le tuyau reste la source de vérité : rien de bloquant.
        }
    }

    private void RemoveInfo()
    {
        try
        {
            if (File.Exists(_endpoint.InfoPath)) File.Delete(_endpoint.InfoPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Un fichier de présence oublié se reconnaît : son tuyau ne répond plus.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        _life.Cancel();
        foreach (var worker in _workers)
        {
            try
            {
                await worker.ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or IOException)
            {
                // Arrêt demandé : une instance de tuyau coupée en vol n'a rien à signaler.
            }
        }

        RemoveInfo();
        _stop.TrySetResult(false);
        _life.Dispose();
        _writes.Dispose();
    }
}
