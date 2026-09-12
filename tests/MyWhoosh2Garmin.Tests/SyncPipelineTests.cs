using Microsoft.Extensions.Logging.Abstractions;
using MyWhoosh2Garmin;
using MyWhoosh2Garmin.Garmin;
using MyWhoosh2Garmin.Notifications;
using MyWhoosh2Garmin.Tracking;

namespace MyWhoosh2Garmin.Tests;

public class FakeActivityTracker : IActivityTracker
{
    public HashSet<string> ProcessedHashes { get; } = new();
    public string? LastMarkedFileName { get; private set; }
    public string? LastMarkedActivityId { get; private set; }

    public Task<bool> IsProcessedAsync(string fileHash) => Task.FromResult(ProcessedHashes.Contains(fileHash));

    public Task MarkProcessedAsync(string fileHash, string fileName, string? garminActivityId)
    {
        ProcessedHashes.Add(fileHash);
        LastMarkedFileName = fileName;
        LastMarkedActivityId = garminActivityId;
        return Task.CompletedTask;
    }
}

public class FakeTokenStore : ITokenStore
{
    public OAuth1Token? StoredToken { get; set; }
    public Task SaveOAuth1TokenAsync(OAuth1Token token) { StoredToken = token; return Task.CompletedTask; }
    public Task<OAuth1Token?> LoadOAuth1TokenAsync() => Task.FromResult(StoredToken);
}

public class FakeMacNotifier : IMacNotifier
{
    public List<(string Title, string Message)> Notifications { get; } = new();
    public Task NotifyAsync(string title, string message)
    {
        Notifications.Add((title, message));
        return Task.CompletedTask;
    }
}

public class SyncPipelineTests
{
    private static string CreateTempFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"pipeline-test-{Guid.NewGuid():N}.fit");
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    [Fact]
    public async Task Skips_already_processed_files_without_calling_garmin()
    {
        string filePath = CreateTempFile();
        var tracker = new FakeActivityTracker();
        tracker.ProcessedHashes.Add(MyWhoosh2Garmin.Utilities.FileHasher.ComputeSha256(filePath));
        var tokenStore = new FakeTokenStore { StoredToken = new OAuth1Token("t", "s") };
        var notifier = new FakeMacNotifier();

        // authClient/uploadClient are real instances but must never be called for this test to pass,
        // since there's no HttpClient wired to a fake handler here — any real call would throw.
        var pipeline = new SyncPipeline(
            tracker, tokenStore,
            new GarminAuthClient(new HttpClient()),
            new GarminUploadClient(new HttpClient()),
            notifier, NullLogger<SyncPipeline>.Instance);

        await pipeline.HandleNewActivityAsync(filePath, CancellationToken.None);

        Assert.Empty(notifier.Notifications);
        File.Delete(filePath);
    }

    [Fact]
    public async Task Logs_and_returns_when_no_garmin_account_linked()
    {
        string filePath = CreateTempFile();
        var tracker = new FakeActivityTracker();
        var tokenStore = new FakeTokenStore { StoredToken = null };
        var notifier = new FakeMacNotifier();

        var pipeline = new SyncPipeline(
            tracker, tokenStore,
            new GarminAuthClient(new HttpClient()),
            new GarminUploadClient(new HttpClient()),
            notifier, NullLogger<SyncPipeline>.Instance);

        await pipeline.HandleNewActivityAsync(filePath, CancellationToken.None);

        Assert.Empty(notifier.Notifications);
        Assert.Empty(tracker.ProcessedHashes);
        File.Delete(filePath);
    }
}
