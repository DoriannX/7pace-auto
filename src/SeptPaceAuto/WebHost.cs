using System.Drawing;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SeptPaceAuto.Services;

namespace SeptPaceAuto;

/// <summary>
/// Hôte WebView2 réutilisable : charge l'interface validée depuis le dossier <c>web</c>
/// et fait le pont JSON-RPC entre la page et <see cref="ITrackingApp"/>.
/// </summary>
/// <remarks>
/// Messages page -> hôte : <c>{"id":1,"method":"bootstrap","params":{}}</c>.
/// Réponses hôte -> page : <c>{"id":1,"result":{...}}</c> ou <c>{"id":1,"error":"..."}</c>.
/// Notifications hôte -> page : <c>{"event":"tracking","payload":{...}}</c>.
/// </remarks>
internal sealed class WebHost : UserControl
{
    /// <summary>Nom d'hôte virtuel mappé sur le dossier <c>web</c> de la sortie de compilation.</summary>
    public const string VirtualHost = "app.7pace.local";

    /// <summary>Fond de la page validée (<c>--canvas</c>) : évite tout flash blanc au démarrage.</summary>
    public static readonly Color Canvas = Color.FromArgb(0x13, 0x13, 0x13);

    private const int PendingLimit = 64;

#if DEBUG
    private const bool DeveloperTools = true;
#else
    private const bool DeveloperTools = false;
#endif

    private static Task<CoreWebView2Environment>? _environment;

    private readonly WebView2 _view;
    private readonly ITrackingApp _app;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string> _pending = new();
    private readonly Action _flush;

    private bool _initialized;
    private bool _ready;
    private bool _closed;

    public WebHost(ITrackingApp app, Color canvas)
    {
        _app = app;
        _flush = Flush;
        BackColor = canvas;
        _view = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = canvas,
            TabStop = true,
        };
        Controls.Add(_view);
        _app.Pushed += OnPushed;
    }

    /// <summary>Demandes de bascule de fenêtre (<c>showMini</c> / <c>showMain</c>), traitées par la coquille.</summary>
    public Action<string>? WindowCommand { get; set; }

    /// <summary>
    /// Sélection native d'un dossier (<c>chooseFolder</c>), assurée par la coquille : reçoit le
    /// chemin actuel et renvoie le dossier choisi, ou <c>null</c> quand la boîte est annulée.
    /// </summary>
    public Func<string?, string?>? FolderChooser { get; set; }

    /// <summary>Le processus de rendu a disparu : l'interface de cette fenêtre est perdue.</summary>
    public event Action? Crashed;

    /// <summary>
    /// Initialise WebView2 puis navigue vers l'interface. <paramref name="view"/> vaut
    /// <c>"mini"</c> pour la vue compacte, <c>null</c> pour la vue complète.
    /// </summary>
    public async Task InitializeAsync(string? view)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        if (!Directory.Exists(ShellPaths.WebFolder))
        {
            throw new DirectoryNotFoundException(
                $"Interface introuvable : le dossier « {ShellPaths.WebFolder} » n'a pas été copié à côté de l'exécutable.");
        }

        ShellPaths.EnsureDataFolder();
        var environment = await CreateEnvironmentAsync().ConfigureAwait(true);
        await _view.EnsureCoreWebView2Async(environment).ConfigureAwait(true);

        var core = _view.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping(
            VirtualHost,
            ShellPaths.WebFolder,
            CoreWebView2HostResourceAccessKind.DenyCors);

        var settings = core.Settings;
        settings.AreDevToolsEnabled = DeveloperTools;
        settings.AreDefaultContextMenusEnabled = DeveloperTools;
        settings.AreBrowserAcceleratorKeysEnabled = DeveloperTools;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsBuiltInErrorPageEnabled = DeveloperTools;

        core.WebMessageReceived += OnWebMessageReceived;
        core.NavigationCompleted += OnNavigationCompleted;
        core.NewWindowRequested += OnNewWindowRequested;
        core.ProcessFailed += OnProcessFailed;

        var suffix = view is null ? string.Empty : "?view=" + Uri.EscapeDataString(view);
        core.Navigate($"https://{VirtualHost}/index.html{suffix}");
    }

    /// <summary>Environnement partagé : les deux fenêtres utilisent le même profil persistant.</summary>
    private static Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        if (_environment is not null && !_environment.IsFaulted && !_environment.IsCanceled)
        {
            return _environment;
        }

        // options null : WebView2 applique alors la variable d'environnement standard
        // WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS, utile pour diagnostiquer l'interface.
        return _environment = CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: ShellPaths.WebViewFolder,
            options: null);
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _ready = true;
        Flush();
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // Tout reste dans l'application : aucune fenêtre de navigateur ne doit s'ouvrir.
        e.Handled = true;
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited
            or CoreWebView2ProcessFailedKind.RenderProcessExited)
        {
            _ready = false;
            Crashed?.Invoke();
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string message;
        try
        {
            message = e.TryGetWebMessageAsString();
        }
        catch (Exception)
        {
            // Message non textuel : hors contrat, ignoré.
            return;
        }

        _ = DispatchAsync(message);
    }

    private async Task DispatchAsync(string message)
    {
        var id = 0;
        var method = "?";
        try
        {
            string parameters;
            using (var document = JsonDocument.Parse(message))
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("method", out var methodElement)
                    || methodElement.ValueKind != JsonValueKind.String)
                {
                    return;
                }

                method = methodElement.GetString() ?? "?";
                if (root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.Number)
                {
                    idElement.TryGetInt32(out id);
                }

                parameters = root.TryGetProperty("params", out var paramsElement)
                    && paramsElement.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                        ? paramsElement.GetRawText()
                        : "{}";
            }

            if (method == "chooseFolder")
            {
                // Boîte native : elle appartient à la coquille, le service n'en entend jamais parler.
                QueueFolderPicker(id, CurrentFolder(parameters));
                return;
            }

            if (method is "showMini" or "showMain")
            {
                // La coquille bascule la fenêtre avant de prévenir le service.
                WindowCommand?.Invoke(method);
                Reply(id, await SafeHandleAsync(method, parameters).ConfigureAwait(true));
                return;
            }

            var result = await _app.HandleAsync(method, parameters, _cts.Token).ConfigureAwait(true);
            if (!IsJson(result))
            {
                ReplyError(id, $"Réponse illisible du service pour « {method} ».");
                return;
            }

            Reply(id, result);

            // Mise à jour installée : la coquille ferme l'application, le script prend le relais.
            if (method == "applyUpdate" && JsonDocument.Parse(result).RootElement
                    .TryGetProperty("ok", out var applied) && applied.ValueKind == JsonValueKind.True)
            {
                WindowCommand?.Invoke("exitForUpdate");
            }
        }
        catch (OperationCanceledException)
        {
            // Fermeture en cours : plus rien à répondre.
        }
        catch (JsonException)
        {
            ReplyError(id, "Message d'interface illisible.");
        }
        catch (Exception ex)
        {
            // Les services lèvent des exceptions dont le message est déjà la phrase française à afficher.
            ReplyError(id, Readable(ex, method));
        }
    }

    /// <summary>
    /// Bascule de fenêtre : elle a déjà eu lieu, un service qui ne connaît pas la méthode
    /// ne doit pas transformer la bascule en erreur visible.
    /// </summary>
    private async Task<string> SafeHandleAsync(string method, string parameters)
    {
        try
        {
            var result = await _app.HandleAsync(method, parameters, _cts.Token).ConfigureAwait(true);
            return IsJson(result) ? result : "{}";
        }
        catch (Exception)
        {
            return "{}";
        }
    }

    /// <summary>Dossier actuellement configuré, proposé comme point de départ à la boîte native.</summary>
    private static string? CurrentFolder(string parameters)
    {
        try
        {
            using var document = JsonDocument.Parse(parameters);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("current", out var current)
                && current.ValueKind == JsonValueKind.String
                    ? current.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Programme l'ouverture du sélecteur pour après le retour du gestionnaire WebView2 : la boîte
    /// native fait tourner sa propre boucle de messages, l'ouvrir depuis l'événement figerait
    /// l'interface au lieu de laisser les autres appels être servis pendant ce temps.
    /// </summary>
    private void QueueFolderPicker(int id, string? current)
    {
        if (_closed)
        {
            return;
        }

        try
        {
            BeginInvoke(new Action(() => RunFolderPicker(id, current)));
        }
        catch (Exception)
        {
            ReplyError(id, "Le sélecteur de dossier n'est pas disponible pour le moment.");
        }
    }

    private void RunFolderPicker(int id, string? current)
    {
        if (_closed)
        {
            return;
        }

        string? path;
        try
        {
            path = FolderChooser?.Invoke(current);
        }
        catch (Exception ex)
        {
            ReplyError(id, Readable(ex, "chooseFolder"));
            return;
        }

        Reply(id, path is null
            ? "{\"path\":null}"
            : $"{{\"path\":{JsonSerializer.Serialize(path)}}}");
    }

    private void OnPushed(string name, string payload)
    {
        var body = IsJson(payload) ? payload : JsonSerializer.Serialize(payload ?? string.Empty);
        Post($"{{\"event\":{JsonSerializer.Serialize(name)},\"payload\":{body}}}");
    }

    private void Reply(int id, string result) => Post($"{{\"id\":{id},\"result\":{result}}}");

    private void ReplyError(int id, string message)
        => Post($"{{\"id\":{id},\"error\":{JsonSerializer.Serialize(message)}}}");

    private void Post(string message)
    {
        if (_closed)
        {
            return;
        }

        if (!IsHandleCreated || !_ready)
        {
            Enqueue(message);
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action<string>(Send), message);
            }
            catch (Exception)
            {
                // Fenêtre en train de disparaître.
            }

            return;
        }

        Send(message);
    }

    private void Send(string message)
    {
        if (_closed)
        {
            return;
        }

        var core = _view.CoreWebView2;
        if (core is null)
        {
            Enqueue(message);
            return;
        }

        try
        {
            core.PostWebMessageAsString(message);
        }
        catch (Exception)
        {
            // La page peut avoir été déchargée entre-temps.
        }
    }

    private void Enqueue(string message)
    {
        lock (_pending)
        {
            if (_pending.Count >= PendingLimit)
            {
                _pending.RemoveAt(0);
            }

            _pending.Add(message);
        }

        if (!_ready || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(_flush);
        }
        catch (Exception)
        {
            // Fenêtre en train de disparaître.
        }
    }

    private void Flush()
    {
        string[] messages;
        lock (_pending)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            messages = _pending.ToArray();
            _pending.Clear();
        }

        foreach (var message in messages)
        {
            Send(message);
        }
    }

    private static bool IsJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Readable(Exception ex, string method)
        => string.IsNullOrWhiteSpace(ex.Message)
            ? $"Action « {method} » impossible."
            : ex.Message;

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_closed)
        {
            _closed = true;
            _app.Pushed -= OnPushed;
            _cts.Cancel();

            var core = _view.CoreWebView2;
            if (core is not null)
            {
                core.WebMessageReceived -= OnWebMessageReceived;
                core.NavigationCompleted -= OnNavigationCompleted;
                core.NewWindowRequested -= OnNewWindowRequested;
                core.ProcessFailed -= OnProcessFailed;
            }

            _view.Dispose();
            _cts.Dispose();
        }

        base.Dispose(disposing);
    }
}
