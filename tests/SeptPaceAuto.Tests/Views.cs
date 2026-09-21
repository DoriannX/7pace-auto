using SeptPaceAuto.Services;
using SeptPaceAuto.Terminal;

namespace SeptPaceAuto.Tests;

/// <summary>Vues de consultation montées à la main : l'image rendue doit être prévisible.</summary>
internal static class Views
{
    public const string Date = "2026-03-17";

    public static readonly DateTimeOffset Now = new(2026, 3, 17, 10, 30, 0, TimeSpan.FromHours(1));

    public static readonly TimeSpan Refresh = TimeSpan.FromSeconds(2);

    public static Entry Span(int id, string start, string end, int? workItem = null, string label = "", string source = "git") =>
        new() { Id = id, Start = start, End = end, WorkItem = workItem, Label = label, Source = source };

    public static TrackingView Tracking(
        string state = "running",
        string label = "Réparer l’import",
        string? branch = "feature/1234-import",
        int? bug = 1234,
        int? workItem = 5678,
        bool quick = false,
        DateTimeOffset? observed = null,
        DateTimeOffset? branchAt = null,
        DateTimeOffset? written = null,
        string? spanDate = null,
        int? spanStart = null,
        int? spanEnd = null,
        string? error = null,
        DateTimeOffset? errorAt = null,
        bool git = true,
        int staleAfter = 90) =>
        new(
            state,
            label,
            branch,
            bug,
            workItem,
            quick,
            observed ?? Now,
            branchAt ?? Now,
            written ?? Now,
            spanDate,
            spanStart,
            spanEnd,
            error,
            errorAt,
            git,
            staleAfter);

    public static CurrentDayView Day(
        string date = Date,
        IEnumerable<Entry>? entries = null,
        int[][]? holes = null,
        int[][]? overlaps = null,
        int total = 0,
        int planned = 450,
        int unassigned = 0,
        int pending = 0,
        TrackingView? tracking = null) =>
        new(
            date,
            (entries ?? Array.Empty<Entry>()).ToList(),
            holes ?? Array.Empty<int[]>(),
            overlaps ?? Array.Empty<int[]>(),
            total,
            planned,
            unassigned,
            pending,
            tracking ?? Tracking());

    public static string Render(CurrentDayView view, DateTimeOffset? now = null, bool live = true) =>
        CurrentDayScreen.Render(view, now ?? Now, live, Refresh);
}

/// <summary>Surface pilotée par le test : les images sont gardées, les touches scénarisées.</summary>
internal sealed class FakeSurface : IScreenSurface
{
    private readonly Queue<bool> _keys;

    public FakeSurface(bool interactive = true, params bool[] keys)
    {
        Interactive = interactive;
        _keys = new Queue<bool>(keys);
    }

    public bool Interactive { get; }

    public List<string> Frames { get; } = new();

    public int Waits { get; private set; }

    public void Draw(string frame) => Frames.Add(frame);

    public Task<bool> WaitForKeyAsync(TimeSpan delay, CancellationToken ct)
    {
        Waits++;
        // Sans consigne restante, la touche ferme l'écran : un test ne doit jamais boucler.
        return Task.FromResult(_keys.Count == 0 || _keys.Dequeue());
    }
}

/// <summary>Suite de vues rendue au fil des rafraîchissements, la dernière étant répétée.</summary>
internal sealed class FakeLoader
{
    private readonly IReadOnlyList<CurrentDayView> _views;

    public FakeLoader(params CurrentDayView[] views) => _views = views;

    public int Calls { get; private set; }

    public Task<CurrentDayView> LoadAsync(CancellationToken ct)
    {
        var view = _views[Math.Min(Calls, _views.Count - 1)];
        Calls++;
        return Task.FromResult(view);
    }
}
