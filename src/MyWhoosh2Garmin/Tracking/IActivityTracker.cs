namespace MyWhoosh2Garmin.Tracking;

public interface IActivityTracker
{
    Task<bool> IsProcessedAsync(string fileHash);
    Task MarkProcessedAsync(string fileHash, string fileName, string? garminActivityId);
}
