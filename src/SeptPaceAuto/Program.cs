using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using SeptPaceAuto.Services;

namespace SeptPaceAuto;

internal static class Program
{
    /// <summary>
    /// Exclusion et canal d'activation propres au profil de données : deux profils distincts
    /// coexistent, deux exécutions du même profil se rejoignent.
    /// </summary>
    private static readonly string InstanceKey = Key(ShellPaths.DataFolder);

    private static string InstanceMutexName => $@"Local\SeptPaceAuto.instance.{InstanceKey}";

    /// <summary>Canal d'activation : une seconde exécution réveille la fenêtre existante.</summary>
    internal static string ActivationPipeName => $"SeptPaceAuto.activate.{InstanceKey}";

    private static string Key(string folder)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(folder.ToLowerInvariant()));
        return Convert.ToHexString(bytes, 0, 8);
    }

    [STAThread]
    private static int Main()
    {
        using var instance = new Mutex(initiallyOwned: true, InstanceMutexName, out var owned);
        if (!owned)
        {
            ActivateRunningInstance();
            return 0;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            ShellPaths.EnsureDataFolder();
        }
        catch (Exception ex)
        {
            Warn($"Dossier de données inaccessible : {ex.Message}");
            return 1;
        }

        ITrackingApp app;
        try
        {
            app = TrackingAppFactory.Create();
        }
        catch (Exception ex)
        {
            Warn($"Démarrage impossible : {ex.Message}");
            return 1;
        }

        var shell = new ShellController(app);
        var main = shell.MainWindow;
        shell.ListenForActivation();
        Application.Run(main);
        return 0;
    }

    private static void ActivateRunningInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", ActivationPipeName, PipeDirection.Out);
            client.Connect(1500);
            client.Write(Encoding.UTF8.GetBytes("show"));
            client.Flush();
        }
        catch (Exception)
        {
            Warn("7pace auto est déjà en cours d'exécution.");
        }
    }

    internal static void Warn(string message)
        => MessageBox.Show(message, "7pace auto", MessageBoxButtons.OK, MessageBoxIcon.Information);
}

/// <summary>
/// Coquille de l'application : une seule fenêtre visible à la fois, bascule
/// complète/mini, et arrêt propre du service avant de quitter.
/// </summary>
internal sealed partial class ShellController
{
    /// <summary>GW_ENABLEDPOPUP : la boîte modale encore ouverte que possède une fenêtre.</summary>
    private const uint EnabledPopup = 6;

    private readonly ITrackingApp _app;
    private readonly CancellationTokenSource _cts = new();

    private MainForm? _main;
    private MiniForm? _mini;
    private Form? _folderPickerOwner;
    private bool _pickingFolder;
    private bool _started;
    private bool _interfaceLost;

    public ShellController(ITrackingApp app) => _app = app;

    /// <summary>Arrêt en cours : les fenêtres peuvent se fermer sans redemander la sortie.</summary>
    public bool IsExiting { get; private set; }

    public MainForm MainWindow
    {
        get
        {
            if (_main is null)
            {
                _main = new MainForm(_app, this);
                _main.Shown += OnMainShown;
            }

            return _main;
        }
    }

    /// <summary>Traite <c>showMini</c> / <c>showMain</c> avant que le service n'en soit informé.</summary>
    public void HandleWindowCommand(string method)
    {
        if (IsExiting)
        {
            return;
        }

        switch (method)
        {
            case "showMini":
                ShowMini();
                break;
            case "showMain":
                ShowMain();
                break;
            case "exitForUpdate":
                RequestExit();
                break;
        }
    }

    /// <summary>
    /// Sélection native du dépôt Git à surveiller (<c>chooseFolder</c>). Renvoie le dossier
    /// choisi, ou <c>null</c> à l'annulation. Une seule boîte à la fois : pendant qu'une
    /// sélection est en cours, une seconde demande ramène la boîte ouverte au premier plan
    /// et répond « rien de choisi ».
    /// </summary>
    public string? ChooseRepositoryFolder(string? current)
    {
        if (IsExiting)
        {
            return null;
        }

        if (_pickingFolder)
        {
            FocusFolderPicker();
            return null;
        }

        // La boîte appartient à la fenêtre affichée : elle reste centrée sur elle et, quand seule
        // la mini est visible, ne fait pas resurgir la fenêtre complète derrière le sélecteur.
        var owner = VisibleWindow();
        _pickingFolder = true;
        _folderPickerOwner = owner;
        try
        {
            using var dialog = new FolderBrowserDialog
            {
                UseDescriptionForTitle = true,
                Description = "Choisis le dépôt Git à surveiller",
                ShowNewFolderButton = false,
            };

            if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
            {
                dialog.SelectedPath = current;
            }

            var answer = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
            return answer == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath)
                ? dialog.SelectedPath
                : null;
        }
        finally
        {
            _pickingFolder = false;
            _folderPickerOwner = null;
        }
    }

    /// <summary>Fenêtre actuellement affichée : propriétaire des boîtes natives.</summary>
    private Form? VisibleWindow()
    {
        if (_main is { IsDisposed: false, IsHandleCreated: true, Visible: true })
        {
            return _main;
        }

        return _mini is { IsDisposed: false, IsHandleCreated: true, Visible: true } ? _mini : null;
    }

    /// <summary>Ramène au premier plan la boîte déjà ouverte plutôt que d'en empiler une seconde.</summary>
    private void FocusFolderPicker()
    {
        var owner = _folderPickerOwner;
        if (owner is null || owner.IsDisposed || !owner.IsHandleCreated)
        {
            return;
        }

        try
        {
            var dialog = GetWindow(owner.Handle, EnabledPopup);
            if (dialog != IntPtr.Zero)
            {
                SetForegroundWindow(dialog);
            }
        }
        catch (Exception)
        {
            // Boîte refermée entre-temps : il n'y a plus rien à ramener devant.
        }
    }

    public void ShowMini()
    {
        var mini = _mini ??= new MiniForm(_app, this);
        if (!mini.Visible)
        {
            mini.Show();
        }

        mini.Activate();
        _ = LoadMiniAsync(mini);
        _main?.Hide();
    }

    public void ShowMain()
    {
        var main = MainWindow;
        if (!main.Visible)
        {
            main.Show();
        }

        if (main.WindowState == FormWindowState.Minimized)
        {
            main.WindowState = FormWindowState.Normal;
        }

        main.Activate();
        main.BringToFront();
        _mini?.Hide();
    }

    /// <summary>Écoute les demandes d'activation envoyées par une seconde exécution.</summary>
    public void ListenForActivation() => _ = AcceptActivationsAsync();

    /// <summary>Ferme l'application après l'arrêt du service.</summary>
    public void RequestExit()
    {
        if (IsExiting)
        {
            return;
        }

        IsExiting = true;
        _ = ShutdownAsync();
    }

    /// <summary>Le rendu de l'interface a été perdu : on le dit, puis on quitte proprement.</summary>
    public void ReportInterfaceLost()
    {
        if (IsExiting || _interfaceLost)
        {
            return;
        }

        _interfaceLost = true;
        Program.Warn("L'affichage s'est interrompu. Relancez 7pace auto ; aucun temps n'a été envoyé.");
        RequestExit();
    }

    private async void OnMainShown(object? sender, EventArgs e)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        try
        {
            await _app.StartAsync(_cts.Token);
            await MainWindow.InitializeWebAsync();
        }
        catch (OperationCanceledException)
        {
            // Fermeture demandée pendant le démarrage.
        }
        catch (Exception ex)
        {
            Program.Warn($"Démarrage impossible : {ex.Message}");
            RequestExit();
        }
    }

    private async Task LoadMiniAsync(MiniForm mini)
    {
        try
        {
            await mini.InitializeWebAsync();
        }
        catch (Exception ex)
        {
            Program.Warn($"Vue mini indisponible : {ex.Message}");
            ShowMain();
        }
    }

    private async Task AcceptActivationsAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    Program.ActivationPipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(_cts.Token);
                var buffer = new byte[16];
                await server.ReadAsync(buffer, _cts.Token);
                RequestActivation();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // Canal indisponible : nouvelle tentative silencieuse, l'application reste utilisable.
                try
                {
                    await Task.Delay(500, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private void RequestActivation()
    {
        var main = _main;
        if (IsExiting || main is null || !main.IsHandleCreated)
        {
            return;
        }

        try
        {
            main.BeginInvoke(new Action(ShowMain));
        }
        catch (Exception)
        {
            // Fenêtre en train de disparaître.
        }
    }

    private async Task ShutdownAsync()
    {
        _cts.Cancel();
        _mini?.Hide();
        _main?.Hide();

        try
        {
            // Un service bloqué ne doit pas empêcher l'application de se fermer.
            await Task.WhenAny(_app.DisposeAsync().AsTask(), Task.Delay(5000));
        }
        catch (Exception)
        {
            // Arrêt en cours : plus rien à signaler.
        }

        Application.Exit();
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetWindow(IntPtr hwnd, uint command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hwnd);
}
