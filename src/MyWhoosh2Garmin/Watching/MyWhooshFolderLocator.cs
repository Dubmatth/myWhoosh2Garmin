namespace MyWhoosh2Garmin.Watching;

public static class MyWhooshFolderLocator
{
    public static string GetActivitiesFolder()
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Seul macOS est supporté pour l'instant.");

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(
            home, "Library", "Containers", "com.whoosh.whooshgame", "Data",
            "Library", "Application Support", "Epic", "MyWhoosh", "Content", "Data");
    }
}
