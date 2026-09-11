#nullable enable
using System;
using System.Globalization;
using System.Reflection;

namespace SeptPaceAuto.Services;

/// <summary>
/// Version de l'exécutable en cours, unique source de vérité pour la comparaison avec la
/// dernière publication. Elle vient de &lt;Version&gt; dans le csproj, lue à l'exécution.
/// </summary>
internal static class AppVersion
{
    public static string Current { get; } = Read();

    private static string Read()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly;

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // Le SDK ajoute « +<hash de commit> » quand le dépôt est connu : il ne fait pas partie de la version.
            var plus = informational.IndexOf('+');
            var text = (plus >= 0 ? informational[..plus] : informational).Trim();
            if (text.Length > 0) return text;
        }

        var version = assembly.GetName().Version;
        return version is null
            ? "0.0.0"
            : string.Join('.',
                version.Major.ToString(CultureInfo.InvariantCulture),
                version.Minor.ToString(CultureInfo.InvariantCulture),
                Math.Max(0, version.Build).ToString(CultureInfo.InvariantCulture));
    }
}
