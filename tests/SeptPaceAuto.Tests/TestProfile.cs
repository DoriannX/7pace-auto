using System.Runtime.CompilerServices;
using Xunit;

// Les tests partagent un profil de données sur le disque : ils s'exécutent en série pour
// que deux journées ne se marchent jamais dessus.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SeptPaceAuto.Tests;

/// <summary>
/// Profil de données isolé. SEPTPACE_DATA est posé avant que le cœur ne fige ses chemins :
/// aucun test ne touche les journées ni les réglages de l'installation réelle.
/// </summary>
internal static class TestProfile
{
    [ModuleInitializer]
    internal static void Prepare()
    {
        var folder = Path.Combine(Path.GetTempPath(), "7pace-auto-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        Environment.SetEnvironmentVariable("SEPTPACE_DATA", folder);
    }
}
