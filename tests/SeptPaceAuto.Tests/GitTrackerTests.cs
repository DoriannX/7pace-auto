#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using SeptPaceAuto.Services;
using Xunit;

namespace SeptPaceAuto.Tests;

/// <summary>
/// Politique de collecte : une lecture Git ratée n'est ni un dépôt absent, ni une veille du
/// poste. Ces relevés sont joués à la main sur une horloge figée, sans git ni attente.
/// </summary>
public sealed class GitTrackerTests
{
    private const string GapLabel = "Intervalle à préciser (poste en veille ou arrêté)";
    private static readonly BranchRead Timeout = BranchRead.Failed("git a dépassé le délai");

    [Fact]
    public async Task LectureNominale_ProlongeUnSeulCreneau()
    {
        using var harness = new TrackerHarness();

        await harness.PrimeAt(9, 0);
        await harness.TickAt(9, 0, 30);
        await harness.TickAt(9, 1);
        await harness.TickAt(9, 2);

        var entry = Assert.Single(harness.Entries());
        Assert.Equal("git", entry.Source);
        Assert.Equal("09:00", entry.Start);
        Assert.Equal("09:02", entry.End);
        Assert.Empty(harness.Gaps());
    }

    [Fact]
    public async Task EchecPonctuel_NeCoupePasLeCreneauEnCours()
    {
        using var harness = new TrackerHarness();
        await harness.PrimeAt(9, 0);
        await harness.TickAt(9, 1);

        harness.Branches.Answer = Timeout;
        await harness.TickAt(9, 2);

        // Le relevé raté ne ferme rien et n'ajoute aucune minute : il attend le suivant.
        var held = Assert.Single(harness.Entries());
        Assert.Equal("09:01", held.End);
        Assert.Equal("git-unreadable", harness.Tracker.Current.State);

        harness.Branches.Answer = BranchRead.Found("feature/34131-collecte");
        await harness.TickAt(9, 3);

        var entry = Assert.Single(harness.Entries());
        Assert.Equal("09:00", entry.Start);
        Assert.Equal("09:03", entry.End);
        Assert.Empty(harness.Gaps());
        Assert.Equal("running", harness.Tracker.Current.State);
    }

    [Fact]
    public async Task EchecsRepetes_GelentLeCreneauSansInventerDeTemps()
    {
        using var harness = new TrackerHarness();
        await harness.PrimeAt(9, 0);
        await harness.TickAt(9, 1);

        harness.Branches.Answer = Timeout;
        foreach (var minute in new[] { 2, 3, 4, 5 }) await harness.TickAt(9, minute);

        var entry = Assert.Single(harness.Entries());
        Assert.Equal("09:01", entry.End); // dernière minute réellement vérifiée
        Assert.Empty(harness.Gaps());
        Assert.Equal("git-unreadable", harness.Tracker.Current.State);
        Assert.Contains("collecte suspendue", harness.Tracker.Current.Label, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecuperationApresGel_OuvreUnCreneauSansDoublonNiChevauchement()
    {
        using var harness = new TrackerHarness();
        await harness.PrimeAt(9, 0);
        await harness.TickAt(9, 1);

        harness.Branches.Answer = Timeout;
        foreach (var minute in new[] { 2, 3, 4, 5 }) await harness.TickAt(9, minute);

        harness.Branches.Answer = BranchRead.Found("feature/34131-collecte");
        await harness.TickAt(9, 9);
        await harness.TickAt(9, 12);

        var entries = harness.Entries();
        Assert.Equal(2, entries.Count);
        Assert.Equal(("09:00", "09:01"), (entries[0].Start, entries[0].End));
        Assert.Equal(("09:09", "09:12"), (entries[1].Start, entries[1].End));
        Assert.Empty(harness.Gaps());
        Assert.Empty(DayStore.Overlaps(entries));
    }

    [Fact]
    public async Task RecuperationSurUneAutreBranche_BasculeSansTrouNiChevauchement()
    {
        using var harness = new TrackerHarness();
        await harness.PrimeAt(9, 0);
        await harness.TickAt(9, 2);

        harness.Branches.Answer = Timeout;
        await harness.TickAt(9, 3);

        harness.Branches.Answer = BranchRead.Found("feature/34999-autre");
        await harness.TickAt(9, 4);
        await harness.TickAt(9, 6);

        var entries = harness.Entries();
        Assert.Equal(2, entries.Count);
        Assert.Equal(("09:00", "09:04"), (entries[0].Start, entries[0].End));
        Assert.Equal(("09:04", "09:06"), (entries[1].Start, entries[1].End));
        Assert.Equal(34131, entries[0].Bug);
        Assert.Equal(34999, entries[1].Bug);
        Assert.Empty(harness.Gaps());
        Assert.Empty(DayStore.Overlaps(entries));
    }

    [Fact]
    public async Task HeadDetachee_EstSuivieCommeUnCreneauAAttribuer()
    {
        using var harness = new TrackerHarness();
        harness.Branches.Answer = BranchRead.Found("HEAD détachée");

        await harness.PrimeAt(9, 0);
        await harness.TickAt(9, 5);

        var entry = Assert.Single(harness.Entries());
        Assert.Equal("git", entry.Source);
        Assert.Equal(("09:00", "09:05"), (entry.Start, entry.End));
        Assert.True(entry.Unassigned);
        Assert.Contains("HEAD détachée", entry.Label, StringComparison.Ordinal);
        Assert.Empty(harness.Gaps());
    }

    [Fact]
    public async Task DepotAbsent_FermeLeCreneauEtLeSignale()
    {
        using var harness = new TrackerHarness();
        await harness.PrimeAt(9, 0);
        await harness.TickAt(9, 2);

        harness.Branches.Answer = BranchRead.Absent("le dossier n’est pas un dépôt Git");
        await harness.TickAt(9, 3);

        var entry = Assert.Single(harness.Entries());
        Assert.Equal("09:03", entry.End);
        Assert.Equal("no-repo", harness.Tracker.Current.State);
        Assert.Empty(harness.Gaps());
    }

    [Fact]
    public async Task Cadence300Secondes_NeFabriqueAucunIntervalleAPreciser()
    {
        using var harness = new TrackerHarness(pollSeconds: 300);

        // Une journée de travail relevée toutes les cinq minutes : chaque relevé arrive
        // quelques secondes après l'échéance, ce qui ne doit jamais valoir une absence.
        await harness.PrimeAt(9, 0);
        for (var elapsed = 5; elapsed <= 120; elapsed += 5)
        {
            await harness.TickAt(9 + (elapsed / 60), elapsed % 60, 1 + (elapsed % 3));
        }

        var entry = Assert.Single(harness.Entries());
        Assert.Equal("git", entry.Source);
        Assert.Equal(("09:00", "11:00"), (entry.Start, entry.End));
        Assert.Empty(harness.Gaps());
    }

    [Fact]
    public async Task VeilleProlongee_CombleLaPeriodeSansToucherAuCreneauPrecedent()
    {
        using var harness = new TrackerHarness(pollSeconds: 300);
        await harness.PrimeAt(9, 0);
        await harness.TickAt(9, 5, 2);

        // Poste endormi : aucun relevé pendant une heure vingt-cinq.
        await harness.TickAt(10, 30);
        await harness.TickAt(10, 35);

        var entries = harness.Entries();
        Assert.Equal(3, entries.Count);
        Assert.Equal(("09:00", "09:05"), (entries[0].Start, entries[0].End));
        Assert.Equal(("09:05", "10:30"), (entries[1].Start, entries[1].End));
        Assert.Equal("gap", entries[1].Source);
        Assert.Equal(GapLabel, entries[1].Label);
        Assert.Equal(("10:30", "10:35"), (entries[2].Start, entries[2].End));
        Assert.Empty(DayStore.Overlaps(entries));
    }

    [Fact]
    public async Task RedemarrageApresArret_CombleDepuisLeDernierBattement()
    {
        using var harness = new TrackerHarness();
        harness.State.Heartbeat = harness.Moment(9, 20);

        await harness.PrimeAt(11, 0);

        var entry = Assert.Single(harness.Entries());
        Assert.Equal("gap", entry.Source);
        Assert.Equal(("09:20", "11:00"), (entry.Start, entry.End));
    }

    [Fact]
    public async Task PauseDejeuner_FermeSurLaFinDeFenetreSansIntervalleAPreciser()
    {
        using var harness = new TrackerHarness(pollSeconds: 300);

        await harness.PrimeAt(12, 20);
        for (var minute = 12 * 60 + 25; minute <= 13 * 60 + 40; minute += 5)
        {
            await harness.TickAt(minute / 60, minute % 60, 1);
        }

        var entries = harness.Entries();
        Assert.Equal(2, entries.Count);
        Assert.Equal(("12:20", "12:30"), (entries[0].Start, entries[0].End));
        Assert.Equal(("13:30", "13:40"), (entries[1].Start, entries[1].End));
        Assert.Empty(harness.Gaps());
        Assert.Empty(DayStore.Overlaps(entries));
    }

    [Fact]
    public async Task Comblement_PreserveLesCreneauxManuelsEtEnvoyes()
    {
        using var harness = new TrackerHarness();
        harness.Days.Save(harness.Date, new Entry { Start = "09:00", End = "10:00", WorkItem = 4242, Label = "Réunion d’équipe" });
        harness.Days.Save(harness.Date, new Entry { Start = "10:00", End = "10:30", WorkItem = 777, Label = "Déjà envoyé" });
        var sent = harness.Entries().First(entry => entry.Start == "10:00");
        harness.Days.StampSent(harness.Date, new[] { sent.Id!.Value }, "2026-03-18T08:00:00+01:00");

        harness.State.Heartbeat = harness.Moment(8, 30);
        await harness.PrimeAt(11, 0);

        var entries = harness.Entries();
        var gaps = harness.Gaps();
        Assert.Equal(2, gaps.Count);
        Assert.Equal(("08:30", "09:00"), (gaps[0].Start, gaps[0].End));
        Assert.Equal(("10:30", "11:00"), (gaps[1].Start, gaps[1].End));
        Assert.Empty(DayStore.Overlaps(entries));

        var manual = entries.First(entry => entry.Start == "09:00");
        Assert.Equal("manual", manual.Source);
        Assert.Equal(4242, manual.WorkItem);
        Assert.Equal("Réunion d’équipe", manual.Label);
        Assert.NotNull(entries.First(entry => entry.Start == "10:00").SentAt);
    }

    [Fact]
    public async Task ChronoRapideOuvert_EmpecheLeDoublonALaReprise()
    {
        using var harness = new TrackerHarness();
        harness.State.Heartbeat = harness.Moment(9, 0);
        harness.State.Quick = new QuickTimerState { Date = harness.Date, StartMinute = 9 * 60 };

        await harness.PrimeAt(11, 0);

        var entry = Assert.Single(harness.Entries());
        Assert.Equal("quick", entry.Source);
        Assert.Equal(("09:00", "11:00"), (entry.Start, entry.End));
        Assert.Empty(harness.Gaps());
    }

    [Fact]
    public async Task JourneeClose_NeRecoitAucunIntervalleAPreciser()
    {
        using var harness = new TrackerHarness(day: new DateTime(2026, 3, 10));
        harness.Days.Close(harness.Date);
        harness.State.Heartbeat = harness.Moment(8, 30);

        await harness.PrimeAt(11, 0);

        Assert.Empty(harness.Entries());
    }
}
