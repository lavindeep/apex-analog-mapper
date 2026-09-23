using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ApexMapper.App.Views;

/// <summary>
/// Reads a changing text out to a screen reader: why Start is off, what a key capture or
/// a calibration step waits for, how the sensor check went. Windows announces a live
/// region only when the app says it changed, so the element takes the text as its name
/// and raises the event once the text is laid out, after a hidden element has appeared.
/// </summary>
public static class Live
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Live), new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string? value) => element.SetValue(TextProperty, value);

    /// <summary>
    /// What announces <paramref name="text"/>: the element's own automation peer, or for an
    /// element with none, such as WPF-UI's InfoBar, the text block inside it that shows the
    /// text, made live too. Null until the text is laid out.
    /// </summary>
    public static AutomationPeer? PeerFor(UIElement element, string text)
    {
        if (UIElementAutomationPeer.CreatePeerForElement(element) is { } own)
        {
            return own;
        }
        if (VisualTree.Descendants(element).OfType<TextBlock>().FirstOrDefault(block => block.Text == text) is not { } shown)
        {
            return null;
        }
        AutomationProperties.SetLiveSetting(shown, AutomationLiveSetting.Polite);
        return UIElementAutomationPeer.CreatePeerForElement(shown);
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }
        var text = (string?)e.NewValue;
        AutomationProperties.SetName(element, text ?? "");
        AutomationProperties.SetLiveSetting(element, AutomationLiveSetting.Polite);
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        element.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            // A text that changed again before this ran is announced by its own callback.
            if (GetText(element) == text && element.IsVisible && AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged)
                && PeerFor(element, text) is { } peer)
            {
                peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            }
        });
    }
}
