using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MyWhoosh2Garmin;
using MyWhoosh2Garmin.Garmin;
using MyWhoosh2Garmin.Processes;
using MyWhoosh2Garmin.Notifications;
using MyWhoosh2Garmin.Tracking;
using MyWhoosh2Garmin.Watching;

if (args.Length > 0 && args[0] == "login")
{
    await RunLoginCommandAsync();
    return;
}

var builder = Host.CreateApplicationBuilder(args);

string databasePath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "MyWhoosh2Garmin", "tracker.db");
Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

builder.Services.AddSingleton<IActivityTracker>(_ => new SqliteActivityTracker($"Data Source={databasePath}"));
builder.Services.AddSingleton<IProcessRunner, SystemProcessRunner>();
builder.Services.AddSingleton<ITokenStore, MacKeychainTokenStore>();
builder.Services.AddSingleton<IMacNotifier, OsascriptNotifier>();
builder.Services.AddSingleton(new HttpClient());
builder.Services.AddSingleton<GarminAuthClient>();
builder.Services.AddSingleton<GarminUploadClient>();
builder.Services.AddSingleton<SyncPipeline>();
builder.Services.AddSingleton<IFileStabilityChecker, FileStabilityChecker>();

builder.Services.AddHostedService(sp => new ActivityFileWatcherService(
    MyWhooshFolderLocator.GetActivitiesFolder(),
    sp.GetRequiredService<IFileStabilityChecker>(),
    (path, ct) => sp.GetRequiredService<SyncPipeline>().HandleNewActivityAsync(path, ct),
    sp.GetRequiredService<ILogger<ActivityFileWatcherService>>()));

var host = builder.Build();
await host.RunAsync();

async Task RunLoginCommandAsync()
{
    Console.Write("Email Garmin : ");
    string email = Console.ReadLine() ?? throw new InvalidOperationException("Email requis.");

    Console.Write("Mot de passe Garmin : ");
    string password = ReadPasswordMasked();

    using var httpClient = new HttpClient();
    var authClient = new GarminAuthClient(httpClient);
    var tokenStore = new MacKeychainTokenStore(new SystemProcessRunner());

    Console.WriteLine("Connexion à Garmin...");
    string ticket = await authClient.LoginAndGetTicketAsync(
        email, password, PromptMfaCodeAsync, CancellationToken.None);
    OAuth1Token oauth1 = await authClient.GetOAuth1TokenAsync(ticket, CancellationToken.None);

    // Confirms the token pair actually works before saving it.
    await authClient.ExchangeForAccessTokenAsync(oauth1, CancellationToken.None);

    await tokenStore.SaveOAuth1TokenAsync(oauth1);
    Console.WriteLine("Compte Garmin lié avec succès. Le service peut maintenant tourner en tâche de fond.");
}

Task<string> PromptMfaCodeAsync()
{
    Console.Write("Code de vérification Garmin (reçu par email/SMS) : ");
    string code = Console.ReadLine() ?? throw new InvalidOperationException("Code de vérification requis.");
    return Task.FromResult(code);
}

string ReadPasswordMasked()
{
    var password = new System.Text.StringBuilder();
    ConsoleKeyInfo key;
    while ((key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
    {
        if (key.Key == ConsoleKey.Backspace && password.Length > 0)
        {
            password.Length--;
            Console.Write("\b \b");
        }
        else if (!char.IsControl(key.KeyChar))
        {
            password.Append(key.KeyChar);
            Console.Write("*");
        }
    }
    Console.WriteLine();
    return password.ToString();
}
