using MyWhoosh2Garmin.Garmin;
using MyWhoosh2Garmin.Processes;

namespace MyWhoosh2Garmin.Tests.Garmin;

public class FakeProcessRunner : IProcessRunner
{
    public List<(string FileName, IReadOnlyList<string> Arguments)> Calls { get; } = new();
    public Func<string, IReadOnlyList<string>, ProcessResult> Respond { get; set; }
        = (_, _) => new ProcessResult(0, "", "");

    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments)
    {
        Calls.Add((fileName, arguments));
        return Task.FromResult(Respond(fileName, arguments));
    }
}

public class MacKeychainTokenStoreTests
{
    [Fact]
    public async Task SaveOAuth1TokenAsync_calls_security_add_generic_password_with_json()
    {
        var runner = new FakeProcessRunner();
        var store = new MacKeychainTokenStore(runner);

        await store.SaveOAuth1TokenAsync(new OAuth1Token("tok-123", "secret-456"));

        var addCall = runner.Calls.Single(c => c.Arguments.Contains("add-generic-password"));
        Assert.Equal("security", addCall.FileName);
        int passwordFlagIndex = addCall.Arguments.ToList().IndexOf("-w");
        string storedJson = addCall.Arguments[passwordFlagIndex + 1];
        Assert.Contains("tok-123", storedJson);
        Assert.Contains("secret-456", storedJson);
    }

    [Fact]
    public async Task LoadOAuth1TokenAsync_returns_null_when_nothing_stored()
    {
        var runner = new FakeProcessRunner
        {
            Respond = (_, _) => new ProcessResult(44, "", "security: SecKeychainSearchCopyNext: The specified item could not be found."),
        };
        var store = new MacKeychainTokenStore(runner);

        OAuth1Token? token = await store.LoadOAuth1TokenAsync();

        Assert.Null(token);
    }

    [Fact]
    public async Task LoadOAuth1TokenAsync_parses_stored_json()
    {
        var runner = new FakeProcessRunner
        {
            Respond = (_, args) => args.Contains("find-generic-password")
                ? new ProcessResult(0, """{"OAuthToken":"tok-123","OAuthTokenSecret":"secret-456"}""" + "\n", "")
                : new ProcessResult(0, "", ""),
        };
        var store = new MacKeychainTokenStore(runner);

        OAuth1Token? token = await store.LoadOAuth1TokenAsync();

        Assert.Equal(new OAuth1Token("tok-123", "secret-456"), token);
    }
}
