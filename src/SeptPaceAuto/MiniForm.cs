using System.Drawing;
using SeptPaceAuto.Services;

namespace SeptPaceAuto;

/// <summary>
/// Vue mini : ticket courant, chrono, pause. Toujours au-dessus des autres fenêtres,
/// absente de la barre des tâches, déplaçable en glissant n'importe où dans la fenêtre.
/// </summary>
internal sealed class MiniForm : Form
{
    private const string StateKey = "mini";
    private const int ScreenMargin = 24;
    private const int DragThreshold = 4;

    private static readonly Size FixedSize = new(320, 150);

    private readonly WebHost _host;
    private readonly ShellController _shell;
    private readonly System.Windows.Forms.Timer _dragWatch;

    private bool _buttonWasDown;
    private bool _grabbed;
    private bool _moving;
    private Point _grabCursor;
    private Point _grabLocation;

    public MiniForm(ITrackingApp app, ShellController shell)
    {
        _shell = shell;

        Text = "7pace auto";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = WebHost.Canvas;
        ForeColor = Color.FromArgb(0xEA, 0xEA, 0xEA);
        MinimumSize = FixedSize;
        MaximumSize = FixedSize;
        Size = FixedSize;

        _host = new WebHost(app, WebHost.Canvas) { Dock = DockStyle.Fill };
        _host.WindowCommand = shell.HandleWindowCommand;
        _host.FolderChooser = shell.ChooseRepositoryFolder;
        _host.Crashed += shell.ReportInterfaceLost;
        Controls.Add(_host);

        if (!WindowStateStore.Restore(this, StateKey, restoreSize: false, restoreMaximized: false))
        {
            PlaceByDefault();
        }

        // WebView2 consomme les événements souris de la page : le glissement est surveillé
        // au niveau de la fenêtre, sans intercepter les clics destinés aux boutons de l'interface.
        _dragWatch = new System.Windows.Forms.Timer { Interval = 15 };
        _dragWatch.Tick += OnDragWatchTick;
    }

    /// <summary>Charge l'interface en vue compacte. À appeler une fois la fenêtre affichée.</summary>
    public Task InitializeWebAsync() => _host.InitializeAsync("mini");

    private void PlaceByDefault()
    {
        var area = (Screen.PrimaryScreen ?? Screen.FromPoint(Point.Empty)).WorkingArea;
        StartPosition = FormStartPosition.Manual;
        Bounds = WindowStateStore.Clamp(new Rectangle(
            area.Right - FixedSize.Width - ScreenMargin,
            area.Bottom - FixedSize.Height - ScreenMargin,
            FixedSize.Width,
            FixedSize.Height));
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        _dragWatch.Enabled = Visible;
        if (!Visible)
        {
            _buttonWasDown = false;
            _grabbed = false;
            _moving = false;
        }
    }

    private void OnDragWatchTick(object? sender, EventArgs e)
    {
        var down = (Control.MouseButtons & MouseButtons.Left) == MouseButtons.Left;

        if (!down)
        {
            if (_moving)
            {
                SnapIntoScreen();
                WindowStateStore.Save(this, StateKey);
            }

            _buttonWasDown = false;
            _grabbed = false;
            _moving = false;
            return;
        }

        var cursor = Cursor.Position;

        if (!_grabbed)
        {
            // On ne prend la fenêtre que sur un appui commencé dessus, jamais sur un glissement venu d'ailleurs.
            if (_buttonWasDown || !Bounds.Contains(cursor))
            {
                _buttonWasDown = true;
                return;
            }

            _buttonWasDown = true;
            _grabbed = true;
            _grabCursor = cursor;
            _grabLocation = Location;
            return;
        }

        if (!_moving)
        {
            // Sous le seuil, le clic appartient à l'interface (pause, ouverture de la vue complète).
            if (Math.Abs(cursor.X - _grabCursor.X) < DragThreshold
                && Math.Abs(cursor.Y - _grabCursor.Y) < DragThreshold)
            {
                return;
            }

            _moving = true;
        }

        Location = new Point(
            _grabLocation.X + (cursor.X - _grabCursor.X),
            _grabLocation.Y + (cursor.Y - _grabCursor.Y));
    }

    private void SnapIntoScreen() => Bounds = WindowStateStore.Clamp(Bounds);

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        WindowStateStore.Save(this, StateKey);

        if (!_shell.IsExiting)
        {
            e.Cancel = true;
            _shell.RequestExit();
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _dragWatch.Tick -= OnDragWatchTick;
            _dragWatch.Dispose();
        }

        base.Dispose(disposing);
    }
}
