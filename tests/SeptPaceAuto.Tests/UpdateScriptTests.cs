using System.Diagnostics;
using System.Text;
using SeptPaceAuto.Services;
using Xunit;

namespace SeptPaceAuto.Tests;

/// <summary>
/// Remplacement des binaires pendant une mise à jour. Le script produit est joué pour de
/// vrai contre un collecteur en marche : il ne doit toucher à rien tant qu'un processus
/// tient les fichiers, puis remplacer et relancer, sans jamais perdre le profil de données.
/// </summary>
public sealed class UpdateScriptTests
{
    private const string Marqueur = "nouvelle-version.txt";

    /// <summary>Fichiers qui suffisent à faire tourner le collecteur depuis un autre dossier.</summary>
    private static readonly string[] Pieces =
    {
        "SeptPaceAuto.Agent.exe",
        "SeptPaceAuto.Agent.dll",
        "SeptPaceAuto.Agent.deps.json",
        "SeptPaceAuto.Agent.runtimeconfig.json",
        "SeptPaceAuto.Core.dll",
    };

    [Fact]
    public void Le_script_attend_les_deux_processus_sauvegarde_puis_relance_le_collecteur()
    {
        var texte = SeptPaceAuto.Services.GitHubUpdateService.Script(
            @"C:\staged",
            (@"C:\install\SeptPaceAuto.Agent.exe", @"C:\install\SeptPaceAuto.App.exe", @"C:\install"),
            clientPid: 4242,
            reopenApp: true);

        Assert.Contains(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 4242", texte, StringComparison.Ordinal);
        Assert.Contains("for %%P in (%PIDS%)", texte, StringComparison.Ordinal);
        Assert.Contains("if defined RESTE", texte, StringComparison.Ordinal);
        Assert.Contains("robocopy \"%INSTALL%\" \"%BACKUP%\"", texte, StringComparison.Ordinal);
        Assert.Contains("robocopy \"%STAGED%\" \"%INSTALL%\"", texte, StringComparison.Ordinal);
        Assert.Contains("robocopy \"%BACKUP%\" \"%INSTALL%\"", texte, StringComparison.Ordinal);
        Assert.Contains("start \"\" \"%AGENT%\"", texte, StringComparison.Ordinal);
        Assert.Contains("start \"\" \"%APP%\" --widget", texte, StringComparison.Ordinal);
    }

    [Fact]
    public void L_app_n_est_rouverte_que_si_elle_l_a_demande()
    {
        var texte = SeptPaceAuto.Services.GitHubUpdateService.Script(
            @"C:\staged",
            (@"C:\install\SeptPaceAuto.Agent.exe", @"C:\install\SeptPaceAuto.App.exe", @"C:\install"),
            clientPid: null,
            reopenApp: false);

        Assert.DoesNotContain("%APP%\" --widget", texte, StringComparison.Ordinal);
        Assert.Contains("start \"\" \"%AGENT%\"", texte, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Une_mise_a_jour_jouee_pour_de_vrai_remplace_puis_relance_sans_rien_perdre()
    {
        await using var profile = new AgentProcessProfile();
        var racine = Path.Combine(Path.GetTempPath(), "7pace-auto-tests", Guid.NewGuid().ToString("N"));
        var install = Path.Combine(racine, "install");
        var staged = Path.Combine(racine, "staged");
        Poser(install, marqueur: false);
        Poser(staged, marqueur: true);

        var collecteur = Path.Combine(install, "SeptPaceAuto.Agent.exe");
        var enPlace = profile.Start(collecteur);
        var client = new AgentClient(profile.Endpoint, new NoLaunch());
        Assert.True(await client.ConnectAsync(launchIfMissing: false, CancellationToken.None));
        var battementAvant = profile.Heartbeat();
        Assert.NotEqual(string.Empty, battementAvant);

        var script = Path.Combine(racine, "apply.cmd");
        var texte = SeptPaceAuto.Services.GitHubUpdateService.Script(
            staged,
            (collecteur, Path.Combine(install, "SeptPaceAuto.App.exe"), install),
            clientPid: enPlace.Id,
            reopenApp: false);
        File.WriteAllText(script, texte, new UTF8Encoding(false));

        // Le profil temporaire est imposé au script : le collecteur qu'il relancera doit
        // repartir sur les mêmes données, pas sur celles du poste.
        var lancement = new ProcessStartInfo(script)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = racine,
        };
        lancement.Environment["SEPTPACE_DATA"] = profile.Folder;
        using var installation = Process.Start(lancement)!;

        // Tant que le collecteur tient les fichiers, rien n'est remplacé.
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.False(File.Exists(Path.Combine(install, Marqueur)), "La mise à jour a commencé alors que le collecteur tournait.");
        Assert.False(installation.HasExited);

        // Arrêt gracieux : le collecteur écrit son dernier relevé puis libère les binaires.
        await client.StopAgentAsync(CancellationToken.None);
        await client.DisposeAsync();
        Assert.True(await AgentProcessProfile.Exited(enPlace, TimeSpan.FromSeconds(30)));

        Assert.True(
            await AgentProcessProfile.WaitFor(() => File.Exists(Path.Combine(install, Marqueur)), TimeSpan.FromSeconds(60)),
            "Les fichiers n’ont pas été remplacés après la sortie du collecteur.");

        // Le suivi repart tout seul sur la nouvelle version, et le profil est intact.
        var repris = new AgentClient(profile.Endpoint, new NoLaunch());
        var rejoint = false;
        var limite = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (!rejoint && DateTime.UtcNow < limite)
        {
            rejoint = await repris.ConnectAsync(launchIfMissing: false, CancellationToken.None);
            if (!rejoint) await Task.Delay(500);
        }
        Assert.True(rejoint, "Le collecteur n’a pas été relancé par la mise à jour.");
        Assert.NotEqual(enPlace.Id, repris.Agent!.Pid);
        Assert.Equal(profile.Folder, repris.Agent.DataFolder);
        Assert.NotEqual(string.Empty, profile.Heartbeat());
        Assert.Contains("pollSeconds", profile.Read("settings.json"), StringComparison.Ordinal);

        await repris.StopAgentAsync(CancellationToken.None);
        await repris.DisposeAsync();

        try
        {
            Directory.Delete(racine, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Un dossier temporaire qui survit ne fait échouer aucun test.
        }
    }

    /// <summary>Installation minimale : de quoi lancer réellement le collecteur ailleurs.</summary>
    private static void Poser(string folder, bool marqueur)
    {
        Directory.CreateDirectory(folder);
        foreach (var piece in Pieces)
        {
            var source = Path.Combine(AppContext.BaseDirectory, piece);
            if (File.Exists(source)) File.Copy(source, Path.Combine(folder, piece), overwrite: true);
        }
        File.WriteAllText(Path.Combine(folder, "SeptPaceAuto.App.exe.txt"), "place tenue");
        if (marqueur) File.WriteAllText(Path.Combine(folder, Marqueur), "1.2.0");
    }

    private sealed class NoLaunch : IAgentLauncher
    {
        public string? Executable => null;

        public bool Launch() => false;
    }
}
