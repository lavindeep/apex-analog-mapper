using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using ApexMapper.App.ViewModels.Devices;
using ApexMapper.Core.Keys;
using ApexMapper.Persistence.Devices;

namespace ApexMapper.App.Tests.Views;

public sealed class SensorCalibrationWindowTests
{
    [Fact]
    public void Populated_calibration_layout_displays_updated_sensor_travel()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var row = new SensorCalibrationRow(KeyId.FromScanCode(0x11),
                    new KeyCalibration(KeyId.FromScanCode(0x11), 3000, 1000, 15), (_, _) => { });
                row.Update(true, 2200);
                var window = LoadWindow();
                window.DataContext = new { Rows = new[] { row }, Error = (string?)null };
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(720, 430));
                content.Arrange(new Rect(0, 0, 720, 430));
                content.UpdateLayout();

                var progress = Assert.Single(Descendants(content).OfType<ProgressBar>());
                Assert.Equal(40d, progress.Value);
                row.Update(true, 1400);
                content.UpdateLayout();
                Assert.Equal(80d, progress.Value);
                row.Update(false, 1400);
                content.UpdateLayout();
                Assert.Equal(0d, progress.Value);
                Assert.False(progress.IsEnabled);
                window.Close();
            }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Calibration layout test did not complete.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    // Parse the real window markup without its hardware-backed code-behind.
    // No Application instance or visible window is needed to create row bindings.
    private static Window LoadWindow()
    {
        const string relativePath = "src/ApexMapper.App/Views/Devices/SensorCalibrationWindow.xaml";
        var directory = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !System.IO.File.Exists(System.IO.Path.Combine(directory.FullName, relativePath)))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var root = XDocument.Load(System.IO.Path.Combine(directory.FullName, relativePath)).Root!;
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        root.Attribute(xaml + "Class")!.Remove();
        foreach (var click in root.Descendants().Attributes("Click").ToArray()) click.Remove();
        return (Window)XamlReader.Parse(root.ToString());
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
