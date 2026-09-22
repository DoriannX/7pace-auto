using System.Text;
using SeptPaceAuto.Services;
using SeptPaceAuto.Terminal;

try
{
    // Sans console attachée — terminal piloté par un script ou par un test —, Windows refuse
    // de changer la page de codes. Ce n'est pas une raison de ne pas démarrer.
    Console.InputEncoding = Encoding.UTF8;
    Console.OutputEncoding = Encoding.UTF8;
}
catch (Exception error) when (error is IOException or PlatformNotSupportedException or UnauthorizedAccessException)
{
}

using var lifetime = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    // CTRL+C ferme l'interface, et elle seule : le collecteur continue de collecter.
    eventArgs.Cancel = true;
    lifetime.Cancel();
};

try
{
    Directory.CreateDirectory(TrackingAppFactory.DataFolder);

    Console.WriteLine("7pace auto — terminal");
    Console.WriteLine($"Données : {TrackingAppFactory.DataFolder}");
    Console.WriteLine("Recherche du collecteur en arrière-plan…");

    // Plusieurs terminaux peuvent être ouverts : aucun n'écrit, tous passent par le
    // collecteur, qui reste seul à tenir la plume.
    await using var channel = new AgentClient();
    if (!await channel.ConnectAsync(launchIfMissing: true, lifetime.Token))
    {
        Console.WriteLine(channel.Trouble ?? "Le collecteur de fond ne répond pas.");
        Console.WriteLine("Rien n’est collecté tant qu’il ne tourne pas : relance-le depuis le menu.");
    }

    return await new TerminalUi(channel, lifetime.Token).RunAsync();
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
