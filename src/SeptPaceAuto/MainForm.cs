using System.Drawing;
using System.Runtime.InteropServices;
using SeptPaceAuto.Services;

namespace SeptPaceAuto;

/// <summary>Fenêtre de gestion : planning, édition de la journée et validation du soir.</summary>
internal sealed partial class MainForm : Form
{
    private const string StateKey = "main";
    private const int UseImmersiveDarkMode = 20;

    private readonly WebHost _host;
    private readonly ShellController _shell;

    public MainForm(ITrackingApp app, ShellController shell)
    {
        _shell = shell;

        Text = "7pace auto";
        BackColor = WebHost.Canvas;
        ForeColor = Color.FromArgb(0xEA, 0xEA, 0xEA);
        MinimumSize = new Size(1100, 700);
        KeyPreview = false;

        _host = new WebHost(app, WebHost.Canvas) { Dock = DockStyle.Fill };
        _host.WindowCommand = shell.HandleWindowCommand;
        _host.FolderChooser = shell.ChooseRepositoryFolder;
        _host.Crashed += shell.ReportInterfaceLost;
        Controls.Add(_host);

        if (!WindowStateStore.Restore(this, StateKey, restoreSize: true, restoreMaximized: true))
        {
            PlaceByDefault();
        }
    }

    /// <summary>Charge l'interface complète. À appeler une fois la fenêtre affichée.</summary>
    public Task InitializeWebAsync() => _host.InitializeAsync(null);

    private void PlaceByDefault()
    {
        var area = (Screen.PrimaryScreen ?? Screen.FromPoint(Point.Empty)).WorkingArea;
        var width = Math.Max(MinimumSize.Width, Math.Min(1360, area.Width));
        var height = Math.Max(MinimumSize.Height, Math.Min(860, area.Height));
        StartPosition = FormStartPosition.Manual;
        Bounds = WindowStateStore.Clamp(new Rectangle(
            area.X + ((area.Width - width) / 2),
            area.Y + ((area.Height - height) / 2),
            width,
            height));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyDarkChrome(Handle);
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        WindowStateStore.Save(this, StateKey);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        WindowStateStore.Save(this, StateKey);

        if (!_shell.IsExiting)
        {
            // Fermer la fenêtre visible quitte l'application, mais seulement après l'arrêt propre du service.
            e.Cancel = true;
            _shell.RequestExit();
            return;
        }

        base.OnFormClosing(e);
    }

    /// <summary>Barre de titre sombre (Windows 10 2004+ / Windows 11) pour éviter un bandeau clair.</summary>
    private static void ApplyDarkChrome(IntPtr handle)
    {
        var enabled = 1;
        try
        {
            DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref enabled, sizeof(int));
        }
        catch (Exception)
        {
            // Attribut absent sur les versions plus anciennes : la fenêtre reste simplement claire.
        }
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
