#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace SeptPaceAuto.Services;

/// <summary>
/// Seam unique entre l'hôte WinForms/WebView2 et le domaine : l'interface ne connaît
/// que du JSON, le domaine ne connaît aucune fenêtre.
/// </summary>
public interface ITrackingApp : IAsyncDisposable
{
    /// <summary>Poussées non sollicitées vers l'interface : (nom d'évènement, charge JSON).</summary>
    event Action<string, string>? Pushed;

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
    public static ITrackingApp Create() => new TrackingApp();
}
