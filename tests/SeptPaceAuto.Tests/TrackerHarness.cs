#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SeptPaceAuto.Services;

namespace SeptPaceAuto.Tests;

/// <summary>Battement et chrono rapide gardés en mémoire : chaque test part d'un état connu.</summary>
internal sealed class MemoryTrackerState : ITrackerState
{
    public DateTime Heartbeat { get; set; }

    public QuickTimerState? Quick { get; set; }

    public DateTime ReadHeartbeat() => Heartbeat;

    public void WriteHeartbeat(DateTime now) => Heartbeat = now;

    public QuickTimerState? ReadQuick() => Quick;

    public void WriteQuick(QuickTimerState? quick) => Quick = quick;
}

/// <summary>Relevé de branche dicté par le test : c'est lui qui décide des pannes.</summary>
internal sealed class ScriptedBranchReader : IBranchReader
{
    public BranchRead Answer { get; set; } = BranchRead.Found("feature/34131-collecte");

    public bool Available { get; set; } = true;

    public int Reads { get; private set; }

    public Task<BranchRead> ReadAsync(string repository, CancellationToken ct)
    {
        Reads++;
        return Task.FromResult(Answer);
    }
}

/// <summary>
/// Résolveur muet : il répond toujours depuis « son cache », donc aucune tâche de fond
/// n'est lancée et le relevé reste déterministe.
/// </summary>
internal sealed class SilentResolver : IWorkItemResolver
{
    public bool TryCached(string? branch, out Resolution resolution)
    {
        resolution = new Resolution(WorkItemResolver.ExtractBug(branch), null, null, false, "Résolution neutralisée pour le test.");
        return true;
    }

    public Task<Resolution> ResolveAsync(string? branch, CancellationToken ct) =>
        Task.FromResult(new Resolution(WorkItemResolver.ExtractBug(branch), null, null, false, "Résolution neutralisée pour le test."));
}

/// <summary>
/// Suivi Git monté sur une horloge, un relevé de branche et un stockage pilotés par le test.
/// Les relevés sont déclenchés un par un : aucune attente réelle, aucun accès à git.
/// </summary>
internal sealed class TrackerHarness : IDisposable
{
    private readonly string _folder;

    public TrackerHarness(int pollSeconds = 30, DateTime? day = null)
    {
        Day = day ?? new DateTime(2026, 3, 17); // un mardi : le comblement ignore les week-ends.
        _folder = Path.Combine(Path.GetTempPath(), "7pace-auto-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);

        Active = SeptPaceAuto.Services.Profile.Create(
            new AppSettings { RepoPath = @"C:\depot", PollSeconds = pollSeconds },
            strict: false);
        Days = new DayStore(() => Active, _folder);
        Tracker = new GitTracker(Days, new SilentResolver(), () => Active, Branches, () => Now, State);
    }

    /// <summary>Journée observée ; les horaires par défaut sont 8 h 30 – 12 h 30 et 13 h 30 – 17 h.</summary>
    public DateTime Day { get; }

    public string Date => TimeRules.DateKey(Day);

    public DateTime Now { get; private set; }

    public Profile Active { get; }

    public DayStore Days { get; }

    public ScriptedBranchReader Branches { get; } = new();

    public MemoryTrackerState State { get; } = new();

    public GitTracker Tracker { get; }

    /// <summary>Premier relevé, battement persisté relu : c'est le démarrage de l'application.</summary>
    public Task PrimeAt(int hour, int minute, int second = 0)
    {
        Now = Moment(hour, minute, second);
        return Tracker.PrimeAsync(CancellationToken.None);
    }

    public Task TickAt(int hour, int minute, int second = 0)
    {
        Now = Moment(hour, minute, second);
        return Tracker.ObserveOnceAsync(CancellationToken.None);
    }

    public DateTime Moment(int hour, int minute, int second = 0) =>
        Day.AddHours(hour).AddMinutes(minute).AddSeconds(second);

    public List<Entry> Entries() => Days.Day(Date);

    public List<Entry> Gaps() => Entries().FindAll(entry => entry.Source == "gap");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Un dossier temporaire qui survit ne fait échouer aucun test.
        }
    }
}
