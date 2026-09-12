using System.Text.Json;
using MyWhoosh2Garmin.Processes;

namespace MyWhoosh2Garmin.Garmin;

public class MacKeychainTokenStore : ITokenStore
{
    private const string ServiceName = "MyWhoosh2Garmin-GarminOAuth1";
    private readonly IProcessRunner _processRunner;

    public MacKeychainTokenStore(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public async Task SaveOAuth1TokenAsync(OAuth1Token token)
    {
        string json = JsonSerializer.Serialize(token);

        // Ignore the result: this fails harmlessly if no entry exists yet.
        await _processRunner.RunAsync("security", ["delete-generic-password", "-s", ServiceName]);

        var result = await _processRunner.RunAsync(
            "security", ["add-generic-password", "-s", ServiceName, "-a", Environment.UserName, "-w", json]);

        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Échec d'écriture dans le Keychain : {result.StandardError}");
    }

    public async Task<OAuth1Token?> LoadOAuth1TokenAsync()
    {
        var result = await _processRunner.RunAsync(
            "security", ["find-generic-password", "-s", ServiceName, "-w"]);

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
            return null;

        return JsonSerializer.Deserialize<OAuth1Token>(result.StandardOutput.Trim());
    }
}
