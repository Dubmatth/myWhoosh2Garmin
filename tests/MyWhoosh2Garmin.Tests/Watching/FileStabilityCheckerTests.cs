using Microsoft.Extensions.Time.Testing;
using MyWhoosh2Garmin.Watching;

namespace MyWhoosh2Garmin.Tests.Watching;

public class FileStabilityCheckerTests
{
    [Fact]
    public async Task Returns_true_once_size_stops_changing()
    {
        var timeProvider = new FakeTimeProvider();
        var sizeAtCall = new Queue<long>(new long[] { 100, 250, 250, 250 });
        var checker = new FileStabilityChecker(
            timeProvider,
            _ => sizeAtCall.Count > 1 ? sizeAtCall.Dequeue() : sizeAtCall.Peek());

        var task = checker.WaitUntilStableAsync(
            "irrelevant-path.fit",
            checkInterval: TimeSpan.FromSeconds(1),
            stablePeriod: TimeSpan.FromSeconds(2),
            CancellationToken.None);

        // Advance time in 1s steps so the fake delay completes each poll.
        for (int i = 0; i < 4 && !task.IsCompleted; i++)
        {
            timeProvider.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }

        bool result = await task;
        Assert.True(result);
    }

    [Fact]
    public async Task Returns_false_when_cancelled_before_stable()
    {
        var timeProvider = new FakeTimeProvider();
        using var cts = new CancellationTokenSource();
        var checker = new FileStabilityChecker(timeProvider, _ => 100);

        var task = checker.WaitUntilStableAsync(
            "irrelevant-path.fit",
            checkInterval: TimeSpan.FromSeconds(1),
            stablePeriod: TimeSpan.FromSeconds(10),
            cts.Token);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await Task.Yield();
        cts.Cancel();

        bool result = await task;
        Assert.False(result);
    }
}
