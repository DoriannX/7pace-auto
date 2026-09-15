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

/// <summary>Chrono rapide ouvert, relu au démarrage pour ne pas perdre la période en cours.</summary>
internal sealed class QuickTimerState
{
    [JsonPropertyName("date")] public string? Date { get; set; }
    [JsonPropertyName("startMinute")] public int StartMinute { get; set; }
    [JsonPropertyName("entryId")] public int? EntryId { get; set; }
}

/// <summary>
/// Surveillance discrète : la branche Git active, relevée dans les horaires de travail,
/// alimente le créneau en cours. Aucune surveillance des applications, frappes ou inactivité.
///
/// Le chrono rapide « À attribuer » vit ici aussi : il partage la même cadence, et son
/// créneau coexiste volontairement avec celui du suivi Git.
/// </summary>
internal sealed class GitTracker : IAsyncDisposable
{
    private static readonly TimeSpan GapThreshold = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(10);
    private const int MaxGapDays = 8;
    private const string QuickLabel = "Chrono rapide · à attribuer";

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
    private Resolution _resolution = new(null, null, null, false, null);
    private int _resolving;

    // Chrono rapide en cours.
    private string? _quickDate;
    private int _quickStart;
    private int? _quickEntryId;

    private DateTime _lastTick;
    private Tracking _current = new(null, null, null, "Suivi en préparation", false, "outside-hours");

    /// <param name="profile">Réglages actifs, relus à chaque relevé : un changement s'applique sans redémarrage.</param>
    public GitTracker(DayStore days, WorkItemResolver resolver, Func<Profile> profile)
    {
        _days = days;
        _resolver = resolver;
        _profile = profile;
    }

    /// <summary>Dernier état connu du suivi.</summary>
    public Tracking Current => _current;

    /// <summary>Un chrono rapide ouvert interdit l'envoi : sa fin n'est pas encore connue.</summary>
    public bool QuickRunning => _quickDate is not null;

    public async Task StartAsync(CancellationToken ct)
    {
        _life = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _lastTick = LoadHeartbeat();
        LoadQuick();
        await TickAsync(_life.Token).ConfigureAwait(false);
        _loop = Task.Run(() => LoopAsync(_life.Token), CancellationToken.None);
    }

    /// <summary>
    /// Démarre ou arrête le chrono rapide. Le créneau produit est « à attribuer » : aucun
    /// numéro n'est demandé sur le moment, c'est la relecture du lendemain qui tranche.
    /// </summary>
    public async Task<Tracking> SetQuickAsync(bool running, CancellationToken ct)
    {
        await _turn.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = DateTime.Now;
            if (running)
            {
                if (_quickDate is null)
                {
                    _quickDate = TimeRules.DateKey(now);
                    _quickStart = TimeRules.MinuteOfDay(now);
                    _quickEntryId = null;
                    SaveQuick();
                }
                WriteQuick(now);
            }
            else if (_quickDate is not null)
            {
                WriteQuick(now);
                _quickDate = null;
                _quickEntryId = null;
                SaveQuick();
            }
            Publish(_current.State);
        }
        finally
        {
            _turn.Release();
        }
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
            WriteBlock(TimeRules.MinuteOfDay(now));
            CloseBlock();
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

        // Le chrono rapide suit sa propre horloge : il court aussi hors horaires, parce que
        // c'est l'utilisateur qui l'a démarré.
        WriteQuick(now);

        // Veille, arrêt ou plantage : le bloc est coupé et l'intervalle manquant devient
        // un créneau « à attribuer », jamais une supposition.
        if (previous != default && now - previous > GapThreshold)
        {
            CloseBlock();
            FillGap(previous, now);
        }

        var minute = TimeRules.MinuteOfDay(now);
        var window = _profile().Schedule.WindowAt(minute);
        if (window is null)
        {
            if (_blockWindow is { } closing) WriteBlock(closing.End);
            CloseBlock();
            Publish("outside-hours");
            return;
        }

        var repository = _profile().Settings.RepoPath;
        var branch = repository.Length > 0 && Directory.Exists(repository)
            ? await ReadBranchAsync(repository, ct).ConfigureAwait(false)
            : null;
        if (branch is null)
        {
            if (_blockWindow is { } orphan) WriteBlock(Math.Min(minute, orphan.End));
            CloseBlock();
            Publish("no-repo");
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
                WriteBlock(closeAt);
            }
            CloseBlock();
            _branch = branch;
            _blockDate = date;
            _blockWindow = window;
            _blockStart = Math.Max(window.Value.Start, minute);
            _blockEntryId = null;
            _resolution = new Resolution(WorkItemResolver.ExtractBug(branch), null, null, false, null);
        }

        // Le relevé ne doit jamais attendre az : le cache répond tout de suite, et une
        // branche encore inconnue est résolue en fond, sans bloquer le relevé.
        if (!_resolution.Resolved)
        {
            if (_resolver.TryCached(branch, out var known)) _resolution = known;
            else BeginResolve(branch);
        }

        WriteBlock(minute);
        Publish("running");
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
                    WriteBlock(TimeRules.MinuteOfDay(now));
                    Publish(_current.State);
                }
                finally
                {
                    _turn.Release();
                }
            }
            catch (Exception error) when (error is OperationCanceledException or DomainException or IOException or UnauthorizedAccessException or JsonException)
            {
                // L'attribution restera à faire : rien n'est inventé.
            }
            finally
            {
                Interlocked.Exchange(ref _resolving, 0);
            }
        }, CancellationToken.None);
    }

    private void WriteBlock(int endMinute)
    {
        if (_blockWindow is null || _branch is null) return;
        var end = Math.Min(endMinute, _blockWindow.Value.End);
        if (end <= _blockStart) return;

        var resolved = _resolution.Resolved && _resolution.WorkItem is int;
        var written = _days.WriteSpan(
            _blockDate,
            _blockStart,
            end,
            EntryLabel(),
            resolved ? _resolution.WorkItem : null,
            _resolution.Bug,
            "git",
            _blockEntryId);

        if (written is not null) _blockEntryId = written.Id;
    }

    private void CloseBlock()
    {
        _blockWindow = null;
        _blockEntryId = null;
    }

    /// <summary>
    /// Prolonge le créneau du chrono rapide. Passé minuit, il est fermé sur sa propre
    /// journée : une période à cheval n'existe pas dans le modèle.
    /// </summary>
    private void WriteQuick(DateTime now)
    {
        if (_quickDate is null) return;

        var today = TimeRules.DateKey(now);
        var sameDay = string.Equals(_quickDate, today, StringComparison.Ordinal);
        var end = sameDay ? TimeRules.MinuteOfDay(now) : 24 * 60 - 1;

        var written = _days.WriteSpan(_quickDate, _quickStart, end, QuickLabel, null, null, "quick", _quickEntryId);
        if (written is not null) _quickEntryId = written.Id;

        if (sameDay)
        {
            if (written is not null) SaveQuick();
            return;
        }

        _quickDate = null;
        _quickEntryId = null;
        SaveQuick();
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
                _days.WriteSpan(
                    TimeRules.DateKey(cursor),
                    start,
                    end,
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

    private string EntryLabel()
    {
        if (_resolution.Resolved && _resolution.WorkItem is int item)
        {
            return string.IsNullOrWhiteSpace(_resolution.Title) ? $"Fix #{item}" : _resolution.Title!;
        }
        if (_resolution.Bug is int bug) return $"Bug #{bug} · attribution à compléter";
        return $"Branche {_branch} · attribution à compléter";
    }

    private void Publish(string state)
    {
        var label = state switch
        {
            "running" => EntryLabel(),
            "outside-hours" => "Hors horaires de travail",
            _ => _profile().Settings.RepoPath.Length == 0
                ? "Aucun dépôt Git réglé : ouvre les réglages"
                : "Dépôt Git introuvable",
        };

        var next = new Tracking(
            _branch,
            _resolution.Bug,
            _resolution.Resolved ? _resolution.WorkItem : null,
            label,
            _quickDate is not null,
            state);

        _current = next;
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

    private void LoadQuick()
    {
        var stored = AppPaths.ReadJson<QuickTimerState>(AppPaths.Quick);
        if (stored?.Date is null) return;
        try
        {
            TimeRules.ParseDate(stored.Date);
        }
        catch (DomainException)
        {
            return;
        }
        _quickDate = stored.Date;
        _quickStart = Math.Clamp(stored.StartMinute, 0, 24 * 60 - 1);
        _quickEntryId = stored.EntryId;
    }

    private void SaveQuick()
    {
        try
        {
            AppPaths.EnsureRoot();
            if (_quickDate is null)
            {
                if (File.Exists(AppPaths.Quick)) File.Delete(AppPaths.Quick);
                return;
            }
            var payload = new QuickTimerState { Date = _quickDate, StartMinute = _quickStart, EntryId = _quickEntryId };
            AppPaths.WriteAtomic(AppPaths.Quick, JsonSerializer.Serialize(payload, Json.Pretty));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Le créneau est déjà écrit sur le disque : perdre l'état du chrono ne perd pas le temps.
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
            WriteBlock(TimeRules.MinuteOfDay(now));
            WriteQuick(now);
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
