#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SeptPaceAuto.Services;

/// <summary>
/// Une activité récurrente : son libellé et l'élément de travail sur lequel imputer son
/// temps. <see cref="WorkItem"/> reste nul tant que l'utilisateur ne l'a pas renseigné —
/// aucun numéro n'est fourni par défaut.
/// </summary>
public sealed class ActivitySetting
{
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;
    [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;
    [JsonPropertyName("workItem")] public int? WorkItem { get; set; }
}

/// <summary>
/// Réglages tels qu'ils sont écrits dans %LOCALAPPDATA%\7pace-auto\settings.json et tels
/// que l'interface les manipule. Aucune valeur par défaut ne désigne un poste, une
/// entreprise ou une équipe : tout ce qui est propre à l'utilisateur part vide.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Dépôt GitHub consulté pour les mises à jour de l'application elle-même.</summary>
    public const string DefaultUpdateRepository = "DoriannX/7pace-auto";

    [JsonPropertyName("repoPath")] public string RepoPath { get; set; } = string.Empty;
    [JsonPropertyName("pollSeconds")] public int PollSeconds { get; set; } = 30;
    [JsonPropertyName("azureOrganization")] public string AzureOrganization { get; set; } = string.Empty;
    [JsonPropertyName("sevenPaceAccount")] public string SevenPaceAccount { get; set; } = string.Empty;
    [JsonPropertyName("workWindows")] public List<int[]> WorkWindows { get; set; } = DefaultWindows();
    [JsonPropertyName("lunch")] public int[] Lunch { get; set; } = { 750, 810 };
    [JsonPropertyName("activities")] public List<ActivitySetting> Activities { get; set; } = DefaultActivities();
    [JsonPropertyName("updateRepository")] public string UpdateRepository { get; set; } = DefaultUpdateRepository;
    [JsonPropertyName("checkUpdates")] public bool CheckUpdates { get; set; } = true;

    /// <summary>
    /// Échelle choisie pour la vue jour, en pixels par heure. Zéro signifie « aucun zoom
    /// mémorisé » : la journée est alors ajustée à la hauteur de la fenêtre. Ce n'est pas
    /// une saisie — l'interface l'écrit seule, après un geste de zoom.
    /// </summary>
    [JsonPropertyName("dayHourPx")] public int DayHourPx { get; set; }

    /// <summary>
    /// La prise en main a été menée jusqu'au bout. Faux tant que l'utilisateur ne l'a pas
    /// terminée : c'est ce qui décide de l'afficher au démarrage.
    /// </summary>
    [JsonPropertyName("onboarded")] public bool Onboarded { get; set; }

    private static List<int[]> DefaultWindows() => new() { new[] { 510, 750 }, new[] { 810, 1020 } };

    private static List<ActivitySetting> DefaultActivities()
    {
        var list = new List<ActivitySetting>(Services.Activities.Configurable.Count);
        foreach (var key in Services.Activities.Configurable)
        {
            list.Add(new ActivitySetting { Key = key, Label = Services.Activities.DefaultLabel(key), WorkItem = null });
        }
        return list;
    }
}

/// <summary>
/// Réglages actifs : la version validée des réglages, plus tout ce qui s'en déduit
/// (créneaux, tâches fixes, adresse 7pace). Immuable — un enregistrement produit un
/// nouveau profil, que les services relisent par leur fournisseur.
/// </summary>
public sealed class Profile
{
    private const string SevenPaceHostSuffix = ".timehub.7pace.com";
    private const int MinutesInDay = 24 * 60;

    private readonly Dictionary<string, ActivitySetting> _activities;

    private Profile(AppSettings settings, Schedule schedule, Dictionary<string, ActivitySetting> activities)
    {
        Settings = settings;
        Schedule = schedule;
        _activities = activities;

        var fixedTasks = new Dictionary<string, int>(activities.Count, StringComparer.Ordinal);
        foreach (var pair in activities)
        {
            if (pair.Value.WorkItem is int item) fixedTasks[pair.Key] = item;
        }
        FixedTasks = fixedTasks;

        SevenPaceEndpoint = EndpointFor(settings.SevenPaceAccount);
    }

    public AppSettings Settings { get; }

    public Schedule Schedule { get; }

    /// <summary>Activités récurrentes réellement rattachées à un élément de travail.</summary>
    public IReadOnlyDictionary<string, int> FixedTasks { get; }

    /// <summary>Adresse d'écriture 7pace, nulle tant que le compte n'est pas renseigné.</summary>
    public string? SevenPaceEndpoint { get; }

    /// <summary>
    /// Adresse des relevés 7pace d'un compte donné, sans passer par des réglages
    /// enregistrés : nulle tant que le compte est vide ou impossible à nettoyer.
    /// </summary>
    public static string? EndpointFor(string? account)
    {
        var name = AccountName(account);
        return name.Length == 0
            ? null
            : string.Concat("https://", name, SevenPaceHostSuffix, "/api/rest/workLogs?api-version=3.2");
    }

    /// <summary>Compte 7pace nettoyé, vide quand la valeur ne donne aucun nom de compte.</summary>
    public static string AccountName(string? raw) => Account(raw, strict: false);

    /// <summary>
    /// Organisation Azure DevOps nettoyée, ou null quand ce n'est pas une adresse http(s) :
    /// c'est la seule forme que la CLI accepte.
    /// </summary>
    public static string? OrganizationUrl(string? raw)
    {
        var text = (raw ?? string.Empty).Trim().TrimEnd('/');
        if (text.Length == 0) return null;

        return Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
                || string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal))
            ? text
            : null;
    }

    /// <summary>Le suivi peut travailler : un dépôt Git est désigné et présent sur ce poste.</summary>
    public bool Configured =>
        Settings.RepoPath.Length > 0 && SafeDirectoryExists(Settings.RepoPath);

    public string Label(string activity) =>
        _activities.TryGetValue(activity, out var configured) && configured.Label.Length > 0
            ? configured.Label
            : Activities.DefaultLabel(activity);

    public int? WorkItemFor(string activity) =>
        _activities.TryGetValue(activity, out var configured) ? configured.WorkItem : null;

    /// <summary>Réglages du disque, réparés en silence : un fichier abîmé ne bloque pas le démarrage.</summary>
    public static Profile FromDisk()
    {
        var stored = AppPaths.ReadJson<AppSettings>(AppPaths.Settings);
        if (stored is null)
        {
            var fresh = Create(new AppSettings(), strict: false);
            TrySave(fresh);
            return fresh;
        }

        if (stored.SevenPaceAccount.Length == 0)
        {
            // Installation antérieure : le compte était noyé dans une adresse complète.
            var recovered = LegacyAccount();
            if (recovered is not null) stored.SevenPaceAccount = recovered;
        }
        return Create(stored, strict: false);
    }

    /// <summary>
    /// Valide des réglages venus de l'interface puis les écrit. Toute valeur refusée lève
    /// une <see cref="DomainException"/> dont le message est la phrase à afficher.
    /// </summary>
    public static Profile Save(AppSettings? incoming)
    {
        var profile = Create(incoming, strict: true);
        try
        {
            AppPaths.EnsureRoot();
            AppPaths.WriteAtomic(AppPaths.Settings, JsonSerializer.Serialize(profile.Settings, Json.Pretty));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new DomainException("Impossible d’écrire les réglages : vérifie l’accès à %LOCALAPPDATA%\\7pace-auto.");
        }
        return profile;
    }

    /// <summary>
    /// Mémorise l'échelle de la vue jour. Cette préférence n'est pas une saisie : une
    /// valeur aberrante est réparée plutôt que refusée, et un fichier non inscriptible
    /// n'échoue pas — le zoom vaut moins qu'un démarrage.
    /// </summary>
    public Profile WithDayZoom(int dayHourPx)
    {
        var next = Create(Settings, strict: false);
        next.Settings.DayHourPx = DayZoom(dayHourPx);
        TrySave(next);
        return next;
    }

    private static void TrySave(Profile profile)
    {
        try
        {
            AppPaths.EnsureRoot();
            AppPaths.WriteAtomic(AppPaths.Settings, JsonSerializer.Serialize(profile.Settings, Json.Pretty));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Les réglages par défaut suffisent : ne pas empêcher le démarrage pour un fichier.
        }
    }

    /// <summary>
    /// Construit le profil. <paramref name="strict"/> vient de l'interface : chaque valeur
    /// fautive est refusée avec sa raison. Sinon la valeur fautive est remplacée sans bruit,
    /// pour qu'un fichier édité à la main n'empêche jamais l'application de démarrer.
    /// </summary>
    public static Profile Create(AppSettings? incoming, bool strict)
    {
        var source = incoming ?? new AppSettings();
        var result = new AppSettings
        {
            RepoPath = RepoPath(source.RepoPath, strict),
            PollSeconds = PollSeconds(source.PollSeconds, strict),
            AzureOrganization = Organization(source.AzureOrganization, strict),
            SevenPaceAccount = Account(source.SevenPaceAccount, strict),
            UpdateRepository = Repository(source.UpdateRepository, strict),
            CheckUpdates = source.CheckUpdates,
            Onboarded = source.Onboarded,
            DayHourPx = DayZoom(source.DayHourPx),
        };

        var windows = Windows(source.WorkWindows, strict);
        var lunch = Lunch(source.Lunch, strict);
        result.WorkWindows = new List<int[]>(windows.Count);
        foreach (var window in windows) result.WorkWindows.Add(new[] { window.Start, window.End });
        result.Lunch = new[] { lunch.Start, lunch.End };

        var activities = ActivityMap(source.Activities, strict);
        result.Activities = new List<ActivitySetting>(activities.Count);
        foreach (var key in Activities.Configurable) result.Activities.Add(activities[key]);

        // Un poste déjà réglé avant l'arrivée de la configuration guidée ne la rejoue pas :
        // un dépôt valide vaut onboarding terminé.
        var profile = new Profile(result, new Schedule(windows, lunch), activities);
        if (!result.Onboarded && profile.Configured) result.Onboarded = true;

        return profile;
    }

    // ---------- validation, valeur par valeur ----------

    private static string RepoPath(string? raw, bool strict)
    {
        var text = (raw ?? string.Empty).Trim().Trim('"');
        if (!strict) return text;

        if (text.Length == 0)
        {
            throw new DomainException("Indique le dossier du dépôt Git à surveiller.");
        }

        string full;
        try
        {
            full = Path.GetFullPath(text);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new DomainException($"« {text} » n’est pas un chemin de dossier valide.");
        }

        if (!SafeDirectoryExists(full))
        {
            throw new DomainException($"Le dossier « {full} » n’existe pas sur ce poste.");
        }
        var marker = Path.Combine(full, ".git");
        if (!SafeDirectoryExists(marker) && !SafeFileExists(marker))
        {
            throw new DomainException($"Le dossier « {full} » n’est pas un dépôt Git : il ne contient pas de « .git ».");
        }
        return full;
    }

    private static int PollSeconds(int raw, bool strict)
    {
        if (raw is >= 10 and <= 300) return raw;
        if (strict) throw new DomainException("L’intervalle de relevé doit être compris entre 10 et 300 secondes.");
        return new AppSettings().PollSeconds;
    }

    /// <summary>
    /// Échelle de la vue jour : bornée aux limites du geste de zoom (36 à 600 px par
    /// heure), zéro valant « pas de zoom mémorisé ». Jamais de refus, dans les deux
    /// modes : la valeur ne vient pas d'un formulaire, elle ne doit pas empêcher un
    /// enregistrement de réglages.
    /// </summary>
    private static int DayZoom(int raw) => raw <= 0 ? 0 : Math.Clamp(raw, 36, 600);

    private static string Organization(string? raw, bool strict)
    {
        var text = (raw ?? string.Empty).Trim().TrimEnd('/');
        if (text.Length == 0) return string.Empty;
        if (OrganizationUrl(text) is string organization) return organization;

        if (strict)
        {
            throw new DomainException("L’organisation Azure DevOps doit être une adresse http(s), par exemple https://dev.azure.com/mon-organisation.");
        }
        return string.Empty;
    }

    /// <summary>
    /// Nom de compte 7pace seul. Une adresse complète collée depuis le navigateur est
    /// ramenée au compte plutôt que refusée.
    /// </summary>
    private static string Account(string? raw, bool strict)
    {
        var text = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (text.Length == 0) return string.Empty;

        if (text.StartsWith("https://", StringComparison.Ordinal)) text = text["https://".Length..];
        else if (text.StartsWith("http://", StringComparison.Ordinal)) text = text["http://".Length..];
        var slash = text.IndexOf('/');
        if (slash >= 0) text = text[..slash];
        var dot = text.IndexOf('.');
        if (dot >= 0) text = text[..dot];

        foreach (var character in text)
        {
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_') continue;
            if (strict)
            {
                throw new DomainException("Le compte 7pace ne doit contenir que des lettres, des chiffres ou des tirets, par exemple mon-entreprise.");
            }
            return string.Empty;
        }
        return text;
    }

    private static string Repository(string? raw, bool strict)
    {
        var text = (raw ?? string.Empty).Trim().TrimEnd('/');
        if (text.Length == 0) return AppSettings.DefaultUpdateRepository;

        const string github = "github.com/";
        var marker = text.IndexOf(github, StringComparison.OrdinalIgnoreCase);
        if (marker >= 0) text = text[(marker + github.Length)..];
        if (text.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) text = text[..^4];

        var parts = text.Split('/');
        if (parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0
            && IsRepositoryName(parts[0]) && IsRepositoryName(parts[1]))
        {
            return string.Concat(parts[0], "/", parts[1]);
        }

        if (strict)
        {
            throw new DomainException("Le dépôt de mise à jour doit s’écrire proprietaire/depot, par exemple DoriannX/7pace-auto.");
        }
        return AppSettings.DefaultUpdateRepository;
    }

    private static bool IsRepositoryName(string value)
    {
        foreach (var character in value)
        {
            if (character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.') continue;
            return false;
        }
        return true;
    }

    private static List<(int Start, int End)> Windows(List<int[]>? raw, bool strict)
    {
        var windows = new List<(int Start, int End)>(raw?.Count ?? 0);
        foreach (var pair in raw ?? new List<int[]>())
        {
            if (pair is null || pair.Length != 2)
            {
                if (strict) throw new DomainException("Chaque créneau de travail s’écrit [début, fin] en minutes depuis minuit.");
                continue;
            }
            var (start, end) = (pair[0], pair[1]);
            if (start < 0 || end > MinutesInDay)
            {
                if (strict) throw new DomainException("Les créneaux de travail doivent tenir dans la journée, entre 0 et 1440 minutes.");
                continue;
            }
            if (start >= end)
            {
                if (strict) throw new DomainException($"Le créneau {TimeRules.Clock(start)} – {TimeRules.Clock(end)} se termine avant de commencer.");
                continue;
            }
            windows.Add((start, end));
        }

        if (windows.Count == 0)
        {
            if (strict) throw new DomainException("Définis au moins un créneau de travail.");
            windows.AddRange(SeptPaceAuto.Services.Schedule.Default.Windows);
            return windows;
        }

        windows.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        for (var index = 1; index < windows.Count; index++)
        {
            if (windows[index].Start >= windows[index - 1].End) continue;
            if (strict)
            {
                throw new DomainException($"Les créneaux {TimeRules.Clock(windows[index - 1].Start)} – {TimeRules.Clock(windows[index - 1].End)} et {TimeRules.Clock(windows[index].Start)} – {TimeRules.Clock(windows[index].End)} se chevauchent.");
            }
            windows.RemoveAt(index--);
        }
        return windows;
    }

    private static (int Start, int End) Lunch(int[]? raw, bool strict)
    {
        if (raw is { Length: 2 } && raw[0] >= 0 && raw[1] <= MinutesInDay && raw[0] <= raw[1])
        {
            return (raw[0], raw[1]);
        }
        if (strict)
        {
            throw new DomainException("La pause déjeuner s’écrit [début, fin] et doit tenir dans la journée, entre 0 et 1440 minutes.");
        }
        return SeptPaceAuto.Services.Schedule.Default.Lunch;
    }

    private static Dictionary<string, ActivitySetting> ActivityMap(List<ActivitySetting>? raw, bool strict)
    {
        var map = new Dictionary<string, ActivitySetting>(Activities.Configurable.Count, StringComparer.Ordinal);
        foreach (var key in Activities.Configurable)
        {
            map[key] = new ActivitySetting { Key = key, Label = Activities.DefaultLabel(key), WorkItem = null };
        }

        foreach (var activity in raw ?? new List<ActivitySetting>())
        {
            if (activity is null) continue;
            var key = (activity.Key ?? string.Empty).Trim();
            if (!map.TryGetValue(key, out var target))
            {
                if (strict) throw new DomainException($"Activité inconnue dans les réglages : « {key} ».");
                continue;
            }

            var label = (activity.Label ?? string.Empty).Trim();
            if (label.Length > 0) target.Label = label;

            if (activity.WorkItem is int item)
            {
                if (item < 1)
                {
                    if (strict) throw new DomainException($"Le numéro de tâche de « {target.Label} » doit être un entier positif.");
                    continue;
                }
                target.WorkItem = item;
            }
        }
        return map;
    }

    /// <summary>
    /// Compte 7pace récupéré depuis l'ancienne clé « sevenPaceEndpoint », qui contenait
    /// l'adresse complète. Lu à part pour que le fichier écrit garde la forme du contrat.
    /// </summary>
    private static string? LegacyAccount()
    {
        try
        {
            if (!File.Exists(AppPaths.Settings)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(AppPaths.Settings, Encoding.UTF8));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!document.RootElement.TryGetProperty("sevenPaceEndpoint", out var endpoint) || endpoint.ValueKind != JsonValueKind.String)
            {
                return null;
            }
            if (!Uri.TryCreate(endpoint.GetString(), UriKind.Absolute, out var uri)) return null;
            var host = uri.Host;
            var dot = host.IndexOf('.');
            var account = dot > 0 ? host[..dot] : host;
            return account.Length == 0 ? null : Account(account, strict: false);
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool SafeDirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool SafeFileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
