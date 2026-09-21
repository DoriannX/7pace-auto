using SeptPaceAuto.Terminal;
using Xunit;

namespace SeptPaceAuto.Tests;

/// <summary>
/// L'écran doit vivre pendant qu'il est ouvert : voir arriver un créneau, suivre un
/// changement d'état, et s'arrêter net sans voler la saisie du menu.
/// </summary>
public sealed class CurrentDayScreenTests
{
    private static CurrentDayScreen Screen(FakeLoader loader, FakeSurface surface) =>
        new(loader.LoadAsync, surface, () => Views.Now, Views.Refresh);

    [Fact]
    public async Task Creneau_qui_arrive_pendant_l_affichage_redessine()
    {
        var loader = new FakeLoader(
            Views.Day(),
            Views.Day(entries: new[] { Views.Span(1, "09:00", "09:20", 5678, "Réparer l’import") }, total: 20));
        var surface = new FakeSurface(interactive: true, false, true);

        await Screen(loader, surface).RunAsync(CancellationToken.None);

        Assert.Equal(2, surface.Frames.Count);
        Assert.Contains("Aucun créneau collecté", surface.Frames[0]);
        Assert.Contains("09:00–09:20", surface.Frames[1]);
    }

    [Fact]
    public async Task Creneau_prolonge_reste_une_seule_ligne()
    {
        var loader = new FakeLoader(
            Views.Day(entries: new[] { Views.Span(1, "09:00", "09:20", 5678, "Réparer l’import") }, total: 20),
            Views.Day(entries: new[] { Views.Span(1, "09:00", "09:40", 5678, "Réparer l’import") }, total: 40));
        var surface = new FakeSurface(interactive: true, false, true);

        await Screen(loader, surface).RunAsync(CancellationToken.None);

        var last = surface.Frames[^1];
        Assert.Contains("09:00–09:40", last);
        Assert.Equal(1, Occurrences(last, "09:00–"));
    }

    [Fact]
    public async Task Vue_inchangee_ne_redessine_pas()
    {
        var loader = new FakeLoader(Views.Day(entries: new[] { Views.Span(1, "09:00", "09:20", 5678) }, total: 20));
        var surface = new FakeSurface(interactive: true, false, false, true);

        var screen = Screen(loader, surface);
        await screen.RunAsync(CancellationToken.None);

        Assert.Equal(3, loader.Calls);
        Assert.Equal(3, surface.Waits);
        Assert.Single(surface.Frames);
        Assert.Equal(1, screen.Frames);
    }

    [Fact]
    public async Task Changement_de_branche_et_d_etat_est_redessine()
    {
        var loader = new FakeLoader(
            Views.Day(tracking: Views.Tracking(branch: "feature/1234-import")),
            Views.Day(tracking: Views.Tracking(
                state: "outside-hours",
                label: "Hors horaires de travail",
                branch: "hotfix/9999-urgence",
                bug: 9999,
                workItem: null)));
        var surface = new FakeSurface(interactive: true, false, true);

        await Screen(loader, surface).RunAsync(CancellationToken.None);

        Assert.Equal(2, surface.Frames.Count);
        Assert.Contains("Branche : feature/1234-import", surface.Frames[0]);
        Assert.Contains("Branche : hotfix/9999-urgence", surface.Frames[1]);
        Assert.Contains("Suivi : hors horaires", surface.Frames[1]);
        Assert.Contains("#9999 lu dans la branche, Fix ou Task pas encore résolu", surface.Frames[1]);
    }

    [Fact]
    public async Task Passage_de_minuit_suit_la_nouvelle_journee()
    {
        var loader = new FakeLoader(
            Views.Day(entries: new[] { Views.Span(1, "09:00", "17:00", 5678) }, total: 480),
            Views.Day(date: "2026-03-18"));
        var surface = new FakeSurface(interactive: true, false, true);

        await Screen(loader, surface).RunAsync(CancellationToken.None);

        Assert.Contains("mardi 17 mars (2026-03-17)", surface.Frames[0]);
        Assert.Contains("mercredi 18 mars (2026-03-18)", surface.Frames[1]);
        Assert.Contains("Aucun créneau collecté", surface.Frames[1]);
        Assert.DoesNotContain("09:00–17:00", surface.Frames[1]);
    }

    [Fact]
    public async Task Touche_frappee_rend_la_main_sans_relire()
    {
        var loader = new FakeLoader(Views.Day());
        var surface = new FakeSurface(interactive: true, true);

        await Screen(loader, surface).RunAsync(CancellationToken.None);

        Assert.Equal(1, loader.Calls);
        Assert.Equal(1, surface.Waits);
        Assert.Single(surface.Frames);
    }

    [Fact]
    public async Task Entree_redirigee_rend_une_image_sans_toucher_au_clavier()
    {
        var loader = new FakeLoader(Views.Day());
        var surface = new FakeSurface(interactive: false);

        await Screen(loader, surface).RunAsync(CancellationToken.None);

        Assert.Equal(1, loader.Calls);
        Assert.Equal(0, surface.Waits);
        Assert.Single(surface.Frames);
        Assert.Contains("Entrée redirigée", surface.Frames[0]);
    }

    [Fact]
    public async Task Arret_demande_ferme_l_ecran_sans_rien_dessiner()
    {
        var loader = new FakeLoader(Views.Day());
        var surface = new FakeSurface(interactive: true);
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();

        await Screen(loader, surface).RunAsync(stopped.Token);

        Assert.Equal(0, loader.Calls);
        Assert.Empty(surface.Frames);
    }

    [Fact]
    public async Task Derniere_vue_est_gardee_pour_le_menu()
    {
        var loader = new FakeLoader(Views.Day(tracking: Views.Tracking(quick: true)));
        var surface = new FakeSurface(interactive: true, true);

        var screen = Screen(loader, surface);
        await screen.RunAsync(CancellationToken.None);

        Assert.NotNull(screen.Last);
        Assert.True(screen.Last!.Tracking.QuickRunning);
        Assert.Equal("running", screen.Last.Tracking.State);
    }

    private static int Occurrences(string text, string needle)
    {
        var count = 0;
        for (var index = text.IndexOf(needle, StringComparison.Ordinal); index >= 0; index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }
}
