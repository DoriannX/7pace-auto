#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace SeptPaceAuto.Services;

/// <summary>Dernière journée pour laquelle la notification du matin est déjà partie.</summary>
internal sealed class AnnouncedDay
{
    [JsonPropertyName("date")] public string? Date { get; set; }
}

/// <summary>
/// Notification du matin, désormais portée par le collecteur : c'est lui qui vit toute la
/// journée, et lui seul sait à quel moment une journée terminée arrive dans la file.
///
/// Une seule notification par journée calendaire, jamais avant le début des horaires de
/// travail — sans quoi le passage de minuit réveillerait l'utilisateur pour rien. La date
/// déjà annoncée est persistée : un redémarrage du collecteur ne rejoue pas l'annonce.
/// </summary>
internal sealed class MorningAnnouncer : IAsyncDisposable
{
    private readonly ITrackingApp _app;
    private readonly Func<DateTime> _clock;
    private readonly string? _terminal;
    private readonly TimeSpan _period;
    private readonly string _memory;
    private readonly Action<string, string, string?> _announce;
    private readonly CancellationTokenSource _life = new();

    private Task? _loop;

    /// <param name="terminal">Terminal ouvert au clic sur la notification, null quand il est introuvable.</param>
    /// <param name="memory">Fichier retenant la dernière journée annoncée.</param>
    /// <param name="announce">Envoi de la notification ; isolé pour ne pas en déclencher une vraie en test.</param>
    public MorningAnnouncer(
        ITrackingApp app,
        Func<DateTime> clock,
        string? terminal,
        string memory,
        TimeSpan? period = null,
        Action<string, string, string?>? announce = null)
    {
        _app = app;
        _clock = clock;
        _terminal = terminal;
        _memory = memory;
        _period = period ?? TimeSpan.FromMinutes(1);
        _announce = announce ?? Notifier.Show;
    }

    public void Start()
    {
        _loop = Task.Run(() => LoopAsync(_life.Token), CancellationToken.None);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_period);
        try
        {
            do
            {
                await AnnounceOnceAsync(ct).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Arrêt demandé.
        }
    }

    /// <summary>Un passage de la surveillance. Isolé pour être éprouvé sans attendre une minute.</summary>
    internal async Task<bool> AnnounceOnceAsync(CancellationToken ct)
    {
        int pending;
        int workStart;
        try
        {
            using var document = JsonDocument.Parse(await _app.HandleAsync("bootstrap", "{}", ct).ConfigureAwait(false));
            pending = Number(document.RootElement, "pending");
            workStart = Number(document.RootElement, "workStart");
        }
        catch (Exception error) when (error is DomainException or JsonException)
        {
            return false;
        }

        var now = _clock();
        var today = TimeRules.DateKey(now);
        if (!DueNow(today, TimeRules.MinuteOfDay(now), workStart, pending, Read())) return false;

        Remember(today);
        _announce(
            "7pace auto",
            pending == 1
                ? "Une journée terminée attend d’être vérifiée puis envoyée."
                : $"{pending} journées terminées attendent d’être vérifiées puis envoyées.",
            _terminal);
        return true;
    }

    /// <summary>Règle d'annonce, sans horloge ni disque : une fois par jour, une fois la matinée venue.</summary>
    internal static bool DueNow(string today, int minuteOfDay, int workStart, int pending, string? announced)
    {
        if (pending <= 0) return false;
        if (minuteOfDay < workStart) return false;
        return !string.Equals(announced, today, StringComparison.Ordinal);
    }

    private string? Read()
    {
        try
        {
            if (!File.Exists(_memory)) return null;
            var text = File.ReadAllText(_memory, AgentWire.Utf8);
            return string.IsNullOrWhiteSpace(text)
                ? null
                : JsonSerializer.Deserialize<AnnouncedDay>(text, Json.Wire)?.Date;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Remember(string date)
    {
        try
        {
            AppPaths.WriteAtomic(_memory, JsonSerializer.Serialize(new AnnouncedDay { Date = date }, Json.Pretty));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Au pire la notification repartira au prochain démarrage : rien n'est perdu.
        }
    }

    private static int Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : 0;

    public async ValueTask DisposeAsync()
    {
        _life.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Attendu.
            }
        }
        _life.Dispose();
    }
}
