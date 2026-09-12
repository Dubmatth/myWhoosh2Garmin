namespace MyWhoosh2Garmin.Watching;

public class FileStabilityChecker : IFileStabilityChecker
{
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, long> _getFileSize;

    public FileStabilityChecker(TimeProvider? timeProvider = null, Func<string, long>? getFileSize = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _getFileSize = getFileSize ?? (path => new FileInfo(path).Length);
    }

    public async Task<bool> WaitUntilStableAsync(
        string path, TimeSpan checkInterval, TimeSpan stablePeriod, CancellationToken cancellationToken)
    {
        long lastSize = _getFileSize(path);
        var stableSince = _timeProvider.GetUtcNow();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(checkInterval, _timeProvider, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            long currentSize = _getFileSize(path);
            if (currentSize != lastSize)
            {
                lastSize = currentSize;
                stableSince = _timeProvider.GetUtcNow();
                continue;
            }

            if (_timeProvider.GetUtcNow() - stableSince >= stablePeriod)
                return true;
        }

        return false;
    }
}
