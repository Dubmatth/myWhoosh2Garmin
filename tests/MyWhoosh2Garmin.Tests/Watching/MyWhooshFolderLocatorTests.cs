using MyWhoosh2Garmin.Watching;

namespace MyWhoosh2Garmin.Tests.Watching;

public class MyWhooshFolderLocatorTests
{
    [Fact]
    public void Returns_path_under_the_user_home_directory()
    {
        string path = MyWhooshFolderLocator.GetActivitiesFolder();

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.StartsWith(home, path);
        Assert.EndsWith(Path.Combine("MyWhoosh", "Content", "Data"), path);
    }
}
