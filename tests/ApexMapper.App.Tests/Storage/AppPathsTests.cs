using System.IO;
using ApexMapper.App.Storage;
using Xunit;

namespace ApexMapper.App.Tests.Storage;

public class AppPathsTests
{
    [Fact]
    public void Everything_lives_in_one_apex_analog_mapper_folder_in_app_data_and_never_in_the_old_one()
    {
        var paths = AppPaths.ForCurrentUser();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        Assert.Equal(Path.Combine(appData, "ApexAnalogMapper"), paths.Root);
        foreach (var path in new[] { paths.Profiles, paths.Calibration, paths.Settings, paths.Log })
        {
            Assert.StartsWith(paths.Root + Path.DirectorySeparatorChar, path);
            Assert.DoesNotContain("ApexMapper", path.Split(Path.DirectorySeparatorChar));
        }
        Assert.Equal(["profiles", "calibration", "settings.json", "log.txt"], new[] { paths.Profiles, paths.Calibration, paths.Settings, paths.Log }.Select(Path.GetFileName));
    }
}
