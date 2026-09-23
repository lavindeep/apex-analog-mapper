using System.Windows;
using System.Windows.Media;

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
}
