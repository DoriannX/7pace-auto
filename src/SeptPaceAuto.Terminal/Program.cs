using System.Text;
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
