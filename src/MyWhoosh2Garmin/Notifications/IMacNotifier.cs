namespace MyWhoosh2Garmin.Notifications;

public interface IMacNotifier
{
    Task NotifyAsync(string title, string message);
}
