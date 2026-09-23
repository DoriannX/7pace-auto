using System.Text.Json;
using SeptPaceAuto.Services;
using Xunit;

namespace SeptPaceAuto.Tests;

/// <summary>
/// Le collecteur tel qu'il tourne vraiment : un processus séparé, sans fenêtre, sur un
/// profil de données temporaire. Ces cas attendent de vrais relevés, et c'est le prix de la
/// seule preuve qui compte : fermer le terminal n'arrête plus la collecte.
/// </summary>
public sealed class AgentProcessTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(45);

    [Fact]
    public async Task Le_terminal_demarre_le_collecteur_absent_puis_s_y_connecte()
    {
        await using var profile = new AgentProcessProfile();
        var client = profile.Client();

        Assert.True(await client.ConnectAsync(launchIfMissing: true, CancellationToken.None));
        Assert.NotEqual(Environment.ProcessId, client.Agent!.Pid);
        Assert.Equal(profile.Folder, client.Agent.DataFolder);
        Assert.True(File.Exists(profile.Endpoint.InfoPath));

        await client.DisposeAsync();
    }

    [Fact]
    public async Task Un_second_collecteur_se_retire_au_lieu_d_ecrire_en_double()
    {
        await using var profile = new AgentProcessProfile();
        var client = profile.Client();
        Assert.True(await client.ConnectAsync(launchIfMissing: true, CancellationToken.None));
        var premier = client.Agent!.Pid;

        var intrus = profile.Start(AgentProcessProfile.AgentExecutable);
        Assert.True(await AgentProcessProfile.Exited(intrus, TimeSpan.FromSeconds(20)));
        Assert.Equal(2, intrus.ExitCode);

        // Le collecteur en place n'a pas bougé, et c'est toujours lui qui répond.
        Assert.Equal(premier, profile.Endpoint.ReadInfo()!.Pid);
        await client.DisposeAsync();

        var second = profile.Client();
        Assert.True(await second.ConnectAsync(launchIfMissing: false, CancellationToken.None));
        Assert.Equal(premier, second.Agent!.Pid);
        await second.DisposeAsync();
    }

    [Fact]
    public async Task Fermer_le_terminal_laisse_le_collecteur_collecter()
    {
        await using var profile = new AgentProcessProfile();
        var client = profile.Client();
        Assert.True(await client.ConnectAsync(launchIfMissing: true, CancellationToken.None));

        // Le chrono rapide donne un état observable sans dépendre d'un dépôt Git.
        await client.HandleAsync("setQuick", "{\"running\":true}", CancellationToken.None);
        var date = Today(await client.HandleAsync("bootstrap", "{}", CancellationToken.None));
        Assert.Contains(date, profile.Read("quick.json"), StringComparison.Ordinal);
        var avant = Observed(await client.HandleAsync("currentDay", "{}", CancellationToken.None));

        // Le terminal s'en va : c'est exactement le geste qui coupait tout en 1.0.9.
        await client.DisposeAsync();

        var battement = profile.Heartbeat();
        var cycles = 0;
        Assert.True(
            await AgentProcessProfile.WaitFor(
                () =>
                {
                    var courant = profile.Heartbeat();
                    if (courant.Length == 0 || string.Equals(courant, battement, StringComparison.Ordinal)) return false;
                    battement = courant;
                    return ++cycles >= 2;
                },
                Patience),
            "Le collecteur a cessé de relever après la fermeture du terminal.");

        // Rouvrir le terminal retrouve l'état, et la collecte a bien avancé entre-temps.
        var repris = profile.Client();
        Assert.True(await repris.ConnectAsync(launchIfMissing: false, CancellationToken.None));

        var vue = await repris.HandleAsync("currentDay", "{}", CancellationToken.None);
        using (var document = JsonDocument.Parse(vue))
        {
            Assert.True(document.RootElement.GetProperty("tracking").GetProperty("quickRunning").GetBoolean());
        }
        Assert.True(Observed(vue) > avant, "Le dernier relevé n’a pas avancé pendant l’absence du terminal.");

        await repris.DisposeAsync();
    }

    [Fact]
    public async Task L_arret_explicite_ferme_la_collecte_et_retire_la_presence()
    {
        await using var profile = new AgentProcessProfile();
        var client = profile.Client();
        Assert.True(await client.ConnectAsync(launchIfMissing: true, CancellationToken.None));
        await client.HandleAsync("setQuick", "{\"running\":true}", CancellationToken.None);
        var date = Today(await client.HandleAsync("bootstrap", "{}", CancellationToken.None));

        var message = await client.StopAgentAsync(CancellationToken.None);
        Assert.Contains("arrêté", message, StringComparison.Ordinal);

        Assert.True(await AgentProcessProfile.WaitFor(() => !File.Exists(profile.Endpoint.InfoPath), TimeSpan.FromSeconds(20)));
        Assert.False(client.Connected);

        // L'état est écrit avant de sortir : le chrono ouvert et le battement survivent.
        Assert.Contains(date, profile.Read("quick.json"), StringComparison.Ordinal);
        Assert.NotEqual(string.Empty, profile.Heartbeat());

        // Plus personne ne répond : l'arrêt est complet, pas seulement celui de l'interface.
        var orphelin = profile.Client();
        Assert.False(await orphelin.ConnectAsync(launchIfMissing: false, CancellationToken.None));
        await orphelin.DisposeAsync();
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Deux_profils_de_donnees_ne_se_voient_pas()
    {
        await using var gauche = new AgentProcessProfile();
        await using var droite = new AgentProcessProfile();

        var premier = gauche.Client();
        var second = droite.Client();
        Assert.True(await premier.ConnectAsync(launchIfMissing: true, CancellationToken.None));
        Assert.True(await second.ConnectAsync(launchIfMissing: true, CancellationToken.None));
        Assert.NotEqual(premier.Agent!.Pid, second.Agent!.Pid);

        await premier.HandleAsync("setQuick", "{\"running\":true}", CancellationToken.None);
        var date = Today(await premier.HandleAsync("bootstrap", "{}", CancellationToken.None));

        Assert.Contains(date, gauche.Read("quick.json"), StringComparison.Ordinal);
        Assert.Equal(string.Empty, droite.Read("quick.json"));

        using var vue = JsonDocument.Parse(await second.HandleAsync("currentDay", "{}", CancellationToken.None));
        Assert.False(vue.RootElement.GetProperty("tracking").GetProperty("quickRunning").GetBoolean());

        await premier.DisposeAsync();
        await second.DisposeAsync();
    }

    [Fact]
    public async Task Le_collecteur_renseigne_puis_obeit_en_ligne_de_commande()
    {
        await using var profile = new AgentProcessProfile();
        var client = profile.Client();
        Assert.True(await client.ConnectAsync(launchIfMissing: true, CancellationToken.None));

        var etat = profile.Start(AgentProcessProfile.AgentExecutable, "--status");
        Assert.True(await AgentProcessProfile.Exited(etat, TimeSpan.FromSeconds(20)));
        Assert.Equal(0, etat.ExitCode);

        var arret = profile.Start(AgentProcessProfile.AgentExecutable, "--stop");
        Assert.True(await AgentProcessProfile.Exited(arret, TimeSpan.FromSeconds(30)));
        Assert.Equal(0, arret.ExitCode);
        Assert.True(await AgentProcessProfile.WaitFor(() => !File.Exists(profile.Endpoint.InfoPath), TimeSpan.FromSeconds(20)));

        // Plus rien à arrêter : la commande reste bénigne.
        var vide = profile.Start(AgentProcessProfile.AgentExecutable, "--stop");
        Assert.True(await AgentProcessProfile.Exited(vide, TimeSpan.FromSeconds(20)));
        Assert.Equal(0, vide.ExitCode);

        var absent = profile.Start(AgentProcessProfile.AgentExecutable, "--status");
        Assert.True(await AgentProcessProfile.Exited(absent, TimeSpan.FromSeconds(20)));
        Assert.Equal(1, absent.ExitCode);

        await client.DisposeAsync();
    }

    private static string Today(string bootstrap)
    {
        using var document = JsonDocument.Parse(bootstrap);
        return document.RootElement.GetProperty("today").GetString()!;
    }

    /// <summary>Horodatage du dernier relevé mené à son terme, vu depuis la consultation.</summary>
    private static DateTimeOffset Observed(string currentDay)
    {
        using var document = JsonDocument.Parse(currentDay);
        var raw = document.RootElement.GetProperty("health").GetProperty("observedAt").GetString();
        return DateTimeOffset.Parse(raw!, System.Globalization.CultureInfo.InvariantCulture);
    }
}
