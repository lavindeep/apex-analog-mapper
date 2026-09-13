using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Markup;
using System.Xml.Linq;
using ApexMapper.App.Services;
using ApexMapper.App.ViewModels.Devices;

namespace ApexMapper.App.Tests.Views;

public sealed class DevicePickerViewTests
{
    [Fact]
    public void Connected_selected_keyboard_displays_its_name_after_refresh()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var selector = new Selector();
                var model = new DevicePickerViewModel(selector);
                var view = LoadView();
                view.DataContext = model;
                view.Measure(new Size(500, 500));
                view.Arrange(new Rect(0, 0, 500, 500));
                view.UpdateLayout();
                var keyboard = Descendants(view).OfType<ComboBox>().First();
                AssertKeyboard();
                model.RefreshCommand.Execute(null);
                view.UpdateLayout();
                AssertKeyboard();
                selector.AddSource();
                model.RefreshCommand.Execute(null);
                view.UpdateLayout();
                AssertKeyboard();
                view.Dispatcher.InvokeShutdown();

                void AssertKeyboard()
                {
                    Assert.NotNull(model.SelectedKeyboard);
                    Assert.Same(model.SelectedKeyboard, keyboard.SelectedItem);
                    Assert.Contains(Descendants(keyboard).OfType<TextBlock>(), text => text.Text == "Apex Pro TKL");
                    Assert.True(keyboard.IsEnabled);
                    var popup = (System.Windows.Controls.Primitives.Popup)keyboard.Template.FindName("PART_Popup", keyboard);
                    popup.Child.Measure(new Size(500, 500));
                    popup.Child.Arrange(new Rect(0, 0, 500, 500));
                    var item = (ComboBoxItem)keyboard.ItemContainerGenerator.ContainerFromItem(model.SelectedKeyboard);
                    Assert.True(item.IsEnabled);
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "WPF view test did not complete.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    // Use the real view and styles in a local resource scope. Creating an
    // Application here would alter dispatcher behavior in unrelated tests.
    private static UserControl LoadView()
    {
        var directory = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !System.IO.File.Exists(
            System.IO.Path.Combine(directory.FullName, "src/ApexMapper.App/App.xaml")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var appPath = System.IO.Path.Combine(directory.FullName, "src/ApexMapper.App");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var resources = XDocument.Load(System.IO.Path.Combine(appPath, "App.xaml"))
            .Root!.Element(presentation + "Application.Resources")!.Element(presentation + "ResourceDictionary")!;
        resources.Element(presentation + "ResourceDictionary.MergedDictionaries")!.Remove();
        var view = XDocument.Load(System.IO.Path.Combine(appPath, "Views/Devices/DevicePickerView.xaml")).Root!;
        view.Attribute(xaml + "Class")!.Remove();
        foreach (var click in view.Descendants().Attributes("Click").ToArray()) click.Remove();
        var localResources = view.Element(presentation + "UserControl.Resources")!;
        resources.Add(localResources.Elements().ToArray());
        localResources.ReplaceNodes(resources);
        return (UserControl)XamlReader.Parse(view.ToString());
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

    private sealed class Selector : IDeviceSelectorFacade
    {
        private readonly DeviceFacadeEntry _device = new(Guid.NewGuid(), "Apex Pro TKL", 0x1038, 0x161C,
            true, true, "keyboard-a", "source-a", "Keyboard input");
        private DeviceFacadeEntry? _secondSource;
        public Guid? PrimaryId => _device.Id;
        public IReadOnlyList<DeviceFacadeEntry> ListAll() => _secondSource is null ? new[] { _device } : new[] { _device, _secondSource };
        public void AddSource() => _secondSource = _device with { Id = Guid.NewGuid(), IsPrimary = false, DevicePath = "source-b" };
        public void SelectPrimary(Guid id) { }
        public void Refresh() => TopologyChanged?.Invoke(this, new(ListAll()));
        public event EventHandler<TopologyChangedEventArgs>? TopologyChanged;
    }

}
