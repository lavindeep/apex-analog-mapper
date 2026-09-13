using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using ApexMapper.App.Views.Devices;
using ApexMapper.App.Views.Profiles;
using FluentAssertions;
using Xunit;

namespace ApexMapper.App.Tests.Views;

public sealed class ViewInitializationTests
{
    [Theory]
    [InlineData(typeof(DevicePickerView))]
    [InlineData(typeof(ProfileSelectorView))]
    public void Tab_view_constructs_its_list_and_toolbar(Type viewType)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var view = (UserControl)Activator.CreateInstance(viewType)!;
                var grid = view.Content.Should().BeOfType<Grid>().Subject;
                grid.Children.OfType<ListBox>().Should().ContainSingle();
                grid.Children.OfType<StackPanel>().Should().ContainSingle();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
