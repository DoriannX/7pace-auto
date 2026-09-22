#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SeptPaceAuto.Services;

/// <summary>
/// Carte de visite du collecteur en arrière-plan. Elle voyage sur le tuyau, à l'ouverture
/// d'une connexion, et se dépose aussi dans le profil de données pour que les scripts
/// d'installation sachent quoi arrêter.
/// </summary>
public sealed class AgentInfo
{
    [JsonPropertyName("protocol")] public int Protocol { get; set; }
    [JsonPropertyName("pid")] public int Pid { get; set; }
    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
    [JsonPropertyName("startedAt")] public string? StartedAt { get; set; }
    [JsonPropertyName("executable")] public string? Executable { get; set; }
    [JsonPropertyName("dataFolder")] public string? DataFolder { get; set; }
    [JsonPropertyName("pipe")] public string? Pipe { get; set; }

    /// <summary>Heure de démarrage relue, ou null quand elle est absente ou illisible.</summary>
    [JsonIgnore]
    public DateTimeOffset? Started =>
        DateTimeOffset.TryParse(StartedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var moment)
            ? moment
            : null;

    /// <summary>Description du processus courant, publiée par le collecteur qui l'héberge.</summary>
    public static AgentInfo Describe(AgentEndpoint endpoint) => new()
    {
        Protocol = AgentEndpoint.Protocol,
        Pid = Environment.ProcessId,
        Version = AppVersion.Current,
        StartedAt = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
        Executable = Environment.ProcessPath,
        DataFolder = endpoint.DataFolder,
        Pipe = endpoint.PipeName,
    };
}

/// <summary>
/// Point de rendez-vous entre le collecteur et les terminaux d'un même profil de données.
///
/// Tout est dérivé du dossier de données : deux profils distincts ont un tuyau distinct,
/// un verrou distinct et un fichier de présence distinct, donc deux collecteurs peuvent
/// tourner côte à côte sans jamais écrire dans la même journée.
/// </summary>
public sealed class AgentEndpoint
{
    /// <summary>Version du protocole. Un écart franc vaut mieux qu'un malentendu silencieux.</summary>
    public const int Protocol = 1;

    public const string AgentExecutable = "SeptPaceAuto.Agent.exe";
    public const string TerminalExecutable = "SeptPaceAuto.Terminal.exe";

    private AgentEndpoint(string dataFolder)
    {
        DataFolder = dataFolder;
        Key = KeyOf(dataFolder);
    }

    /// <summary>Rendez-vous du profil actif, celui que désigne SEPTPACE_DATA le cas échéant.</summary>
    public static AgentEndpoint Default { get; } = new(AppPaths.Root);

    /// <summary>Rendez-vous d'un profil désigné : utile aux tests et aux outils.</summary>
    public static AgentEndpoint For(string dataFolder) => new(Path.GetFullPath(dataFolder));

    public string DataFolder { get; }

    /// <summary>Empreinte stable du profil, reprise par le tuyau et les verrous.</summary>
    public string Key { get; }

    public string PipeName => string.Concat("SeptPaceAuto.agent.", Key);

    /// <summary>Verrou du collecteur : un seul par profil de données.</summary>
    public string MutexName => string.Concat(@"Local\SeptPaceAuto.agent.", Key);

    /// <summary>
    /// Verrou de l'ancien terminal tout-en-un (1.0.9 et avant). Le collecteur le prend aussi :
    /// une version antérieure installée à côté ne peut alors plus écrire en parallèle.
    /// </summary>
    public string LegacyMutexName => string.Concat(@"Local\SeptPaceAuto.instance.", Key);

    /// <summary>Présence du collecteur, déposée au démarrage et retirée à l'arrêt propre.</summary>
    public string InfoPath => Path.Combine(DataFolder, "agent.json");

    /// <summary>Lit la présence déposée sur le disque, ou null quand rien n'est déclaré.</summary>
    public AgentInfo? ReadInfo()
    {
        try
        {
            if (!File.Exists(InfoPath)) return null;
            var text = File.ReadAllText(InfoPath, Encoding.UTF8);
            return string.IsNullOrWhiteSpace(text) ? null : JsonSerializer.Deserialize<AgentInfo>(text, Json.Wire);
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Empreinte d'un dossier de données, insensible à la casse comme Windows.</summary>
    public static string KeyOf(string dataFolder)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes((dataFolder ?? string.Empty).ToLowerInvariant()));
        return Convert.ToHexString(bytes, 0, 8);
    }
}
