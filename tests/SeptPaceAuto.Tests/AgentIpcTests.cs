using System.Diagnostics;
using System.Text.Json;
using SeptPaceAuto.Services;
using Xunit;

namespace SeptPaceAuto.Tests;

/// <summary>
/// Liaison entre le terminal et le collecteur. Tout y passe par un vrai tuyau nommé : ce
/// sont les mêmes classes que sur le poste de l'utilisateur, seul le cœur métier est
/// scénarisé pour que chaque cas soit reproductible.
/// </summary>
public sealed class AgentIpcTests
{
    [Fact]
    public async Task Le_terminal_joint_le_collecteur_et_lit_sa_carte_de_visite()
    {
        await using var harness = new HostHarness();
        var client = harness.Client();

        Assert.True(await client.ConnectAsync(launchIfMissing: false, CancellationToken.None));
        Assert.True(client.Connected);
        Assert.Equal(AgentEndpoint.Protocol, client.Agent!.Protocol);
        Assert.Equal(Environment.ProcessId, client.Agent.Pid);
        Assert.Null(client.Trouble);
    }

    [Fact]
    public async Task Le_collecteur_depose_sa_presence_puis_la_retire()
    {
        var harness = new HostHarness();
        try
        {
            Assert.True(File.Exists(harness.Endpoint.InfoPath));
            Assert.Equal(Environment.ProcessId, harness.Endpoint.ReadInfo()!.Pid);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task Un_appel_traverse_le_tuyau_avec_ses_parametres()
    {
        await using var harness = new HostHarness();
        harness.App.Handler = (method, parameters, _) =>
            Task.FromResult("{\"method\":\"" + method + "\",\"recu\":" + parameters + "}");

        var client = harness.Client();
        var response = await client.HandleAsync("saveEntry", "{\"date\":\"2026-03-17\"}", CancellationToken.None);

        using var document = JsonDocument.Parse(response);
        Assert.Equal("saveEntry", document.RootElement.GetProperty("method").GetString());
        Assert.Equal("2026-03-17", document.RootElement.GetProperty("recu").GetProperty("date").GetString());
    }

    [Fact]
    public async Task Une_erreur_metier_arrive_avec_sa_phrase_francaise()
    {
        await using var harness = new HostHarness();
        harness.App.Handler = (_, _, _) => throw new DomainException("La journée en cours se traite demain matin.");

        var client = harness.Client();
        var error = await Assert.ThrowsAsync<DomainException>(
            () => client.HandleAsync("saveEntry", "{}", CancellationToken.None));

        Assert.Equal("La journée en cours se traite demain matin.", error.Message);
        Assert.True(client.Connected);
    }

    [Fact]
    public async Task Une_panne_du_collecteur_ne_ferme_pas_la_liaison()
    {
        await using var harness = new HostHarness();
        harness.App.Handler = (_, _, _) => throw new InvalidOperationException("index hors limites");

        var client = harness.Client();
        var error = await Assert.ThrowsAsync<DomainException>(
            () => client.HandleAsync("currentDay", "{}", CancellationToken.None));

        Assert.Contains("Panne du collecteur", error.Message, StringComparison.Ordinal);

        harness.App.Handler = static (_, _, _) => Task.FromResult("{\"ok\":true}");
        Assert.Contains("ok", await client.HandleAsync("currentDay", "{}", CancellationToken.None), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sans_collecteur_le_terminal_le_dit_au_lieu_de_pretendre_suivre()
    {
        var folder = Path.Combine(Path.GetTempPath(), "7pace-auto-tests", Guid.NewGuid().ToString("N"));
        await using var client = new AgentClient(AgentEndpoint.For(folder), new DeadLauncher());

        Assert.False(await client.ConnectAsync(launchIfMissing: true, CancellationToken.None));
        Assert.False(client.Connected);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => client.HandleAsync("currentDay", "{}", CancellationToken.None));
        Assert.Contains("injoignable", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deux_terminaux_n_ecrivent_jamais_en_meme_temps()
    {
        await using var harness = new HostHarness();
        harness.App.Handler = async (_, _, ct) =>
        {
            await Task.Delay(150, ct);
            return "{\"ok\":true}";
        };

        var left = harness.Client();
        var right = harness.Client();
        await Task.WhenAll(
            left.HandleAsync("saveEntry", "{}", CancellationToken.None),
            right.HandleAsync("saveEntry", "{}", CancellationToken.None));

        Assert.Equal(2, harness.App.Count("saveEntry"));
        Assert.Equal(1, harness.App.Peak);
    }

    [Fact]
    public async Task Une_consultation_reste_vivante_pendant_une_ecriture_longue()
    {
        await using var harness = new HostHarness();
        harness.App.Handler = async (method, _, ct) =>
        {
            if (string.Equals(method, "submitDay", StringComparison.Ordinal)) await Task.Delay(1500, ct);
            return "{\"ok\":true}";
        };

        var sender = harness.Client();
        var reader = harness.Client();

        var sending = sender.HandleAsync("submitDay", "{}", CancellationToken.None);
        var watch = Stopwatch.StartNew();
        await reader.HandleAsync("currentDay", "{}", CancellationToken.None);
        watch.Stop();

        Assert.True(watch.ElapsedMilliseconds < 1000, $"La consultation a attendu {watch.ElapsedMilliseconds} ms.");
        await sending;
    }

    [Fact]
    public async Task Le_terminal_se_rattache_apres_un_redemarrage_du_collecteur()
    {
        await using var harness = new HostHarness();
        var client = harness.Client();
        await client.HandleAsync("currentDay", "{}", CancellationToken.None);

        await harness.RestartAsync();

        var response = await client.HandleAsync("currentDay", "{}", CancellationToken.None);
        Assert.Contains("echo", response, StringComparison.Ordinal);
        Assert.True(client.Connected);
    }

    [Fact]
    public async Task Une_ecriture_coupee_en_vol_est_signalee_et_jamais_rejouee()
    {
        var folder = Path.Combine(Path.GetTempPath(), "7pace-auto-tests", Guid.NewGuid().ToString("N"));
        var endpoint = AgentEndpoint.For(folder);
        await using var rude = new RudeHost(endpoint);
        await using var client = new AgentClient(endpoint, new DeadLauncher());

        Assert.True(await client.ConnectAsync(launchIfMissing: false, CancellationToken.None));

        var error = await Assert.ThrowsAsync<DomainException>(
            () => client.HandleAsync("saveEntry", "{}", CancellationToken.None));

        Assert.Contains("liaison", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("saveEntry", error.Message, StringComparison.Ordinal);
        Assert.False(client.Connected);
    }

    [Theory]
    [InlineData("currentDay", true)]
    [InlineData("bootstrap", true)]
    [InlineData("checkUpdate", true)]
    [InlineData("agent.hello", true)]
    [InlineData("saveEntry", false)]
    [InlineData("deleteEntry", false)]
    [InlineData("submitDay", false)]
    [InlineData("discardDay", false)]
    [InlineData("setQuick", false)]
    [InlineData("saveSettings", false)]
    [InlineData("applyUpdate", false)]
    [InlineData("pendingDay", false)]
    public void Seuls_les_appels_de_lecture_peuvent_etre_rejoues(string method, bool expected)
    {
        Assert.Equal(expected, AgentClient.CanReplay(method));
    }

    [Fact]
    public async Task Un_collecteur_muet_finit_par_rendre_la_main()
    {
        await using var harness = new HostHarness();
        harness.App.Handler = async (_, _, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return "{}";
        };

        var client = harness.Client(patience: TimeSpan.FromMilliseconds(400));
        var error = await Assert.ThrowsAsync<DomainException>(
            () => client.HandleAsync("submitDay", "{}", CancellationToken.None));

        Assert.Contains("délai", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task L_arret_explicite_est_confirme_puis_le_collecteur_se_retire()
    {
        await using var harness = new HostHarness();
        var client = harness.Client();

        var message = await client.HandleAsync("agent.stop", "{}", CancellationToken.None);

        Assert.Contains("arrêté", message, StringComparison.Ordinal);
        Assert.True(await harness.Host.StopRequested);
        Assert.Empty(harness.App.Seen);
    }

    [Fact]
    public async Task Fermer_le_terminal_ne_touche_pas_au_collecteur()
    {
        await using var harness = new HostHarness();

        var first = harness.Client();
        await first.HandleAsync("currentDay", "{}", CancellationToken.None);
        await first.DisposeAsync();

        var second = harness.Client();
        Assert.True(await second.ConnectAsync(launchIfMissing: false, CancellationToken.None));
        Assert.Contains("echo", await second.HandleAsync("currentDay", "{}", CancellationToken.None), StringComparison.Ordinal);
        Assert.False(harness.Host.StopRequested.IsCompleted);
    }
}
