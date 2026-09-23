#nullable enable
using System;
using System.Globalization;
using System.Threading.Tasks;
using SeptPaceAuto.Services;
using Xunit;

namespace SeptPaceAuto.Tests;

/// <summary>
/// Couture entre la politique de lecture Git et la consultation de la journée en cours :
/// une panne de lecture doit se voir comme telle, sans laisser croire à un suivi arrêté ni
/// à une collecte qui continue.
/// </summary>
public sealed class TrackingHealthTests
{
    private static readonly BranchRead Timeout = BranchRead.Failed("git a dépassé le délai");

    [Fact]
    public async Task Suivi_nominal_horodate_releve_lecture_ecriture_et_creneau()
    {
        using var harness = new TrackerHarness();
        await harness.PrimeAt(9, 0);
        await harness.TickAt(9, 1);

        var health = harness.Tracker.Health;

        Assert.Equal(harness.Moment(9, 1), At(health.ObservedAt));
        Assert.Equal(harness.Moment(9, 1), At(health.BranchAt));
        Assert.Equal(harness.Moment(9, 1), At(health.WrittenAt));
        Assert.Equal(harness.Date, health.SpanDate);
        Assert.Equal(9 * 60, health.SpanStart);
        Assert.Equal(9 * 60 + 1, health.SpanEnd);
        Assert.Null(health.Error);
        Assert.True(health.GitAvailable);
    }

    [Fact]
    public async Task Lecture_ratee_garde_le_releve_frais_et_fige_la_lecture_de_branche()
    {
        using var harness = new TrackerHarness();
        await harness.PrimeAt(9, 0);
        await harness.TickAt(9, 1);

        harness.Branches.Answer = Timeout;
        await harness.TickAt(9, 2);

        var health = harness.Tracker.Health;

        // Le suivi tourne toujours : c'est le dépôt qui ne répond plus, et la distinction
        // est exactement ce que la consultation doit montrer.
        Assert.Equal(harness.Moment(9, 2), At(health.ObservedAt));
        Assert.Equal(harness.Moment(9, 1), At(health.BranchAt));
        Assert.Equal(9 * 60 + 1, health.SpanEnd); // aucune minute inventée pendant la panne
        Assert.Equal("git-unreadable", harness.Tracker.Current.State);
        Assert.Null(health.Error); // un relevé qualifié n'est pas une exception
    }

    [Fact]
    public async Task Gel_de_la_collecte_ne_rapporte_plus_de_creneau_en_cours()
    {
        using var harness = new TrackerHarness();
        await harness.PrimeAt(9, 0);
        await harness.TickAt(9, 1);

        harness.Branches.Answer = Timeout;
        foreach (var minute in new[] { 2, 3, 4, 5 }) await harness.TickAt(9, minute);

        var health = harness.Tracker.Health;

        Assert.Null(health.SpanDate);
        Assert.Null(health.SpanStart);
        Assert.Null(health.SpanEnd);
        Assert.Equal(harness.Moment(9, 1), At(health.WrittenAt));
        Assert.Equal("git-unreadable", harness.Tracker.Current.State);
    }

    [Fact]
    public async Task Git_absent_du_poste_est_annonce_par_la_sante_du_suivi()
    {
        using var harness = new TrackerHarness();
        harness.Branches.Available = false;
        harness.Branches.Answer = BranchRead.Absent("git est introuvable sur ce poste");

        await harness.PrimeAt(9, 0);

        Assert.False(harness.Tracker.Health.GitAvailable);
        Assert.Equal("no-repo", harness.Tracker.Current.State);
    }

    [Theory]
    [InlineData(30, 90)]
    [InlineData(300, 900)]
    public async Task Delai_de_suivi_fige_suit_la_cadence_reglee(int pollSeconds, int expected)
    {
        using var harness = new TrackerHarness(pollSeconds);
        await harness.PrimeAt(9, 0);

        Assert.Equal(expected, harness.Tracker.Health.StaleAfterSeconds);
    }

    private static DateTime At(string? stamp) =>
        DateTime.Parse(stamp!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
