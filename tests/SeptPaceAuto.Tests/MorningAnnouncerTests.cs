using SeptPaceAuto.Services;
using Xunit;

namespace SeptPaceAuto.Tests;

/// <summary>
/// La notification du matin appartient au collecteur, qui vit toute la journée. Elle doit
/// partir une seule fois par journée calendaire, jamais en pleine nuit, et ne pas rejouer
/// une annonce déjà faite après un redémarrage du collecteur.
/// </summary>
public sealed class MorningAnnouncerTests
{
    [Fact]
    public async Task Une_seule_notification_par_journee_meme_apres_redemarrage()
    {
        using var profil = new Memoire();
        var app = new ScriptedApp { Handler = static (_, _, _) => Task.FromResult("{\"pending\":2,\"workStart\":510}") };
        var horloge = new DateTime(2026, 3, 17, 9, 5, 0, DateTimeKind.Local);
        var envois = new List<string>();

        await using (var veilleur = Veilleur(app, () => horloge, profil.Fichier, envois))
        {
            Assert.True(await veilleur.AnnounceOnceAsync(CancellationToken.None));
            Assert.False(await veilleur.AnnounceOnceAsync(CancellationToken.None));
        }
        Assert.Single(envois);
        Assert.Contains("2 journées", envois[0], StringComparison.Ordinal);

        // Le collecteur redémarre : la journée déjà annoncée est relue sur le disque.
        await using (var repris = Veilleur(app, () => horloge, profil.Fichier, envois))
        {
            Assert.False(await repris.AnnounceOnceAsync(CancellationToken.None));
        }
        Assert.Single(envois);

        // Le lendemain, la file mérite à nouveau d'être signalée.
        horloge = new DateTime(2026, 3, 18, 9, 5, 0, DateTimeKind.Local);
        await using (var demain = Veilleur(app, () => horloge, profil.Fichier, envois))
        {
            Assert.True(await demain.AnnounceOnceAsync(CancellationToken.None));
        }
        Assert.Equal(2, envois.Count);
    }

    [Fact]
    public async Task Le_passage_de_minuit_ne_reveille_personne()
    {
        using var profil = new Memoire();
        var app = new ScriptedApp { Handler = static (_, _, _) => Task.FromResult("{\"pending\":1,\"workStart\":510}") };
        var envois = new List<string>();

        await using var veilleur = Veilleur(app, static () => new DateTime(2026, 3, 18, 0, 1, 0, DateTimeKind.Local), profil.Fichier, envois);
        Assert.False(await veilleur.AnnounceOnceAsync(CancellationToken.None));
        Assert.Empty(envois);
    }

    [Fact]
    public async Task Un_collecteur_muet_ne_notifie_rien()
    {
        using var profil = new Memoire();
        var app = new ScriptedApp { Handler = static (_, _, _) => throw new DomainException("Journées illisibles.") };
        var envois = new List<string>();

        await using var veilleur = Veilleur(app, static () => new DateTime(2026, 3, 17, 9, 5, 0, DateTimeKind.Local), profil.Fichier, envois);
        Assert.False(await veilleur.AnnounceOnceAsync(CancellationToken.None));
        Assert.Empty(envois);
    }

    private static MorningAnnouncer Veilleur(ITrackingApp app, Func<DateTime> horloge, string memoire, List<string> envois) =>
        new(app, horloge, terminal: null, memoire, period: null, announce: (_, message, _) => envois.Add(message));

    /// <summary>Fichier de mémoire jetable : chaque cas part d'une ardoise vierge.</summary>
    private sealed class Memoire : IDisposable
    {
        public Memoire()
        {
            var folder = Path.Combine(Path.GetTempPath(), "7pace-auto-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            Fichier = Path.Combine(folder, "notified.json");
        }

        public string Fichier { get; }

        public void Dispose()
        {
            try
            {
                var folder = Path.GetDirectoryName(Fichier);
                if (folder is not null) Directory.Delete(folder, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Un dossier temporaire qui survit ne fait échouer aucun test.
            }
        }
    }
}
