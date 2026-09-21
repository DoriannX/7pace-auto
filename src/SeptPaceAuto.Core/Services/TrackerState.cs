#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SeptPaceAuto.Services;

internal sealed class Heartbeat
{
    [JsonPropertyName("at")] public string? At { get; set; }
}

/// <summary>Chrono rapide ouvert, relu au démarrage pour ne pas perdre la période en cours.</summary>
internal sealed class QuickTimerState
{
    [JsonPropertyName("date")] public string? Date { get; set; }
    [JsonPropertyName("startMinute")] public int StartMinute { get; set; }
    [JsonPropertyName("entryId")] public int? EntryId { get; set; }
}

/// <summary>
/// Mémoire du suivi entre deux exécutions : le dernier battement, qui sert à reconnaître une
/// veille ou un arrêt, et le chrono rapide encore ouvert.
/// </summary>
internal interface ITrackerState
{
    /// <summary>Dernier relevé connu, ou <c>default</c> quand aucun n'a été enregistré.</summary>
    DateTime ReadHeartbeat();

    void WriteHeartbeat(DateTime now);

    QuickTimerState? ReadQuick();

    /// <summary>Enregistre le chrono rapide, ou l'efface quand il est nul.</summary>
    void WriteQuick(QuickTimerState? quick);
}

/// <summary>
/// État persisté sous %LOCALAPPDATA%. Une écriture impossible n'arrête jamais la collecte :
/// le temps est déjà dans la journée, seule la mémoire du redémarrage en souffre.
/// </summary>
internal sealed class FileTrackerState : ITrackerState
{
    public DateTime ReadHeartbeat()
    {
        var stored = AppPaths.ReadJson<Heartbeat>(AppPaths.Heartbeat);
        return DateTime.TryParse(stored?.At, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var moment)
            ? moment.ToLocalTime()
            : default;
    }

    public void WriteHeartbeat(DateTime now)
    {
        try
        {
            AppPaths.EnsureRoot();
            var payload = new Heartbeat { At = now.ToString("o", CultureInfo.InvariantCulture) };
            AppPaths.WriteAtomic(AppPaths.Heartbeat, JsonSerializer.Serialize(payload, Json.Pretty));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Sans battement, un redémarrage ne détectera pas le trou : ce n'est pas bloquant.
        }
    }

    public QuickTimerState? ReadQuick() => AppPaths.ReadJson<QuickTimerState>(AppPaths.Quick);

    public void WriteQuick(QuickTimerState? quick)
    {
        try
        {
            AppPaths.EnsureRoot();
            if (quick is null)
            {
                if (File.Exists(AppPaths.Quick)) File.Delete(AppPaths.Quick);
                return;
            }
            AppPaths.WriteAtomic(AppPaths.Quick, JsonSerializer.Serialize(quick, Json.Pretty));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Le créneau est déjà écrit sur le disque : perdre l'état du chrono ne perd pas le temps.
        }
    }
}
