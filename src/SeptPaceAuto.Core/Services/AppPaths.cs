#nullable enable
using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace SeptPaceAuto.Services;

/// <summary>Emplacements locaux ; rien n'est écrit ailleurs que sous %LOCALAPPDATA%.</summary>
internal static class AppPaths
{
    public static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>
    /// Racine des données. SEPTPACE_DATA la déplace : une instance de test tourne alors sur
    /// son propre profil, sans toucher aux journées ni aux réglages de l'installation.
    /// </summary>
    public static string Root { get; } = Override() ?? Path.Combine(LocalAppData, "7pace-auto");

    private static string? Override()
    {
        var folder = Environment.GetEnvironmentVariable("SEPTPACE_DATA");
        return string.IsNullOrWhiteSpace(folder) ? null : Path.GetFullPath(folder);
    }

    public static string Days => Path.Combine(Root, "days");
    public static string Settings => Path.Combine(Root, "settings.json");
    public static string WorkItems => Path.Combine(Root, "workitems.json");
    public static string Heartbeat => Path.Combine(Root, "heartbeat.json");

    /// <summary>Dates closes : journées envoyées ou ignorées, dont le détail n'existe plus.</summary>
    public static string ClosedDays => Path.Combine(Root, "closed.json");

    /// <summary>Chrono rapide en cours, pour qu'un redémarrage ne perde pas la période ouverte.</summary>
    public static string Quick => Path.Combine(Root, "quick.json");

    /// <summary>Dernière journée pour laquelle la notification du matin est déjà partie.</summary>
    public static string Announced => Path.Combine(Root, "notified.json");

    /// <summary>Jeton 7pace protégé par DPAPI, écrit depuis les réglages de l'application.</summary>
    public static string SevenPaceToken => Path.Combine(Root, "token.bin");

    /// <summary>
    /// Ancien emplacement du jeton, alimenté par un script PowerShell externe. Il reste lu
    /// tant qu'il existe, pour ne pas perdre l'authentification d'une installation antérieure.
    /// </summary>
    public static string LegacySevenPaceToken => Path.Combine(LocalAppData, "vault-7pace.jeton");

    public static void EnsureRoot() => Directory.CreateDirectory(Root);

    /// <summary>Écriture atomique : fichier temporaire dans le même dossier puis remplacement.</summary>
    public static void WriteAtomic(string path, string content)
    {
        var temp = Stage(path);
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Même garantie que <see cref="WriteAtomic(string, string)"/> pour des octets bruts.</summary>
    public static void WriteAtomic(string path, byte[] content)
    {
        var temp = Stage(path);
        File.WriteAllBytes(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    private static string Stage(string path)
    {
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        return path + ".tmp";
    }

    public static T? ReadJson<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path, Encoding.UTF8);
            return string.IsNullOrWhiteSpace(text) ? null : JsonSerializer.Deserialize<T>(text, Json.Wire);
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
