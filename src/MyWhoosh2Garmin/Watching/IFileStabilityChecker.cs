namespace MyWhoosh2Garmin.Watching;

public interface IFileStabilityChecker
{
    Task<bool> WaitUntilStableAsync(
        string path, TimeSpan checkInterval, TimeSpan stablePeriod, CancellationToken cancellationToken);
}
