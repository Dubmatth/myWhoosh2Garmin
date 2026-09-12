using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MyWhoosh2Garmin.Watching;

public class ActivityFileWatcherService : BackgroundService
{
    private readonly string _watchFolder;
    private readonly IFileStabilityChecker _stabilityChecker;
    private readonly Func<string, CancellationToken, Task> _onActivityReady;
    private readonly ILogger<ActivityFileWatcherService> _logger;
    private readonly TimeSpan _pollInterval;
    private readonly HashSet<string> _seen = new();

    public ActivityFileWatcherService(
        string watchFolder,
        IFileStabilityChecker stabilityChecker,
        Func<string, CancellationToken, Task> onActivityReady,
        ILogger<ActivityFileWatcherService> logger,
        TimeSpan? pollInterval = null)
    {
        _watchFolder = watchFolder;
        _stabilityChecker = stabilityChecker;
        _onActivityReady = onActivityReady;
        _logger = logger;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(30);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Directory.Exists(_watchFolder))
        {
            _logger.LogError(
                "Dossier MyWhoosh introuvable : {Folder}. MyWhoosh a peut-être changé d'emplacement de stockage.",
                _watchFolder);
            return;
        }

        var channel = Channel.CreateUnbounded<string>();

        using var watcher = new FileSystemWatcher(_watchFolder, "*.fit")
        {
            EnableRaisingEvents = true,
        };
        watcher.Created += (_, e) => channel.Writer.TryWrite(e.FullPath);

        // Fallback: FileSystemWatcher can miss events on macOS, so also poll periodically.
        var pollingTask = PollForFilesAsync(channel.Writer, stoppingToken);

        try
        {
            await foreach (string path in channel.Reader.ReadAllAsync(stoppingToken))
            {
                if (!_seen.Add(path))
                    continue;

                bool stable = await _stabilityChecker.WaitUntilStableAsync(
                    path, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), stoppingToken);

                if (!stable)
                    continue;

                await _onActivityReady(path, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        await pollingTask;
    }

    private async Task PollForFilesAsync(ChannelWriter<string> writer, CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                foreach (string file in Directory.EnumerateFiles(_watchFolder, "*.fit"))
                    writer.TryWrite(file);

                await Task.Delay(_pollInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
    }
}
