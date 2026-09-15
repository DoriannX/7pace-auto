#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace SeptPaceAuto.Services;

internal sealed class Heartbeat
{
    [JsonPropertyName("at")] public string? At { get; set; }
}

/// <summary>
/// Surveillance discrète : la branche Git active, relevée dans les créneaux de travail,
/// alimente le créneau en cours. Aucune surveillance des applications, frappes ou inactivité.
/// </summary>
internal sealed class GitTracker : IAsyncDisposable
{
    private static readonly TimeSpan GapThreshold = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(10);
    private const int MaxGapDays = 8;

    private readonly DayStore _days;
    private readonly WorkItemResolver _resolver;
    private readonly Func<Profile> _profile;
    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly string? _git = ProcessRunner.Git;

    private CancellationTokenSource? _life;
    private Task? _loop;
    private PeriodicTimer? _timer;

    // Bloc en cours d'accumulation.
    private string? _branch;
    private string _blockDate = string.Empty;
    private int _blockStart;
    private (int Start, int End)? _blockWindow;
    private int? _blockEntryId;
    private DateTime _blockStartedAt;
    private Resolution _resolution = new(null, null, null, false, null);
    private int _resolving;

    private DateTime _lastTick;
    private bool _paused;
    private int _frozenElapsed;
    private Tracking _current = new(false, null, null, null, "Suivi en préparation", 0, "outside-hours");

    /// <param name="profile">Réglages actifs, relus à chaque relevé : un changement s'applique sans redémarrage.</param>
    public GitTracker(DayStore days, WorkItemResolver resolver, Func<Profile> profile)
    {
        _days = days;
        _resolver = resolver;
        _profile = profile;
    }

    /// <summary>Dernier état connu ; l'interface le reçoit aussi par poussée.</summary>
    public Tracking Current => _current;

    public event Action<Tracking>? Changed;

    public async Task StartAsync(CancellationToken ct)
    {
        _life = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _lastTick = LoadHeartbeat();
        await TickAsync(_life.Token).ConfigureAwait(false);
        _loop = Task.Run(() => LoopAsync(_life.Token), CancellationToken.None);
    }

    public async Task<Tracking> SetPausedAsync(bool paused, CancellationToken ct)
    {
        await _turn.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _paused = paused;
            if (paused)
            {
                var now = DateTime.Now;
                WriteBlock(TimeRules.MinuteOfDay(now), now);
                CloseBlock(now);
                Publish("paused", now);
            }
        }
        finally
        {
            _turn.Release();
        }

        // La reprise repart tout de suite sur la branche courante plutôt qu'au prochain relevé.
        if (!paused) await TickAsync(ct).ConfigureAwait(false);
        return _current;
    }

    /// <summary>
    /// Les réglages ont changé : le bloc en cours appartenait à l'ancienne configuration,
    /// on le ferme, on réaligne la cadence et on relève tout de suite.
    /// </summary>
    public async Task<Tracking> ReconfigureAsync(CancellationToken ct)
    {
        await _turn.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = DateTime.Now;
            WriteBlock(TimeRules.MinuteOfDay(now), now);
            CloseBlock(now);
            _branch = null;
            _resolution = new Resolution(null, null, null, false, null);
        }
        finally
        {
            _turn.Release();
        }

        if (_timer is { } timer) timer.Period = Period();
        await TickAsync(ct).ConfigureAwait(false);
        return _current;
    }

    private TimeSpan Period() => TimeSpan.FromSeconds(Math.Clamp(_profile().Settings.PollSeconds, 10, 300));

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Period());
        _timer = timer;
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await TickAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Arrêt demandé.
        }
        finally
        {
            _timer = null;
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        try
        {
            await _turn.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            await ObserveAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Arrêt demandé.
        }
        catch (Exception error) when (error is DomainException or IOException or UnauthorizedAccessException or JsonException)
        {
            // Un relevé raté ne doit pas arrêter la surveillance : le suivant réessaie.
        }
        finally
        {
            _turn.Release();
        }
    }

    private async Task ObserveAsync(CancellationToken ct)
    {
        var now = DateTime.Now;
        var previous = _lastTick;
        _lastTick = now;
        SaveHeartbeat(now);

        // Veille, arrêt ou plantage : le bloc est coupé et l'intervalle manquant devient
        // un créneau « à préciser », jamais une supposition.
        if (previous != default && now - previous > GapThreshold)
        {
            CloseBlock(previous);
            FillGap(previous, now);
        }

        if (_paused)
        {
            CloseBlock(now);
            Publish("paused", now);
            return;
        }

        var minute = TimeRules.MinuteOfDay(now);
        var window = _profile().Schedule.WindowAt(minute);
        if (window is null)
        {
            if (_blockWindow is { } closing) WriteBlock(closing.End, now);
            CloseBlock(now);
            Publish("outside-hours", now);
            return;
        }

        var repository = _profile().Settings.RepoPath;
        var branch = repository.Length > 0 && Directory.Exists(repository)
            ? await ReadBranchAsync(repository, ct).ConfigureAwait(false)
            : null;
        if (branch is null)
        {
            if (_blockWindow is { } orphan) WriteBlock(Math.Min(minute, orphan.End), now);
            CloseBlock(now);
            Publish("no-repo", now);
            return;
        }

        var date = TimeRules.DateKey(now);
        var sameBlock = string.Equals(_branch, branch, StringComparison.Ordinal)
            && string.Equals(_blockDate, date, StringComparison.Ordinal)
            && _blockWindow is { } current && current.End == window.Value.End;

        if (!sameBlock)
        {
            if (_blockWindow is { } previousWindow)
            {
                var closeAt = string.Equals(_blockDate, date, StringComparison.Ordinal) && previousWindow.End == window.Value.End
                    ? minute
                    : previousWindow.End;
                WriteBlock(closeAt, now);
            }
            CloseBlock(now);
            _branch = branch;
            _blockDate = date;
            _blockWindow = window;
            _blockStart = Math.Max(window.Value.Start, minute);
            _blockStartedAt = now;
            _blockEntryId = null;
            _frozenElapsed = 0;
            _resolution = new Resolution(WorkItemResolver.ExtractBug(branch), null, null, false, null);
        }

        // Le relevé ne doit jamais attendre az : le cache répond tout de suite, et une
        // branche encore inconnue est résolue en fond, sans bloquer la pause ni le chrono.
        if (!_resolution.Resolved)
        {
            if (_resolver.TryCached(branch, out var known)) _resolution = known;
            else BeginResolve(branch);
        }

        WriteBlock(minute, now);
        Publish("running", now);
    }

    /// <summary>
    /// Résolution en arrière-plan : az peut mettre vingt secondes, le relevé continue
    /// pendant ce temps et le créneau est corrigé dès que la réponse arrive.
    /// </summary>
    private void BeginResolve(string branch)
    {
        if (Interlocked.Exchange(ref _resolving, 1) == 1) return;
        var token = _life?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                var resolution = await _resolver.ResolveAsync(branch, token).ConfigureAwait(false);
                if (!resolution.Resolved) return;

                await _turn.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    // La branche a pu changer entre-temps : on n'applique qu'au bloc concerné.
                    if (!string.Equals(_branch, branch, StringComparison.Ordinal)) return;
                    _resolution = resolution;
                    var now = DateTime.Now;
                    WriteBlock(TimeRules.MinuteOfDay(now), now);
                    Publish(_paused ? "paused" : "running", now);
                }
                finally
                {
                    _turn.Release();
                }
            }
            catch (Exception error) when (error is OperationCanceledException or DomainException or IOException or UnauthorizedAccessException or JsonException)
            {
                // L'attribution restera à faire à la main : rien n'est inventé.
            }
            finally
            {
                Interlocked.Exchange(ref _resolving, 0);
            }
        }, CancellationToken.None);
    }

    private void WriteBlock(int endMinute, DateTime now)
    {
        if (_blockWindow is null || _branch is null) return;
        var end = Math.Min(endMinute, _blockWindow.Value.End);
        if (end <= _blockStart) return;

        var resolved = _resolution.Resolved && _resolution.WorkItem is int;
        var written = _days.WriteTracked(
            _blockDate,
            _blockStart,
            end,
            resolved ? "ticket" : "unknown",
            EntryTitle(),
            resolved ? _resolution.WorkItem : null,
            _resolution.Bug,
            "git",
            _blockEntryId);

        if (written is null)
        {
            // Ce temps appartient déjà à un créneau saisi à la main : on repart après lui.
            _blockStart = Math.Max(_blockStart, Math.Min(TimeRules.MinuteOfDay(now), _blockWindow.Value.End));
            _blockStartedAt = now;
            _blockEntryId = null;
            return;
        }
        _blockEntryId = written.Id;
    }

    private void CloseBlock(DateTime now)
    {
        if (_blockWindow is not null && _blockStartedAt != default)
        {
            _frozenElapsed = Math.Max(0, (int)(now - _blockStartedAt).TotalSeconds);
        }
        _blockWindow = null;
        _blockEntryId = null;
        _blockStartedAt = default;
    }

    private void FillGap(DateTime from, DateTime to)
    {
        var cursor = from.Date;
        for (var guard = 0; cursor <= to.Date && guard < MaxGapDays; guard++, cursor = cursor.AddDays(1))
        {
            // Un week-end sans poste allumé n'est pas un trou à justifier.
            if (cursor.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;

            var lower = cursor == from.Date ? TimeRules.MinuteOfDay(from) : 0;
            var upper = cursor == to.Date ? TimeRules.MinuteOfDay(to) : 24 * 60;
            foreach (var window in _profile().Schedule.Windows)
            {
                var start = Math.Max(lower, window.Start);
                var end = Math.Min(upper, window.End);
                if (end - start < 1) continue;
                _days.WriteTracked(
                    TimeRules.DateKey(cursor),
                    start,
                    end,
                    "unknown",
                    "Intervalle à préciser (poste en veille ou arrêté)",
                    null,
                    null,
                    "gap",
                    null);
            }
        }
    }

    private async Task<string?> ReadBranchAsync(string repository, CancellationToken ct)
    {
        if (_git is null) return null;

        var head = await ProcessRunner.RunAsync(
            _git,
            new[] { "-C", repository, "rev-parse", "--abbrev-ref", "HEAD" },
            GitTimeout,
            ct).ConfigureAwait(false);
        var name = head.Ok ? head.StdOut.Trim() : null;
        if (!string.IsNullOrEmpty(name) && !string.Equals(name, "HEAD", StringComparison.Ordinal)) return name;

        var symbolic = await ProcessRunner.RunAsync(
            _git,
            new[] { "-C", repository, "symbolic-ref", "--quiet", "--short", "HEAD" },
            GitTimeout,
            ct).ConfigureAwait(false);
        var alternate = symbolic.Ok ? symbolic.StdOut.Trim() : null;
        if (!string.IsNullOrEmpty(alternate)) return alternate;

        // HEAD détachée : le temps est bien réel, mais l'attribution reste à faire.
        return string.IsNullOrEmpty(name) ? null : "HEAD détachée";
    }

    private string EntryTitle()
    {
        if (_resolution.Resolved && _resolution.WorkItem is int item)
        {
            return string.IsNullOrWhiteSpace(_resolution.Title) ? $"Fix #{item}" : _resolution.Title!;
        }
        if (_resolution.Bug is int bug) return $"Bug #{bug} · attribution à compléter";
        return $"Branche {_branch} · attribution à compléter";
    }

    private void Publish(string state, DateTime now)
    {
        var running = string.Equals(state, "running", StringComparison.Ordinal) && _blockWindow is not null;
        var elapsed = running && _blockStartedAt != default
            ? Math.Max(0, (int)(now - _blockStartedAt).TotalSeconds)
            : _frozenElapsed;

        var title = state switch
        {
            "running" => EntryTitle(),
            "paused" => _branch is null ? "Suivi en pause" : EntryTitle(),
            "outside-hours" => "Hors créneaux de travail",
            _ => _profile().Settings.RepoPath.Length == 0
                ? "Aucun dépôt Git réglé : ouvre les réglages"
                : "Dépôt Git introuvable",
        };

        var next = new Tracking(
            _paused,
            _branch,
            _resolution.Bug,
            _resolution.Resolved ? _resolution.WorkItem : null,
            title,
            elapsed,
            state);

        _current = next;
        Changed?.Invoke(next);
    }

    private static DateTime LoadHeartbeat()
    {
        var stored = AppPaths.ReadJson<Heartbeat>(AppPaths.Heartbeat);
        return DateTime.TryParse(stored?.At, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var moment)
            ? moment.ToLocalTime()
            : default;
    }

    private static void SaveHeartbeat(DateTime now)
    {
        try
        {
            AppPaths.EnsureRoot();
            AppPaths.WriteAtomic(AppPaths.Heartbeat, JsonSerializer.Serialize(new Heartbeat { At = now.ToString("o", CultureInfo.InvariantCulture) }, Json.Pretty));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Sans battement, un redémarrage ne détectera pas le trou : ce n'est pas bloquant.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _life?.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Attendu.
            }
        }

        // Dernière écriture : la fermeture ne perd pas la minute en cours.
        try
        {
            var now = DateTime.Now;
            WriteBlock(TimeRules.MinuteOfDay(now), now);
            SaveHeartbeat(now);
        }
        catch (Exception error) when (error is DomainException or IOException or UnauthorizedAccessException)
        {
            // Rien de plus à tenter à la fermeture.
        }

        _life?.Dispose();
        _turn.Dispose();
    }
}
