using System.Text.Json;
using SeptPaceAuto.Services;
using Xunit;

namespace SeptPaceAuto.Tests;

/// <summary>
/// Le contrat « currentDay » est un diagnostic : il rend la journée en cours sans rien
/// écrire, sans appeler Azure ni 7pace, et sans jamais rendre cette journée modifiable.
/// </summary>
public sealed class CurrentDayContractTests
{
    private const string Today = "2026-03-17";
    private const string Yesterday = "2026-03-16";

    [Fact]
    public async Task Journee_en_cours_est_rendue_avec_son_suivi_et_la_file_du_matin()
    {
        Reset();
        var store = Store();
        store.WriteSpan(Yesterday, 540, 600, "Réparer l’import", 5678, null, "git", null);
        store.WriteSpan(Today, 540, 600, "Réparer l’import", 5678, null, "git", null);
        store.WriteSpan(Today, 600, 620, "Chrono rapide · à attribuer", null, null, "quick", null);

        await using var app = App(At(14, 0));
        using var payload = await CallAsync(app, "currentDay");
        var root = payload.RootElement;

        Assert.Equal(Today, root.GetProperty("date").GetString());
        Assert.Equal(2, root.GetProperty("entries").GetArrayLength());
        Assert.Equal(1, root.GetProperty("unassigned").GetInt32());
        Assert.Equal(1, root.GetProperty("pending").GetInt32());
        Assert.True(root.GetProperty("readOnly").GetBoolean());

        var health = root.GetProperty("health");
        Assert.Equal(JsonValueKind.Null, health.GetProperty("observedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, health.GetProperty("error").ValueKind);
        Assert.True(health.GetProperty("staleAfterSeconds").GetInt32() >= 30);
        Assert.Equal("outside-hours", root.GetProperty("tracking").GetProperty("state").GetString());
    }

    [Fact]
    public async Task File_du_matin_garde_la_priorite_sur_la_journee_en_cours()
    {
        Reset();
        var store = Store();
        store.WriteSpan(Yesterday, 540, 600, "Réparer l’import", 5678, null, "git", null);
        store.WriteSpan(Today, 540, 600, "Réparer l’import", 5678, null, "git", null);

        await using var app = App(At(14, 0));
        using var pending = await CallAsync(app, "pendingDay");
        using var current = await CallAsync(app, "currentDay");

        Assert.Equal(Yesterday, pending.RootElement.GetProperty("date").GetString());
        Assert.Equal(Today, current.RootElement.GetProperty("date").GetString());
    }

    [Theory]
    [InlineData("saveEntry")]
    [InlineData("deleteEntry")]
    [InlineData("submitDay")]
    [InlineData("discardDay")]
    public async Task Journee_en_cours_refuse_toute_mutation(string method)
    {
        Reset();
        Store().WriteSpan(Today, 540, 600, "Réparer l’import", 5678, null, "git", null);

        await using var app = App(At(14, 0));
        var parameters = JsonSerializer.Serialize(new
        {
            date = Today,
            id = 1,
            entry = new { id = 1, start = "09:00", end = "10:00", workItem = 5678 },
        });

        var error = await Assert.ThrowsAsync<DomainException>(
            () => app.HandleAsync(method, parameters, CancellationToken.None));
        Assert.Contains("journée en cours", error.Message, StringComparison.OrdinalIgnoreCase);

        using var current = await CallAsync(app, "currentDay");
        Assert.Equal(1, current.RootElement.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task Consultation_repetee_n_ecrit_rien_sur_le_disque()
    {
        Reset();
        Store().WriteSpan(Today, 540, 600, "Réparer l’import", 5678, null, "git", null);

        await using var app = App(At(14, 0));
        using (var warmup = await CallAsync(app, "currentDay"))
        {
            Assert.Equal(Today, warmup.RootElement.GetProperty("date").GetString());
        }

        var before = DaysOnDisk();
        for (var pass = 0; pass < 5; pass++)
        {
            using var ignored = await CallAsync(app, "currentDay");
        }

        Assert.Equal(before, DaysOnDisk());
    }

    [Fact]
    public async Task Passage_de_minuit_bascule_la_journee_consultee_et_la_file()
    {
        Reset();
        Store().WriteSpan(Today, 540, 1020, "Réparer l’import", 5678, null, "git", null);

        var clock = At(23, 58);
        await using var app = App(clock);

        using (var before = await CallAsync(app, "currentDay"))
        {
            Assert.Equal(Today, before.RootElement.GetProperty("date").GetString());
            Assert.Equal(1, before.RootElement.GetProperty("entries").GetArrayLength());
            Assert.Equal(0, before.RootElement.GetProperty("pending").GetInt32());
        }

        clock.Now = new DateTime(2026, 3, 18, 0, 2, 0, DateTimeKind.Local);

        using var after = await CallAsync(app, "currentDay");
        Assert.Equal("2026-03-18", after.RootElement.GetProperty("date").GetString());
        Assert.Equal(0, after.RootElement.GetProperty("entries").GetArrayLength());
        Assert.Equal(1, after.RootElement.GetProperty("pending").GetInt32());

        using var queue = await CallAsync(app, "pendingDay");
        Assert.Equal(Today, queue.RootElement.GetProperty("date").GetString());
    }

    [Fact]
    public void Creneau_prolonge_reste_un_seul_creneau()
    {
        Reset();
        var store = Store();

        var opened = store.WriteSpan(Today, 540, 560, "Réparer l’import", 5678, null, "git", null);
        var extended = store.WriteSpan(Today, 540, 575, "Réparer l’import", 5678, null, "git", opened!.Id);

        var day = store.Day(Today);
        Assert.Single(day);
        Assert.Equal(opened.Id, extended!.Id);
        Assert.Equal("09:00", day[0].Start);
        Assert.Equal("09:35", day[0].End);
    }

    // ---------- montage ----------

    private sealed class Clock
    {
        public Clock(DateTime now) => Now = now;

        public DateTime Now { get; set; }
    }

    private static Clock At(int hour, int minute) => new(new DateTime(2026, 3, 17, hour, minute, 0, DateTimeKind.Local));

    private static ITrackingApp App(Clock clock) => new TrackingApp(() => clock.Now);

    private static DayStore Store() => new(static () => Profile.Create(new AppSettings(), strict: false), AppPaths.Days);

    private static async Task<JsonDocument> CallAsync(ITrackingApp app, string method) =>
        JsonDocument.Parse(await app.HandleAsync(method, "{}", CancellationToken.None));

    /// <summary>Empreinte du dossier des journées : nom, taille et date de dernière écriture.</summary>
    private static List<string> DaysOnDisk() => Directory
        .EnumerateFiles(AppPaths.Days)
        .Select(path => new FileInfo(path))
        .Select(file => string.Join('|', file.Name, file.Length, file.LastWriteTimeUtc.Ticks))
        .OrderBy(static line => line, StringComparer.Ordinal)
        .ToList();

    /// <summary>Profil vierge : chaque cas part d'un disque connu, sans journée héritée.</summary>
    private static void Reset()
    {
        if (Directory.Exists(AppPaths.Days)) Directory.Delete(AppPaths.Days, recursive: true);
        Directory.CreateDirectory(AppPaths.Days);
        foreach (var path in new[] { AppPaths.ClosedDays, AppPaths.Quick, AppPaths.Heartbeat })
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
