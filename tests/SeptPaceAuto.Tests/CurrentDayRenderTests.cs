using SeptPaceAuto.Terminal;
using Xunit;

namespace SeptPaceAuto.Tests;

/// <summary>
/// L'image de consultation est la réponse à « est-ce que le suivi marche ? ». Ces cas
/// vérifient qu'elle répond seule, sans jamais proposer de toucher à la journée en cours.
/// </summary>
public sealed class CurrentDayRenderTests
{
    [Fact]
    public void Journee_vide_le_dit_au_lieu_de_laisser_un_blanc()
    {
        var frame = Views.Render(Views.Day());

        Assert.Contains("Journée en cours — mardi 17 mars (2026-03-17)", frame);
        Assert.Contains("Aucun créneau collecté pour l’instant.", frame);
        Assert.Contains("Consultation seule", frame);
    }

    [Fact]
    public void Ecran_ne_propose_aucune_action_sur_la_journee_en_cours()
    {
        var frame = Views.Render(Views.Day(
            entries: new[] { Views.Span(1, "09:00", "10:30", 5678, "Réparer l’import") },
            total: 90,
            pending: 1));

        foreach (var action in new[]
                 {
                     "Ajouter ou corriger un créneau",
                     "Supprimer un créneau",
                     "Envoyer la journée",
                     "Ignorer cette journée",
                     "ENVOYER",
                     "SUPPRIMER",
                     "Identifiant",
                 })
        {
            Assert.DoesNotContain(action, frame, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Creneaux_montrent_heures_ticket_libelle_et_source()
    {
        var frame = Views.Render(Views.Day(
            entries: new[]
            {
                Views.Span(1, "09:00", "10:30", 5678, "Réparer l’import"),
                Views.Span(2, "10:30", "11:00", source: "gap"),
                Views.Span(3, "11:00", "11:20", source: "quick", label: "Chrono rapide · à attribuer"),
            },
            total: 140,
            unassigned: 2));

        Assert.Contains("09:00–10:30 · 1 h 30 · #5678 · Réparer l’import · suivi Git", frame);
        Assert.Contains("10:30–11:00 · 30 min · à attribuer · intervalle à préciser", frame);
        Assert.Contains("11:00–11:20 · 20 min · à attribuer · Chrono rapide · à attribuer · chrono rapide", frame);
        Assert.Contains("Total collecté : 2 h 20 sur 7 h 30 prévues", frame);
    }

    [Fact]
    public void Intervalles_non_attribues_et_chevauchements_sont_listes()
    {
        var frame = Views.Render(Views.Day(
            entries: new[]
            {
                Views.Span(1, "08:30", "09:30", 5678, "Réparer l’import"),
                Views.Span(2, "09:30", "10:00"),
            },
            overlaps: new[] { new[] { 570, 600 } },
            total: 90,
            unassigned: 1));

        Assert.Contains("Intervalles non attribués : 09:30–10:00", frame);
        Assert.Contains("Chevauchements : 09:30–10:00", frame);
    }

    [Fact]
    public void Seuls_les_trous_deja_ecoules_sont_annonces()
    {
        // Il est 10 h 30 : l'après-midi n'est pas un trou, elle n'a pas encore eu lieu.
        var frame = Views.Render(Views.Day(
            entries: new[] { Views.Span(1, "08:30", "09:30", 5678, "Réparer l’import") },
            holes: new[] { new[] { 570, 750 }, new[] { 810, 1020 } },
            total: 60));

        Assert.Contains("Trous déjà constatés : 09:30–10:30", frame);
        Assert.DoesNotContain("13:30–17:00", frame);
    }

    [Fact]
    public void Journee_du_jour_sans_temps_ecoule_n_annonce_aucun_trou()
    {
        var frame = Views.Render(
            Views.Day(date: "2026-03-18", holes: new[] { new[] { 510, 750 } }),
            now: new DateTimeOffset(2026, 3, 18, 0, 2, 0, TimeSpan.FromHours(1)));

        Assert.DoesNotContain("Trous déjà constatés", frame);
    }

    [Fact]
    public void Suivi_vivant_montre_sa_fraicheur_sans_alarme()
    {
        var frame = Views.Render(Views.Day(tracking: Views.Tracking(
            observed: Views.Now.AddSeconds(-4),
            branchAt: Views.Now.AddSeconds(-4),
            written: Views.Now.AddSeconds(-4),
            spanDate: Views.Date,
            spanStart: 540,
            spanEnd: 630)));

        Assert.Contains("Dernier relevé : 10:29:56 · il y a 4 s", frame);
        Assert.Contains("Créneau en cours : 09:00–10:30 (1 h 30)", frame);
        Assert.Contains("Branche : feature/1234-import", frame);
        Assert.Contains("Ticket : #5678 (depuis le Bug ou PBI #1234)", frame);
        Assert.DoesNotContain("suivi figé", frame);
    }

    [Fact]
    public void Suivi_fige_est_annonce_comme_tel()
    {
        var frame = Views.Render(Views.Day(tracking: Views.Tracking(
            observed: Views.Now.AddMinutes(-12),
            branchAt: Views.Now.AddMinutes(-12),
            written: Views.Now.AddMinutes(-12))));

        Assert.Contains("Dernier relevé : 10:18:00 · il y a 12 min — suivi figé", frame);
    }

    [Fact]
    public void Suivi_qui_na_jamais_releve_le_dit_plutot_que_de_mentir()
    {
        // Suivi qui n'a jamais abouti : aucun horodatage n'existe encore.
        var tracking = new TrackingView(
            "no-repo",
            "Dépôt Git introuvable",
            Branch: null,
            Bug: null,
            WorkItem: null,
            QuickRunning: false,
            ObservedAt: null,
            BranchAt: null,
            WrittenAt: null,
            SpanDate: null,
            SpanStart: null,
            SpanEnd: null,
            Error: null,
            ErrorAt: null,
            GitAvailable: false,
            StaleAfterSeconds: 90);

        var frame = Views.Render(Views.Day(tracking: tracking));

        Assert.Contains("Suivi : dépôt introuvable · Dépôt Git introuvable", frame);
        Assert.Contains("Branche : aucune branche lue", frame);
        Assert.Contains("Dernière lecture de branche : jamais — le suivi n’a encore rien relevé", frame);
        Assert.Contains("Git est introuvable sur ce poste", frame);
        Assert.Contains("Créneau en cours : aucun : rien n’est compté en ce moment", frame);
        Assert.Contains("Ticket : aucun, attribution à compléter demain", frame);
    }

    [Fact]
    public void Erreur_recente_du_suivi_est_visible()
    {
        var frame = Views.Render(Views.Day(tracking: Views.Tracking(
            error: "Impossible d’écrire la journée sur le disque.",
            errorAt: Views.Now.AddMinutes(-1))));

        Assert.Contains("Dernière erreur : Impossible d’écrire la journée sur le disque. (10:29:00)", frame);
    }

    [Fact]
    public void Creneau_ouvert_sur_la_veille_signale_le_passage_de_minuit()
    {
        var frame = Views.Render(Views.Day(
            date: "2026-03-18",
            tracking: Views.Tracking(spanDate: Views.Date, spanStart: 540, spanEnd: 630)));

        Assert.Contains("ouvert sur le 2026-03-17 : la journée a changé depuis le dernier relevé", frame);
    }

    [Fact]
    public void Bloc_tout_juste_ouvert_le_dit_sans_inventer_de_duree()
    {
        var frame = Views.Render(Views.Day(tracking: Views.Tracking(spanDate: Views.Date, spanStart: 630)));

        Assert.Contains("Créneau en cours : ouvert à 10:30, aucune minute encore écrite", frame);
    }

    [Theory]
    [InlineData(0, "Aucune journée terminée n’attend d’être envoyée.")]
    [InlineData(1, "1 journée terminée attend d’être corrigée puis envoyée : reviens au menu.")]
    [InlineData(3, "3 journées terminées attendent d’être corrigées puis envoyées : reviens au menu.")]
    public void File_du_matin_reste_rappelee(int pending, string expected)
    {
        Assert.Contains(expected, Views.Render(Views.Day(pending: pending)));
    }

    [Fact]
    public void Pied_de_page_annonce_le_rafraichissement_ou_son_absence()
    {
        Assert.Contains(
            "Rafraîchissement toutes les 2 s · appuie sur une touche pour revenir au menu.",
            Views.Render(Views.Day()));
        Assert.Contains(
            "Entrée redirigée : image unique, sans rafraîchissement.",
            Views.Render(Views.Day(), live: false));
    }
}
