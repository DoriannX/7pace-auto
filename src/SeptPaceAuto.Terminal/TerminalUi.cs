using System.Globalization;
using System.Text;
using System.Text.Json;
using SeptPaceAuto.Services;

namespace SeptPaceAuto.Terminal;

/// <summary>Journée telle que le cœur la rend : créneaux, trous, chevauchements et totaux.</summary>
internal sealed record DayView(
    string Date,
    List<Entry> Entries,
    int[][] Holes,
    int[][] Overlaps,
    int TotalMinutes,
    int PlannedMinutes,
    int Unassigned,
    int Locked,
    int Pending,
    string? AzureMessage);

/// <summary>
/// Unique interface : la journée terminée la plus ancienne, corrigée puis envoyée. Aucune
/// sélection de date, aucun historique, aucune relecture de 7pace.
/// </summary>
internal sealed class TerminalUi
{
    private static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ITrackingApp _app;
    private readonly CancellationToken _ct;

    private DayView? _day;
    private bool _quickRunning;
    private string _trackingLabel = string.Empty;
    private string _trackingState = string.Empty;

    public TerminalUi(ITrackingApp app, CancellationToken ct)
    {
        _app = app;
        _ct = ct;
    }

    public async Task<int> RunAsync()
    {
        Console.WriteLine("7pace auto — terminal");
        Console.WriteLine($"Données : {TrackingAppFactory.DataFolder}");

        await ReadTrackingAsync();
        await LoadDayAsync();

        while (!_ct.IsCancellationRequested)
        {
            Console.WriteLine();
            PrintDay();
            PrintTracking();
            PrintMenu();

            var choice = Read("Choix");
            if (choice is null or "0") return 0;

            Console.WriteLine();
            try
            {
                switch (choice)
                {
                    case "1":
                        await SaveEntryAsync();
                        break;
                    case "2":
                        await DeleteEntryAsync();
                        break;
                    case "3":
                        await SubmitAsync();
                        break;
                    case "4":
                        await DiscardAsync();
                        break;
                    case "5":
                        await ToggleQuickAsync();
                        break;
                    case "6":
                        await ShowCurrentDayAsync();
                        break;
                    case "7":
                        await ConfigureAsync();
                        break;
                    case "8":
                        await SaveTokenAsync();
                        break;
                    case "9":
                        if (await UpdateAsync()) return 0;
                        break;
                    default:
                        Console.WriteLine("Choix inconnu.");
                        break;
                }
            }
            catch (DomainException error)
            {
                Console.WriteLine($"Erreur : {error.Message}");
            }
            catch (JsonException)
            {
                Console.WriteLine("Erreur : le cœur a renvoyé une réponse illisible.");
            }
        }

        return 0;
    }

    private static void PrintMenu()
    {
        Console.WriteLine();
        Console.WriteLine("1. Ajouter ou corriger un créneau");
        Console.WriteLine("2. Supprimer un créneau");
        Console.WriteLine("3. Envoyer la journée dans 7pace");
        Console.WriteLine("4. Ignorer cette journée");
        Console.WriteLine("5. Démarrer ou arrêter le chrono rapide");
        Console.WriteLine("6. Voir la journée en cours (lecture seule)");
        Console.WriteLine("7. Configurer l’application");
        Console.WriteLine("8. Enregistrer ou supprimer le jeton 7pace");
        Console.WriteLine("9. Rechercher et installer une mise à jour");
        Console.WriteLine("0. Quitter");
    }

    /// <summary>Charge la journée à traiter. Azure y est relancé : c'est le moment utile.</summary>
    private async Task LoadDayAsync()
    {
        using var result = await CallAsync("pendingDay", new { });
        _day = Parse(result.RootElement);
        if (_day?.AzureMessage is { Length: > 0 } message) Console.WriteLine(message);
    }

    private static DayView? Parse(JsonElement root)
    {
        if (!root.TryGetProperty("date", out var date) || date.ValueKind != JsonValueKind.String) return null;

        var azure = root.TryGetProperty("azure", out var node) && node.ValueKind == JsonValueKind.Object
            ? Text(node, "message")
            : null;

        return new DayView(
            date.GetString()!,
            root.GetProperty("entries").Deserialize<List<Entry>>(Wire) ?? new List<Entry>(),
            Spans(root, "holes"),
            Spans(root, "overlaps"),
            Number(root, "totalMinutes"),
            Number(root, "plannedMinutes"),
            Number(root, "unassigned"),
            Number(root, "locked"),
            Number(root, "pending"),
            azure);
    }

    private static int[][] Spans(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) return Array.Empty<int[]>();
        return value.Deserialize<int[][]>(Wire) ?? Array.Empty<int[]>();
    }

    private void PrintDay()
    {
        if (_day is null)
        {
            Console.WriteLine("Aucune journée à envoyer : tout est à jour.");
            return;
        }

        var day = _day;
        var readable = TimeRules.FrenchDate(TimeRules.ParseDate(day.Date));
        var queue = day.Pending > 1 ? $" · {day.Pending} journées en attente" : string.Empty;
        Console.WriteLine($"Journée à envoyer : {readable} ({day.Date}){queue}");

        if (day.Entries.Count == 0)
        {
            Console.WriteLine("  Aucun créneau : ajoute-en un, ou ignore la journée.");
            return;
        }

        foreach (var entry in day.Entries.OrderBy(item => item.StartMinutes))
        {
            var workItem = entry.WorkItem is int item
                ? $"#{item.ToString(CultureInfo.InvariantCulture)}"
                : "à attribuer";
            var locked = entry.SentAt is not null ? " · envoyé, verrouillé" : string.Empty;
            var label = entry.Label.Length > 0 ? $" · {entry.Label}" : string.Empty;
            Console.WriteLine($"  [{entry.Id}] {entry.Start}–{entry.End} · {workItem}{label}{locked}");
        }

        if (day.Holes.Length > 0) Console.WriteLine($"  Trous : {Spans(day.Holes)}");
        if (day.Overlaps.Length > 0) Console.WriteLine($"  Chevauchements : {Spans(day.Overlaps)}");
        if (day.Unassigned > 0) Console.WriteLine($"  À attribuer : {day.Unassigned} créneau(x) — l’envoi est bloqué.");
        Console.WriteLine($"  Total : {TimeRules.Readable(day.TotalMinutes)} sur {TimeRules.Readable(day.PlannedMinutes)} prévues");
    }

    private static string Spans(int[][] spans) => string.Join(
        ", ",
        spans.Where(span => span.Length == 2).Select(span => $"{TimeRules.AsTime(span[0])}–{TimeRules.AsTime(span[1])}"));

    private void PrintTracking()
    {
        Console.WriteLine($"Suivi d’aujourd’hui : {TrackingLabel(_trackingState)} · {_trackingLabel}");
        Console.WriteLine($"Chrono rapide : {(_quickRunning ? "en cours" : "arrêté")}");
        Console.WriteLine("Détail de la journée en cours : choix 6 (lecture seule).");
    }

    /// <summary>
    /// Ouvre la consultation de la journée en cours. Diagnostic pur : l'écran montre ce qui
    /// a déjà été collecté aujourd'hui et la santé du suivi, sans jamais rien corriger ni
    /// envoyer. La touche qui ferme l'écran y est consommée, pour ne pas devenir un choix
    /// de menu involontaire.
    /// </summary>
    private async Task ShowCurrentDayAsync()
    {
        var screen = new CurrentDayScreen(LoadCurrentDayAsync, new ConsoleSurface());
        await screen.RunAsync(_ct);

        // Le suivi affiché sous le menu profite de la lecture qui vient d'être faite.
        if (screen.Last is { } view)
        {
            _trackingState = view.Tracking.State;
            _trackingLabel = view.Tracking.Label;
            _quickRunning = view.Tracking.QuickRunning;
        }
    }

    private async Task<CurrentDayView> LoadCurrentDayAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var result = await CallAsync("currentDay", new { });
        return CurrentDayScreen.Parse(result.RootElement, Wire);
    }

    private async Task ReadTrackingAsync()
    {
        using var result = await CallAsync("bootstrap", new { });
        Remember(result.RootElement.GetProperty("tracking"));

        if (!result.RootElement.GetProperty("configured").GetBoolean())
        {
            Console.WriteLine("Configuration : dépôt Git à renseigner (choix 7)");
        }
        if (result.RootElement.TryGetProperty("connections", out var connections))
        {
            Console.WriteLine(Text(connections.GetProperty("sevenpace"), "label"));
        }
    }

    private void Remember(JsonElement tracking)
    {
        _quickRunning = tracking.TryGetProperty("quickRunning", out var quick) && quick.ValueKind == JsonValueKind.True;
        _trackingLabel = Text(tracking, "label");
        _trackingState = Text(tracking, "state");
    }

    private async Task SaveEntryAsync()
    {
        if (_day is null)
        {
            Console.WriteLine("Rien à corriger : aucune journée en attente.");
            return;
        }

        var id = ReadOptionalPositive("Identifiant à corriger (vide pour ajouter)", null);
        var existing = id is null ? null : _day.Entries.FirstOrDefault(entry => entry.Id == id);
        if (id is not null && existing is null)
        {
            Console.WriteLine("Ce créneau n’existe pas dans cette journée.");
            return;
        }
        if (existing?.SentAt is not null)
        {
            Console.WriteLine("Ce créneau est déjà envoyé : corrige-le directement dans 7pace.");
            return;
        }

        var start = ReadRequired("Début (HH:MM)", existing?.Start ?? "08:30");
        var end = ReadRequired("Fin (HH:MM)", existing?.End ?? "09:00");
        var workItem = ReadOptionalPositive("Fix, Task ou tâche générique (vide = à attribuer)", existing?.WorkItem, zeroClears: true);

        using var result = await CallAsync("saveEntry", new
        {
            date = _day.Date,
            entry = new Entry
            {
                Id = existing?.Id,
                Start = start,
                End = end,
                WorkItem = workItem,
                // Le libellé vient d'Azure : il ne survit pas à une attribution faite à la main,
                // sinon l'écran annonce encore « attribution à compléter » sur un créneau attribué.
                Label = existing is not null && existing.WorkItem == workItem ? existing.Label : string.Empty,
            },
        });

        Apply(result.RootElement);
        Console.WriteLine(existing is null ? "Créneau ajouté." : "Créneau corrigé.");
    }

    private async Task DeleteEntryAsync()
    {
        if (_day is null || _day.Entries.Count == 0)
        {
            Console.WriteLine("Rien à supprimer.");
            return;
        }

        var id = ReadOptionalPositive("Identifiant à supprimer", null);
        if (id is null) return;
        if (_day.Entries.All(entry => entry.Id != id))
        {
            Console.WriteLine("Ce créneau n’existe pas dans cette journée.");
            return;
        }

        if (!Confirm("Tape SUPPRIMER pour confirmer", "SUPPRIMER"))
        {
            Console.WriteLine("Suppression annulée.");
            return;
        }

        using var result = await CallAsync("deleteEntry", new { date = _day.Date, id });
        Apply(result.RootElement);
        Console.WriteLine("Créneau supprimé.");
    }

    /// <summary>Remplace la journée affichée par celle que le cœur vient de rendre.</summary>
    private void Apply(JsonElement root)
    {
        var updated = Parse(root);
        if (updated is null || _day is null) return;
        _day = updated with { Pending = _day.Pending, AzureMessage = null };
    }

    private async Task ToggleQuickAsync()
    {
        using var result = await CallAsync("setQuick", new { running = !_quickRunning });
        Remember(result.RootElement.GetProperty("tracking"));
        Console.WriteLine(_quickRunning
            ? "Chrono rapide démarré : le créneau est « à attribuer », tu le compléteras demain."
            : "Chrono rapide arrêté.");
    }

    private async Task SubmitAsync()
    {
        if (_day is null)
        {
            Console.WriteLine("Rien à envoyer.");
            return;
        }

        PrintDay();
        if (_day.Entries.Count == 0) return;
        if (_quickRunning)
        {
            Console.WriteLine("Le chrono rapide tourne encore : arrête-le (choix 5) avant d’envoyer.");
            return;
        }
        if (_day.Unassigned > 0)
        {
            Console.WriteLine("Attribue ou supprime les créneaux « à attribuer » avant d’envoyer.");
            return;
        }

        Console.WriteLine("Aucun appel 7pace ne part avant la confirmation suivante.");
        if (!Confirm("Tape ENVOYER pour confirmer", "ENVOYER"))
        {
            Console.WriteLine("Envoi annulé.");
            return;
        }

        using var result = await CallAsync("submitDay", new { date = _day.Date });
        var root = result.RootElement;
        Console.WriteLine(Text(root, "message"));

        if (root.TryGetProperty("closed", out var closed) && closed.ValueKind == JsonValueKind.True)
        {
            Console.WriteLine("Journée close : son détail n’existe plus ici, 7pace fait désormais autorité.");
            await LoadDayAsync();
            return;
        }

        // Rien d'accepté : le message d'échec suffit, parler de verrouillage induirait en erreur.
        var accepted = root.TryGetProperty("sent", out var sent) && sent.ValueKind == JsonValueKind.Array
            ? sent.GetArrayLength()
            : 0;
        if (accepted > 0)
        {
            Console.WriteLine($"{accepted} créneau(x) verrouillés ; les refusés restent modifiables et repartiront seuls.");
        }
        await LoadDayAsync();
    }

    private async Task DiscardAsync()
    {
        if (_day is null)
        {
            Console.WriteLine("Rien à ignorer.");
            return;
        }

        Console.WriteLine("Cette journée disparaîtra de l’application sans aucun envoi dans 7pace.");
        if (!Confirm("Tape IGNORER pour confirmer", "IGNORER"))
        {
            Console.WriteLine("Abandon annulé.");
            return;
        }

        using var result = await CallAsync("discardDay", new { date = _day.Date });
        Console.WriteLine(Text(result.RootElement, "message"));
        await LoadDayAsync();
    }

    private async Task ConfigureAsync()
    {
        using var loaded = await CallAsync("loadSettings", new { });
        var settings = loaded.RootElement.GetProperty("settings").Deserialize<AppSettings>(Wire)
            ?? throw new JsonException();

        Console.WriteLine("Laisse une valeur vide pour conserver celle affichée.");
        settings.RepoPath = Read("Dépôt Git", settings.RepoPath) ?? settings.RepoPath;
        settings.PollSeconds = ReadInt("Relevé en secondes", settings.PollSeconds, 10, 300);
        settings.AzureOrganization = Read("Organisation Azure DevOps", settings.AzureOrganization) ?? settings.AzureOrganization;
        settings.SevenPaceAccount = Read("Compte 7pace", settings.SevenPaceAccount) ?? settings.SevenPaceAccount;
        settings.WorkWindows = ReadWindows(settings.WorkWindows);

        using var saved = await CallAsync("saveSettings", new { settings });
        Remember(saved.RootElement.GetProperty("tracking"));
        Console.WriteLine(saved.RootElement.GetProperty("configured").GetBoolean()
            ? "Configuration enregistrée; le suivi utilise déjà les nouvelles valeurs."
            : "Configuration enregistrée, mais le dépôt Git reste indisponible.");
    }

    private async Task SaveTokenAsync()
    {
        Console.Write("Jeton 7pace (SUPPRIMER pour l’effacer, vide pour annuler) : ");
        var token = ReadSecret();
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine("Jeton inchangé.");
            return;
        }

        var clear = string.Equals(token, "SUPPRIMER", StringComparison.Ordinal)
            ? string.Empty
            : token;
        using var result = await CallAsync("saveToken", new { token = clear });
        Console.WriteLine(Text(result.RootElement.GetProperty("connections").GetProperty("sevenpace"), "label"));
    }

    private async Task<bool> UpdateAsync()
    {
        Console.WriteLine("Recherche d’une mise à jour…");
        using var check = await CallAsync("checkUpdate", new { });
        var info = check.RootElement;
        var error = Text(info, "error");
        if (error.Length > 0)
        {
            Console.WriteLine(error);
            return false;
        }

        var current = Text(info, "current");
        var latest = Text(info, "latest");
        if (!info.GetProperty("available").GetBoolean() || latest.Length == 0)
        {
            Console.WriteLine(current.Length > 0
                ? $"Aucune mise à jour : {current} est la dernière version."
                : "Aucune mise à jour disponible.");
            return false;
        }

        Console.WriteLine(current.Length > 0
            ? $"Version {latest} disponible (installée : {current})."
            : $"Version {latest} disponible.");
        var notes = Text(info, "notes");
        if (notes.Length > 0) Console.WriteLine(notes);

        if (!ConfirmYes("Installer cette version ?"))
        {
            Console.WriteLine("Mise à jour annulée.");
            return false;
        }

        Console.WriteLine("Téléchargement de la nouvelle version…");
        using var applied = await CallAsync("applyUpdate", new { });
        var result = applied.RootElement;
        Console.WriteLine(Text(result, "message"));
        return result.TryGetProperty("ok", out var ok) && ok.GetBoolean();
    }

    private async Task<JsonDocument> CallAsync(string method, object parameters)
    {
        _ct.ThrowIfCancellationRequested();
        var payload = JsonSerializer.Serialize(parameters, Wire);
        var response = await _app.HandleAsync(method, payload, _ct);
        return JsonDocument.Parse(response);
    }

    private static List<int[]> ReadWindows(IReadOnlyList<int[]> current)
    {
        var fallback = string.Join(',', current.Select(FormatWindow));
        while (true)
        {
            var raw = Read("Horaires de travail (HH:MM-HH:MM,...)", fallback);
            if (raw is null) return current.Select(window => window.ToArray()).ToList();
            try
            {
                return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(ParseWindow)
                    .ToList();
            }
            catch (DomainException error)
            {
                Console.WriteLine(error.Message);
            }
        }
    }

    private static int[] ParseWindow(string raw)
    {
        var parts = raw.Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length != 2) throw new DomainException("Attendu : HH:MM-HH:MM.");
        var start = TimeRules.Minutes(parts[0]);
        var end = TimeRules.Minutes(parts[1]);
        if (end <= start) throw new DomainException("La fin doit être après le début.");
        return new[] { start, end };
    }

    private static string FormatWindow(int[] window) =>
        window.Length == 2 ? $"{TimeRules.AsTime(window[0])}-{TimeRules.AsTime(window[1])}" : string.Empty;

    private static int ReadInt(string label, int current, int minimum, int maximum)
    {
        while (true)
        {
            var raw = ReadRequired(label, current.ToString(CultureInfo.InvariantCulture));
            if (int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                && value >= minimum && value <= maximum)
            {
                return value;
            }
            Console.WriteLine($"Indique un entier entre {minimum} et {maximum}.");
        }
    }

    private static int? ReadOptionalPositive(string label, int? current, bool zeroClears = false)
    {
        while (true)
        {
            var fallback = current?.ToString(CultureInfo.InvariantCulture);
            var raw = Read(label, fallback);
            if (raw is null) return current;
            if (raw.Length == 0) return null;
            if (zeroClears && raw == "0") return null;
            if (int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0) return value;
            Console.WriteLine(zeroClears ? "Indique un entier positif, ou 0 pour laisser à attribuer." : "Indique un entier positif.");
        }
    }

    private static string ReadRequired(string label, string fallback)
    {
        while (true)
        {
            var value = Read(label, fallback);
            if (!string.IsNullOrWhiteSpace(value)) return value;
            Console.WriteLine("Cette valeur est obligatoire.");
        }
    }

    private static string? Read(string label, string? fallback = null)
    {
        Console.Write(fallback is null || fallback.Length == 0 ? $"{label} : " : $"{label} [{fallback}] : ");
        var raw = Console.ReadLine();
        if (raw is null) return null;
        var value = raw.Trim();
        return value.Length == 0 ? fallback ?? string.Empty : value;
    }

    private static string? ReadSecret()
    {
        if (Console.IsInputRedirected) return Console.ReadLine()?.Trim();

        var value = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return value.ToString().Trim();
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0) value.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
        }
    }

    private static bool ConfirmYes(string prompt)
    {
        Console.Write($"{prompt} (o/N) : ");
        var answer = Console.ReadLine()?.Trim();
        return string.Equals(answer, "o", StringComparison.OrdinalIgnoreCase)
            || string.Equals(answer, "oui", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Confirm(string prompt, string expected)
    {
        Console.Write($"{prompt} : ");
        return string.Equals(Console.ReadLine()?.Trim(), expected, StringComparison.Ordinal);
    }

    private static int Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string TrackingLabel(string state) => TrackingLabels.For(state);
}
