using System.Drawing;
using System.Text.Json;

namespace SeptPaceAuto;

/// <summary>Emplacements de fichiers propres à la coquille de l'application.</summary>
internal static class ShellPaths
{
    /// <summary>
    /// %LOCALAPPDATA%\7pace-auto, ou le dossier imposé par SEPTPACE_DATA pour faire tourner
    /// une instance isolée à côté de l'installation courante.
    /// </summary>
    public static string DataFolder { get; } = Services.AppPaths.Root;

    /// <summary>Profil WebView2 persistant (cookies, cache, état de session).</summary>
    public static string WebViewFolder { get; } = Path.Combine(DataFolder, "webview");

    /// <summary>Tailles et positions des fenêtres.</summary>
    public static string WindowsFile { get; } = Path.Combine(DataFolder, "windows.json");

    /// <summary>Dossier des fichiers d'interface copiés à côté de l'exécutable.</summary>
    public static string WebFolder { get; } = Path.Combine(AppContext.BaseDirectory, "web");

    public static void EnsureDataFolder() => Directory.CreateDirectory(DataFolder);
}

/// <summary>Taille, position et état d'agrandissement mémorisés d'une fenêtre.</summary>
internal sealed class WindowPlacement
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>
/// Mémorise les placements de fenêtres dans <c>%LOCALAPPDATA%\7pace-auto\windows.json</c>.
/// Un fichier illisible est traité comme absent : l'application démarre avec ses valeurs par défaut.
/// </summary>
internal static class WindowStateStore
{
    private const int MinimumVisibleWidth = 160;
    private const int MinimumVisibleHeight = 48;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static Dictionary<string, WindowPlacement>? _cache;

    /// <summary>
    /// Applique le placement mémorisé pour <paramref name="key"/>.
    /// Renvoie <c>false</c> quand rien d'utilisable n'existe : la fenêtre choisit alors sa position par défaut.
    /// </summary>
    public static bool Restore(Form form, string key, bool restoreSize, bool restoreMaximized)
    {
        if (!Load().TryGetValue(key, out var saved))
        {
            return false;
        }

        var size = form.Size;
        if (restoreSize && saved.Width > 0 && saved.Height > 0)
        {
            size = new Size(
                Math.Max(saved.Width, Math.Max(form.MinimumSize.Width, MinimumVisibleWidth)),
                Math.Max(saved.Height, Math.Max(form.MinimumSize.Height, MinimumVisibleHeight)));
        }

        var bounds = new Rectangle(new Point(saved.X, saved.Y), size);
        if (!IsReachable(bounds))
        {
            return false;
        }

        form.StartPosition = FormStartPosition.Manual;
        form.Bounds = Clamp(bounds);
        if (restoreMaximized && saved.Maximized)
        {
            form.WindowState = FormWindowState.Maximized;
        }

        return true;
    }

    /// <summary>Enregistre le placement courant de la fenêtre.</summary>
    public static void Save(Form form, string key)
    {
        if (form.WindowState == FormWindowState.Minimized)
        {
            return;
        }

        var maximized = form.WindowState == FormWindowState.Maximized;
        var bounds = maximized ? form.RestoreBounds : form.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var store = Load();
        store[key] = new WindowPlacement
        {
            X = bounds.X,
            Y = bounds.Y,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = maximized,
        };

        try
        {
            ShellPaths.EnsureDataFolder();
            File.WriteAllText(ShellPaths.WindowsFile, JsonSerializer.Serialize(store, Options));
        }
        catch (Exception)
        {
            // Mémoriser la fenêtre n'est pas critique : un échec d'écriture ne doit jamais interrompre l'application.
        }
    }

    /// <summary>Ramène entièrement un rectangle dans la zone de travail de l'écran qui le contient le mieux.</summary>
    public static Rectangle Clamp(Rectangle bounds)
    {
        var area = Screen.FromRectangle(bounds).WorkingArea;
        var width = Math.Min(bounds.Width, area.Width);
        var height = Math.Min(bounds.Height, area.Height);
        var x = Math.Min(Math.Max(bounds.X, area.Left), area.Right - width);
        var y = Math.Min(Math.Max(bounds.Y, area.Top), area.Bottom - height);
        return new Rectangle(x, y, width, height);
    }

    private static bool IsReachable(Rectangle bounds)
    {
        foreach (var screen in Screen.AllScreens)
        {
            var overlap = Rectangle.Intersect(screen.WorkingArea, bounds);
            if (overlap.Width >= MinimumVisibleWidth && overlap.Height >= MinimumVisibleHeight)
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<string, WindowPlacement> Load()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        try
        {
            if (File.Exists(ShellPaths.WindowsFile))
            {
                _cache = JsonSerializer.Deserialize<Dictionary<string, WindowPlacement>>(
                    File.ReadAllText(ShellPaths.WindowsFile));
            }
        }
        catch (Exception)
        {
            _cache = null;
        }

        return _cache ??= new Dictionary<string, WindowPlacement>(StringComparer.Ordinal);
    }
}
