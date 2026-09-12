using MyWhoosh2Garmin.Tracking;

namespace MyWhoosh2Garmin.Tests.Tracking;

public class SqliteActivityTrackerTests
{
    [Fact]
    public async Task Unknown_hash_is_not_processed()
    {
        using var tracker = new SqliteActivityTracker("Data Source=:memory:");

        bool isProcessed = await tracker.IsProcessedAsync("some-hash");

        Assert.False(isProcessed);
    }

    [Fact]
    public async Task Marking_processed_makes_it_show_as_processed()
    {
        using var tracker = new SqliteActivityTracker("Data Source=:memory:");

        await tracker.MarkProcessedAsync("hash-1", "MyNewActivity-1.fit", "garmin-activity-42");

        Assert.True(await tracker.IsProcessedAsync("hash-1"));
    }
}
