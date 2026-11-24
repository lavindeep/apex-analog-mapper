using System.Windows;
using System.Windows.Automation;
using System.Windows.Data;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace ApexMapper.App.Views;

internal static class VisualTree
{
    /// <summary>Every element under <paramref name="root"/>, the parts of templates included, depth first.</summary>
    public static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>
    /// WPF-UI's expander takes keyboard focus on a toggle with no name of its own, so a screen
    /// reader would say only "button". The toggle takes the row's name, such as "S, Calibrated".
    /// </summary>
    public static void NameExpanderToggle(CardExpander expander)
    {
        if (expander.Template?.FindName("ExpanderToggleButton", expander) is UIElement toggle)
        {
            BindingOperations.SetBinding(toggle, AutomationProperties.NameProperty, new Binding { Path = new PropertyPath(AutomationProperties.NameProperty), Source = expander });
        }
    }
}
