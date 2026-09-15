using System.Globalization;
using System.Text;
using System.Text.Json;
using SeptPaceAuto.Services;

namespace SeptPaceAuto.Terminal;

internal sealed class TerminalUi
{
    private static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ITrackingApp _app;
    private readonly CancellationToken _ct;
    private bool _paused;

    public TerminalUi(ITrackingApp app, CancellationToken ct)
    {
        _app = app;
        _ct = ct;
    }

    public async Task<int> RunAsync()
    {
        Console.WriteLine("7pace auto — MVP terminal");
        Console.WriteLine($"Données : {TrackingAppFactory.DataFolder}");

        while (!_ct.IsCancellationRequested)
        {
            Console.WriteLine();
            await PrintStatusAsync();
            PrintMenu();

            var choice = Read("Choix");
            if (choice is null or "0") return 0;

            Console.WriteLine();
            try
            {
                switch (choice)
                {
                    case "1":
                        await PrintStatusAsync();
                        break;
                    case "2":
                        await ShowDayAsync();
                        break;
                    case "3":
                        await SaveEntryAsync();
                        break;
                    case "4":
                        await DeleteEntryAsync();
                        break;
                    case "5":
                        await TogglePauseAsync();
                        break;
                    case "6":
                        await SubmitDayAsync();
                        break;
                    case "7":
                        await SynchronizeAsync();
                        break;
                    case "8":
                        await ConfigureAsync();
                        break;
                    case "9":
                        await SaveTokenAsync();
                        break;
                    case "10":
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
        Console.WriteLine("1. Rafraîchir l’état");
        Console.WriteLine("2. Afficher une journée");
        Console.WriteLine("3. Ajouter ou corriger un créneau");
        Console.WriteLine("4. Supprimer un créneau");
        Console.WriteLine("5. Mettre en pause ou reprendre");
        Console.WriteLine("6. Envoyer une journée dans 7pace");
        Console.WriteLine("7. Synchroniser avec 7pace");
        Console.WriteLine("8. Configurer l’application");
        Console.WriteLine("9. Enregistrer ou supprimer le jeton 7pace");
        Console.WriteLine("10. Rechercher et installer une mise à jour");
        Console.WriteLine("0. Quitter");
    }

    private async Task PrintStatusAsync()
    {
        using var result = await CallAsync("bootstrap", new { });
        var root = result.RootElement;
        var tracking = root.GetProperty("tracking");
        _paused = tracking.GetProperty("paused").GetBoolean();

        var title = Text(tracking, "title");
        var branch = Text(tracking, "branch");
        var state = Text(tracking, "state");
        var elapsed = tracking.TryGetProperty("elapsedSeconds", out var rawElapsed) && rawElapsed.TryGetInt32(out var seconds)
            ? Duration(seconds)
            : "0 min";

        Console.WriteLine($"Suivi : {TrackingLabel(state)} · {title}");
        if (branch.Length > 0) Console.WriteLine($"Branche : {branch}");
        Console.WriteLine($"Chrono : {elapsed}");

        if (!root.GetProperty("configured").GetBoolean())
        {
            Console.WriteLine("Configuration : dépôt Git à renseigner (choix 8)");
        }

        if (root.TryGetProperty("connections", out var connections))
        {
            Console.WriteLine(Text(connections.GetProperty("sevenpace"), "label"));
        }
    }

    private async Task ShowDayAsync()
    {
        var date = ReadDate("Journée", Today());
        var entries = await LoadDayAsync(date);
        PrintDay(date, entries);
    }

    private async Task SaveEntryAsync()
    {
        var date = ReadDate("Journée", Today());
        var entries = await LoadDayAsync(date);
        PrintDay(date, entries);

        var id = ReadOptionalPositive("Identifiant à corriger (vide pour ajouter)", null);
        var existing = id is null ? null : entries.FirstOrDefault(entry => entry.Id == id);
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
        var activity = ReadActivity(existing?.Activity ?? "ticket");
        var title = Read("Titre", existing?.Title ?? string.Empty) ?? string.Empty;
        var workItem = string.Equals(activity, "ticket", StringComparison.Ordinal)
            ? ReadOptionalPositive("Fix ou Task", existing?.WorkItem)
            : null;

        using var _ = await CallAsync("saveEntry", new
        {
            date,
            entry = new Entry
            {
                Id = existing?.Id,
                Start = start,
                End = end,
                Activity = activity,
                Title = title,
                WorkItem = workItem,
            },
        });

        Console.WriteLine(existing is null ? "Créneau ajouté." : "Créneau corrigé.");
        PrintDay(date, await LoadDayAsync(date));
    }

    private async Task DeleteEntryAsync()
    {
        var date = ReadDate("Journée", Today());
        var entries = await LoadDayAsync(date);
        PrintDay(date, entries);
        if (entries.Count == 0) return;

        var id = ReadOptionalPositive("Identifiant à supprimer", null);
        if (id is null) return;
        var entry = entries.FirstOrDefault(item => item.Id == id);
        if (entry is null)
        {
            Console.WriteLine("Ce créneau n’existe pas dans cette journée.");
            return;
        }

        if (!Confirm("Tape SUPPRIMER pour confirmer", "SUPPRIMER"))
        {
            Console.WriteLine("Suppression annulée.");
            return;
        }

        using var _ = await CallAsync("deleteEntry", new { date, id });
        Console.WriteLine("Créneau supprimé.");
    }

    private async Task TogglePauseAsync()
    {
        using var result = await CallAsync("setPaused", new { paused = !_paused });
        _paused = result.RootElement.GetProperty("tracking").GetProperty("paused").GetBoolean();
        Console.WriteLine(_paused ? "Suivi mis en pause." : "Suivi repris.");
    }

    private async Task SubmitDayAsync()
    {
        var date = ReadDate("Journée à envoyer", Today());
        var entries = await LoadDayAsync(date);
        PrintDay(date, entries);
        if (entries.Count == 0) return;

        Console.WriteLine("Aucun appel 7pace ne part avant la confirmation suivante.");
        if (!Confirm("Tape ENVOYER pour confirmer", "ENVOYER"))
        {
            Console.WriteLine("Envoi annulé.");
            return;
        }

        using var result = await CallAsync("submitDay", new { date });
        Console.WriteLine(Text(result.RootElement, "message"));
    }

    private async Task SynchronizeAsync()
    {
        var from = ReadDate("Premier jour", Today());
        var to = ReadDate("Dernier jour", from);

        using var preview = await CallAsync("synchronize", new { from, to, apply = false });
        var root = preview.RootElement;
        if (!root.GetProperty("ok").GetBoolean())
        {
            Console.WriteLine(Text(root, "message"));
            return;
        }

        var replaced = Number(root, "replaced");
        var removed = Number(root, "removed");
        var imported = Number(root, "imported");
        var delta = Number(root, "secondsDelta");
        Console.WriteLine($"Aperçu {from} → {to} : {replaced} remplacé(s), {removed} supprimé(s), {imported} importé(s), écart {SignedDuration(delta)}.");

        if (replaced == 0 && removed == 0 && imported == 0)
        {
            Console.WriteLine("Le planning correspond déjà à 7pace : rien à appliquer.");
            return;
        }

        Console.WriteLine("Les brouillons restent intacts; les créneaux déjà envoyés suivront 7pace.");
        if (!Confirm("Tape APPLIQUER pour confirmer", "APPLIQUER"))
        {
            Console.WriteLine("Synchronisation annulée : aucun fichier n’a été modifié.");
            return;
        }

        using var applied = await CallAsync("synchronize", new { from, to, apply = true });
        Console.WriteLine(Text(applied.RootElement, "message"));
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
        settings.Lunch = ReadWindow("Pause déjeuner", settings.Lunch);

        Console.WriteLine("Activités récurrentes — saisis 0 comme numéro pour retirer une attribution.");
        foreach (var activity in settings.Activities)
        {
            activity.Label = Read($"Libellé {activity.Key}", activity.Label) ?? activity.Label;
            activity.WorkItem = ReadOptionalPositive($"Fix ou Task {activity.Key}", activity.WorkItem, zeroClears: true);
        }
        settings.Onboarded = true;

        using var saved = await CallAsync("saveSettings", new { settings });
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

        if (!Confirm("Tape METTRE A JOUR pour confirmer", "METTRE A JOUR"))
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

    private async Task<List<Entry>> LoadDayAsync(string date)
    {
        using var result = await CallAsync("loadRange", new { from = date, to = date });
        var days = result.RootElement.GetProperty("days");
        return days.TryGetProperty(date, out var day)
            ? day.Deserialize<List<Entry>>(Wire) ?? new List<Entry>()
            : new List<Entry>();
    }

    private async Task<JsonDocument> CallAsync(string method, object parameters)
    {
        _ct.ThrowIfCancellationRequested();
        var payload = JsonSerializer.Serialize(parameters, Wire);
        var response = await _app.HandleAsync(method, payload, _ct);
        return JsonDocument.Parse(response);
    }

    private static void PrintDay(string date, IReadOnlyList<Entry> entries)
    {
        Console.WriteLine($"Journée du {date}");
        if (entries.Count == 0)
        {
            Console.WriteLine("  Aucun créneau.");
            return;
        }

        var total = 0;
        foreach (var entry in entries.OrderBy(item => item.StartMinutes))
        {
            var minutes = entry.EndMinutes - entry.StartMinutes;
            if (!entry.Excluded) total += minutes;
            var workItem = entry.WorkItem is int item ? $" · #{item}" : string.Empty;
            var state = entry.SentAt is not null ? "envoyé" : entry.Unassigned ? "à attribuer" : "brouillon";
            Console.WriteLine($"  [{entry.Id}] {entry.Start}–{entry.End} · {Activities.DefaultLabel(entry.Activity)}{workItem} · {entry.Title} · {state}");
        }
        Console.WriteLine($"  Total imputable : {Duration(total * 60)}");
    }

    private static string ReadActivity(string current)
    {
        var values = new[] { "ticket", "standup", "meeting", "review", "planning", "training", "unknown", "excluded" };
        Console.WriteLine("Activité :");
        for (var index = 0; index < values.Length; index++)
        {
            Console.WriteLine($"  {index + 1}. {Activities.DefaultLabel(values[index])}");
        }

        while (true)
        {
            var raw = Read("Numéro", (Array.IndexOf(values, current) + 1).ToString(CultureInfo.InvariantCulture));
            if (raw is null) return current;
            if (int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                && index >= 1 && index <= values.Length)
            {
                return values[index - 1];
            }
            Console.WriteLine("Choisis un numéro de la liste.");
        }
    }

    private static List<int[]> ReadWindows(IReadOnlyList<int[]> current)
    {
        var fallback = string.Join(',', current.Select(FormatWindow));
        while (true)
        {
            var raw = Read("Créneaux de travail (HH:MM-HH:MM,...)", fallback);
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

    private static int[] ReadWindow(string label, int[] current)
    {
        var fallback = FormatWindow(current);
        while (true)
        {
            var raw = Read($"{label} (HH:MM-HH:MM)", fallback);
            if (raw is null) return current.ToArray();
            try
            {
                return ParseWindow(raw);
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

    private static string ReadDate(string label, string fallback)
    {
        while (true)
        {
            var value = ReadRequired(label, fallback);
            try
            {
                TimeRules.ParseDate(value);
                return value;
            }
            catch (DomainException error)
            {
                Console.WriteLine(error.Message);
            }
        }
    }

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
            if (raw.Length == 0 && current is null) return null;
            if (zeroClears && raw == "0") return null;
            if (int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0) return value;
            Console.WriteLine(zeroClears ? "Indique un entier positif, ou 0 pour retirer l’attribution." : "Indique un entier positif.");
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

    private static bool Confirm(string prompt, string expected)
    {
        Console.Write($"{prompt} : ");
        return string.Equals(Console.ReadLine()?.Trim(), expected, StringComparison.Ordinal);
    }

    private static string Today() => TimeRules.DateKey(DateTime.Now);

    private static int Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string TrackingLabel(string state) => state switch
    {
        "running" => "en cours",
        "paused" => "en pause",
        "outside-hours" => "hors horaires",
        "no-repo" => "dépôt introuvable",
        "not-configured" => "non configuré",
        _ => state,
    };

    private static string SignedDuration(int seconds) =>
        seconds == 0 ? "0 min" : (seconds > 0 ? "+" : "−") + Duration(Math.Abs(seconds));

    private static string Duration(int seconds)
    {
        var minutes = Math.Max(0, seconds) / 60;
        return TimeRules.Readable(minutes);
    }
}
