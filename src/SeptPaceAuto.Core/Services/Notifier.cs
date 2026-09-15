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

    public static void Show(string title, string message)
    {
        var shell = PowerShell();
        if (shell is null) return;

        var script = string.Concat(
            "Add-Type -AssemblyName System.Windows.Forms; ",
            "Add-Type -AssemblyName System.Drawing; ",
            "$icone = New-Object System.Windows.Forms.NotifyIcon; ",
            "$icone.Icon = [System.Drawing.SystemIcons]::Information; ",
            "$icone.Visible = $true; ",
            "$icone.ShowBalloonTip(", (VisibleSeconds * 1000).ToString(), ", ", Quote(title), ", ", Quote(message), ", 'Info'); ",
            "Start-Sleep -Seconds ", VisibleSeconds.ToString(), "; ",
            "$icone.Dispose()");

        var start = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-NoProfile");
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
