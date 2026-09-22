#nullable enable
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace SeptPaceAuto.Services;

/// <summary>
/// Notification Windows du matin. Une seule par lancement : l'application signale qu'une
/// journée terminée attend, puis se taît. Elle n'a rien à répéter — la journée reste en tête
/// de file au prochain démarrage.
///
/// Passe par PowerShell plutôt que par une dépendance d'interface : le terminal reste une
/// application console, sans boucle de messages ni paquet supplémentaire.
/// </summary>
public static class Notifier
{
    private const int VisibleSeconds = 12;

    /// <param name="openOnClick">
    /// Programme ouvert quand l'utilisateur clique la notification. Le collecteur y met le
    /// terminal : la notification devient le chemin le plus court vers la journée en attente.
    /// </param>
    public static void Show(string title, string message, string? openOnClick = null)
    {
        var shell = PowerShell();
        if (shell is null) return;

        // Le clic n'est reçu que si la boucle de messages tourne : d'où le pompage explicite
        // pendant toute la durée d'affichage, puis la libération de l'icône.
        var script = string.Concat(
            "Add-Type -AssemblyName System.Windows.Forms; ",
            "Add-Type -AssemblyName System.Drawing; ",
            "$cible = ", Quote(openOnClick ?? string.Empty), "; ",
            "$icone = New-Object System.Windows.Forms.NotifyIcon; ",
            "$icone.Icon = [System.Drawing.SystemIcons]::Information; ",
            "$icone.Visible = $true; ",
            "if ($cible -and (Test-Path -LiteralPath $cible)) { $icone.add_BalloonTipClicked({ try { Start-Process -FilePath $cible } catch { } }) }; ",
            "$icone.ShowBalloonTip(", (VisibleSeconds * 1000).ToString(), ", ", Quote(title), ", ", Quote(message), ", 'Info'); ",
            "$fin = (Get-Date).AddSeconds(", VisibleSeconds.ToString(), "); ",
            "while ((Get-Date) -lt $fin) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 100 }; ",
            "$icone.Dispose()");

        var start = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-STA");
        start.ArgumentList.Add("-WindowStyle");
        start.ArgumentList.Add("Hidden");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(script);

        try
        {
            using var process = Process.Start(start);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            // Une notification manquée ne doit jamais empêcher l'application de démarrer.
        }
    }

    /// <summary>Chaîne PowerShell littérale : le guillemet simple s'échappe en le doublant.</summary>
    private static string Quote(string value) => string.Concat("'", (value ?? string.Empty).Replace("'", "''"), "'");

    private static string? PowerShell()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var path = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(path) ? path : null;
    }
}
