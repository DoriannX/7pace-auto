using System.Text;
using System.Text.Json;
using SeptPaceAuto.Services;
using SeptPaceAuto.Terminal;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

using var lifetime = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    lifetime.Cancel();
};

using var instance = new Mutex(
    initiallyOwned: true,
    TrackingAppFactory.InstanceMutexName,
    out var owned);

if (!owned)
{
    Console.Error.WriteLine("7pace auto utilise déjà ce profil de données. Ferme l’autre terminal avant de recommencer.");
    return 1;
}

try
{
    Directory.CreateDirectory(TrackingAppFactory.DataFolder);
    await using var app = TrackingAppFactory.Create();
    await app.StartAsync(lifetime.Token);
    await AnnouncePendingAsync(app, lifetime.Token);
    return await new TerminalUi(app, lifetime.Token).RunAsync();
}
catch (OperationCanceledException)
{
    return 0;
}
catch (DomainException error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
catch (Exception error)
{
    Console.Error.WriteLine($"Démarrage impossible : {error.Message}");
    return 1;
}

// Notification unique du matin : l'application démarre minimisée avec la session, et c'est
// le seul signal qui ramène vers le terminal.
static async Task AnnouncePendingAsync(ITrackingApp app, CancellationToken ct)
{
    int pending;
    try
    {
        using var document = JsonDocument.Parse(await app.HandleAsync("bootstrap", "{}", ct));
        pending = document.RootElement.TryGetProperty("pending", out var value) && value.TryGetInt32(out var count)
            ? count
            : 0;
    }
    catch (Exception error) when (error is DomainException or JsonException)
    {
        return;
    }
    if (pending == 0) return;

    Notifier.Show(
        "7pace auto",
        pending == 1
            ? "Une journée terminée attend d’être vérifiée puis envoyée."
            : $"{pending} journées terminées attendent d’être vérifiées puis envoyées.");
}
