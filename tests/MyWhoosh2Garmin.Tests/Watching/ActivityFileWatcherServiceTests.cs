using Microsoft.Extensions.Logging.Abstractions;
using MyWhoosh2Garmin.Watching;

namespace MyWhoosh2Garmin.Tests.Watching;

public class AlwaysStableChecker : IFileStabilityChecker
{
    public Task<bool> WaitUntilStableAsync(string path, TimeSpan checkInterval, TimeSpan stablePeriod, CancellationToken ct)
        => Task.FromResult(true);
}

public class ActivityFileWatcherServiceTests
{
    [Fact]
    public async Task Picks_up_a_pre_existing_fit_file_via_polling()
    {
        string tempDir = Directory.CreateTempSubdirectory("mw2g-test-").FullName;
        string fitFile = Path.Combine(tempDir, "MyNewActivity-1.fit");
        File.WriteAllBytes(fitFile, [1, 2, 3]);

        var seenFiles = new List<string>();
        var service = new ActivityFileWatcherService(
            tempDir,
            new AlwaysStableChecker(),
            (path, ct) => { seenFiles.Add(path); return Task.CompletedTask; },
            NullLogger<ActivityFileWatcherService>.Instance,
            pollInterval: TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await service.StartAsync(cts.Token);
            await Task.Delay(300);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            Directory.Delete(tempDir, recursive: true);
        }

        Assert.Contains(fitFile, seenFiles);
    }
}
