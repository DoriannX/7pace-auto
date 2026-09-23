#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SeptPaceAuto.Services;

/// <summary>
/// Surveillance discrète : la branche Git active, relevée dans les horaires de travail,
/// alimente le créneau en cours. Aucune surveillance des applications, frappes ou inactivité.
///
/// Le chrono rapide « À attribuer » vit ici aussi : il partage la même cadence, et son
/// créneau coexiste volontairement avec celui du suivi Git.
/// </summary>
internal sealed class GitTracker : IAsyncDisposable
{
    /// <summary>Écart minimal avant de conclure à une veille, même sur un relevé très rapproché.</summary>
    private static readonly TimeSpan MinimumGap = TimeSpan.FromMinutes(5);

    /// <summary>Durées entre lesquelles reste la tolérance à une lecture Git en échec.</summary>
    private static readonly TimeSpan MinimumHold = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaximumHold = TimeSpan.FromMinutes(10);

    private const int MaxGapDays = 8;
    private const string QuickLabel = "Hors ticket · à attribuer";
    private const string GapLabel = "Intervalle à préciser (poste en veille ou arrêté)";

    private readonly DayStore _days;
    private readonly IWorkItemResolver _resolver;
    private readonly Func<Profile> _profile;
    private readonly IBranchReader _branches;
    private readonly Func<DateTime> _clock;
    private readonly ITrackerState _state;
    private readonly SemaphoreSlim _turn = new(1, 1);

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

    /// <summary>Début de la série de lectures Git ratées en cours, <c>default</c> quand la lecture va bien.</summary>
    private DateTime _unreadableSince;

    // Fraîcheur du suivi, tenue à jour pour la consultation en lecture seule.
    private DateTime? _observedAt;
    private DateTime? _branchAt;
    private DateTime? _writtenAt;
    private int? _blockEnd;
    private string? _lastError;
    private DateTime? _lastErrorAt;

    // Chrono rapide en cours.
    private string? _quickDate;
    private int _quickStart;
    private int? _quickEntryId;

    private DateTime _lastTick;
    private Tracking _current = new(null, null, null, "Suivi en préparation", false, "outside-hours");

    /// <param name="profile">Réglages actifs, relus à chaque relevé : un changement s'applique sans redémarrage.</param>
    public GitTracker(DayStore days, IWorkItemResolver resolver, Func<Profile> profile)
        : this(days, resolver, profile, static () => DateTime.Now)
    {
    }

    /// <param name="clock">Horloge du domaine, partagée avec le reste de l'application.</param>
    public GitTracker(DayStore days, IWorkItemResolver resolver, Func<Profile> profile, Func<DateTime> clock)
        : this(days, resolver, profile, new GitBranchReader(), clock, new FileTrackerState())
    {
    }

    /// <param name="branches">Relevé de la branche ; isolé pour éprouver les pannes de lecture.</param>
    /// <param name="clock">Horloge du suivi ; isolée pour éprouver la cadence sans attendre.</param>
    /// <param name="state">Battement et chrono rapide persistés.</param>
    internal GitTracker(
        DayStore days,
        IWorkItemResolver resolver,
        Func<Profile> profile,
        IBranchReader branches,
        Func<DateTime> clock,
        ITrackerState state)
    {
        _days = days;
        _resolver = resolver;
        _profile = profile;
        _branches = branches;
        _clock = clock;
        _state = state;
    }

    /// <summary>Dernier état connu du suivi.</summary>
    public Tracking Current => _current;

    /// <summary>
    /// Fraîcheur du suivi. Lecture pure : aucun relevé n'est déclenché, rien n'est écrit.
    /// Les champs sont lus sans verrou, ce qui suffit à un écran rafraîchi en continu.
    /// </summary>
    public TrackingHealth Health
    {
        get
        {
            var running = _blockWindow is not null;
            return new TrackingHealth(
                Stamp(_observedAt),
                Stamp(_branchAt),
                Stamp(_writtenAt),
                running ? _blockDate : null,
                running ? _blockStart : null,
                running ? _blockEnd : null,
                _lastError,
                Stamp(_lastErrorAt),
                _branches.Available,
                (int)Period().TotalSeconds * 3);
        }
    }

    private static string? Stamp(DateTime? moment) =>
        moment is DateTime value ? value.ToString("o", CultureInfo.InvariantCulture) : null;

    /// <summary>Un chrono rapide ouvert interdit l'envoi : sa fin n'est pas encore connue.</summary>
    public bool QuickRunning => _quickDate is not null;

    public async Task StartAsync(CancellationToken ct)
    {
        _life = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await PrimeAsync(_life.Token).ConfigureAwait(false);
        _loop = Task.Run(() => LoopAsync(_life.Token), CancellationToken.None);
    }

    /// <summary>Relit l'état persisté puis effectue un premier relevé, sans lancer la boucle.</summary>
    internal async Task PrimeAsync(CancellationToken ct)
    {
        _lastTick = _state.ReadHeartbeat();
        LoadQuick();
        await TickAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Un relevé isolé, pour éprouver la politique sans dépendre d'une horloge réelle.</summary>
    internal Task ObserveOnceAsync(CancellationToken ct) => TickAsync(ct);

    /// <summary>
    /// Démarre ou arrête le chrono rapide. Le créneau produit est « à attribuer » : aucun
    /// numéro n'est demandé sur le moment, c'est la relecture du lendemain qui tranche.
    /// </summary>
    public async Task<Tracking> SetQuickAsync(bool running, CancellationToken ct)
    {
        await _turn.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _clock();
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
            var now = _clock();
            WriteBlock(TimeRules.MinuteOfDay(now));
            CloseBlock();
            _branch = null;
            _unreadableSince = default;
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

    /// <summary>
    /// Écart au-delà duquel le poste est tenu pour endormi ou arrêté. Le seuil suit la
    /// cadence réglée : à 300 s de relevé, un relevé normal arrive cinq minutes et quelques
    /// secondes après le précédent, et un seuil fixe de cinq minutes transformait alors
    /// chaque relevé d'une journée de travail en fausse absence.
    /// </summary>
    private TimeSpan GapAfter()
    {
        var threshold = Period() + Period() + TimeSpan.FromMinutes(1);
        return threshold < MinimumGap ? MinimumGap : threshold;
    }

    /// <summary>
    /// Durée pendant laquelle une lecture Git en échec conserve le bloc en cours. Elle reste
    /// inférieure au seuil de veille : une panne de lecture n'est jamais lue comme une absence.
    /// </summary>
    private TimeSpan HoldFor()
    {
        var hold = Period() + Period();
        if (hold < MinimumHold) hold = MinimumHold;
        return hold > MaximumHold ? MaximumHold : hold;
    }

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
            _observedAt = _clock();
            _lastError = null;
            _lastErrorAt = null;
        }
        catch (OperationCanceledException)
        {
            // Arrêt demandé.
        }
        catch (Exception error) when (error is DomainException or IOException or UnauthorizedAccessException or JsonException)
        {
            // Un relevé raté ne doit pas arrêter la surveillance : le suivant réessaie. La
            // trace reste lisible dans la consultation de la journée en cours.
            _lastError = error.Message;
            _lastErrorAt = _clock();
        }
        finally
        {
            _turn.Release();
        }
    }

    private async Task ObserveAsync(CancellationToken ct)
    {
        var now = _clock();
        var previous = _lastTick;
        _lastTick = now;
        SaveHeartbeat(now);

        // Le chrono rapide suit sa propre horloge : il court aussi hors horaires, parce que
        // c'est l'utilisateur qui l'a démarré.
        WriteQuick(now);

        // Veille, arrêt ou plantage : le bloc est coupé et l'intervalle manquant devient
        // un créneau « à attribuer », jamais une supposition.
        if (previous != default && now - previous > GapAfter())
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

        var read = await _branches.ReadAsync(_profile().Settings.RepoPath, ct).ConfigureAwait(false);
        if (read.Status == BranchStatus.Unreadable)
        {
            HoldOrFreeze(now, read);
            return;
        }

        _unreadableSince = default;
        if (read.Status != BranchStatus.Ok || read.Name is not string branch)
        {
            if (_blockWindow is { } orphan) WriteBlock(Math.Min(minute, orphan.End));
            CloseBlock();
            Publish("no-repo");
            return;
        }

        // Seule une lecture aboutie horodate le dépôt : au-delà, il ne répond plus.
        _branchAt = now;

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
            // Une attribution déjà obtenue pour cette branche est conservée : rouvrir un bloc
            // après une coupure ne doit pas refaire un créneau « à attribuer » sans raison.
            if (!string.Equals(_branch, branch, StringComparison.Ordinal))
            {
                _resolution = new Resolution(WorkItemResolver.ExtractBug(branch), null, null, false, null);
            }
            _branch = branch;
            _blockDate = date;
            _blockWindow = window;
            _blockStart = Math.Max(window.Value.Start, minute);
            _blockEntryId = null;
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
    /// Lecture Git ratée. Tant que la panne est brève, le dernier état sûr est conservé : le
    /// bloc reste ouvert sans être prolongé, donc aucune minute n'est inventée, et la reprise
    /// sur la même branche prolonge le même créneau au lieu d'en ouvrir un second. Passé le
    /// délai de grâce, le bloc est gelé sur sa dernière minute vérifiée : la période inconnue
    /// devient un trou signalé, jamais un « poste en veille » qui serait faux.
    /// </summary>
    private void HoldOrFreeze(DateTime now, BranchRead read)
    {
        if (_unreadableSince == default) _unreadableSince = now;

        var held = _blockWindow is not null && now - _unreadableSince <= HoldFor();
        if (!held) CloseBlock();

        Publish("git-unreadable", held
            ? $"Lecture du dépôt Git impossible ({read.Detail}) : branche {_branch} conservée"
            : $"Lecture du dépôt Git impossible ({read.Detail}) : collecte suspendue");
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
                    var now = _clock();
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

        if (written is null) return;

        _blockEntryId = written.Id;
        _blockEnd = end;
        _writtenAt = _clock();
    }

    private void CloseBlock()
    {
        _blockWindow = null;
        _blockEntryId = null;
        _blockEnd = null;
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
        var windows = _profile().Schedule.Windows;
        var cursor = from.Date;
        for (var guard = 0; cursor <= to.Date && guard < MaxGapDays; guard++, cursor = cursor.AddDays(1))
        {
            // Un week-end sans poste allumé n'est pas un trou à justifier.
            if (cursor.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;

            var date = TimeRules.DateKey(cursor);

            // Une journée close appartient à 7pace : plus rien n'y est ajouté.
            if (_days.IsClosed(date)) continue;

            var lower = cursor == from.Date ? TimeRules.MinuteOfDay(from) : 0;
            var upper = cursor == to.Date ? TimeRules.MinuteOfDay(to) : 24 * 60;
            var known = _days.Day(date);
            foreach (var window in windows)
            {
                var start = Math.Max(lower, window.Start);
                var end = Math.Min(upper, window.End);

                // Ce qui est déjà couvert — créneau manuel, envoyé, chrono rapide ou relevé
                // Git — n'est jamais redoublé : une reprise ne doit fabriquer ni doublon ni
                // chevauchement.
                foreach (var free in Uncovered(known, start, end))
                {
                    _days.WriteSpan(date, free.Start, free.End, GapLabel, null, null, "gap", null);
                }
            }
        }
    }

    /// <summary>Sous-périodes de [<paramref name="start"/>, <paramref name="end"/>) que ne couvre aucun créneau connu.</summary>
    private static List<(int Start, int End)> Uncovered(IReadOnlyList<Entry> known, int start, int end)
    {
        var free = new List<(int Start, int End)>();
        if (end - start < 1) return free;

        var covered = new List<(int Start, int End)>();
        foreach (var entry in known)
        {
            var from = Math.Max(entry.StartMinutes, start);
            var to = Math.Min(entry.EndMinutes, end);
            if (to > from) covered.Add((from, to));
        }
        covered.Sort(static (left, right) => left.Start.CompareTo(right.Start));

        var cursor = start;
        foreach (var span in covered)
        {
            if (span.Start > cursor) free.Add((cursor, span.Start));
            if (span.End > cursor) cursor = span.End;
            if (cursor >= end) break;
        }
        if (cursor < end) free.Add((cursor, end));
        return free;
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

    /// <param name="detail">Libellé imposé, quand l'état ne suffit pas à le décrire.</param>
    private void Publish(string state, string? detail = null)
    {
        var label = detail ?? state switch
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

    private void SaveHeartbeat(DateTime now) => _state.WriteHeartbeat(now);

    private void LoadQuick()
    {
        var stored = _state.ReadQuick();
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
        _state.WriteQuick(_quickDate is null
            ? null
            : new QuickTimerState { Date = _quickDate, StartMinute = _quickStart, EntryId = _quickEntryId });
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
            var now = _clock();
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
