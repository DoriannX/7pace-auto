#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace SeptPaceAuto.Services;

/// <summary>
/// Contrat JSON entre le terminal et le cœur métier. Le terminal interroge : rien ne lui
/// est poussé, il n'a pas d'affichage à rafraîchir en continu.
/// </summary>
public interface ITrackingApp : IAsyncDisposable
{
    /// <summary>Démarre la surveillance de fond (suivi Git, résolution des work items).</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>
    /// Exécute un appel de l'interface. Retourne la charge JSON du résultat.
    /// Une erreur métier est levée sous forme d'exception dont le message est déjà
    /// la phrase française à afficher.
    /// </summary>
    Task<string> HandleAsync(string method, string paramsJson, CancellationToken ct);
}

/// <summary>Point d'entrée unique du domaine.</summary>
public static class TrackingAppFactory
{
    /// <summary>Profil de données du terminal.</summary>
    public static string DataFolder => AppPaths.Root;

    /// <summary>Clé stable du profil, utilisée pour empêcher deux processus d'écrire simultanément.</summary>
    public static string DataKey { get; } = Key(DataFolder);

    /// <summary>Mutex du terminal pour ce profil.</summary>
    public static string InstanceMutexName => $@"Local\SeptPaceAuto.instance.{DataKey}";

    public static ITrackingApp Create() => new TrackingApp();

    private static string Key(string folder)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(folder.ToLowerInvariant()));
        return Convert.ToHexString(bytes, 0, 8);
    }
}
