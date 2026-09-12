using MyWhoosh2Garmin.Processes;

namespace MyWhoosh2Garmin.Notifications;

public class OsascriptNotifier : IMacNotifier
{
    private readonly IProcessRunner _processRunner;

    public OsascriptNotifier(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public Task NotifyAsync(string title, string message)
    {
        string script = $"display notification \"{Escape(message)}\" with title \"{Escape(title)}\"";
        return _processRunner.RunAsync("osascript", ["-e", script]);
    }

    private static string Escape(string value) => value.Replace("\"", "\\\"");
}
