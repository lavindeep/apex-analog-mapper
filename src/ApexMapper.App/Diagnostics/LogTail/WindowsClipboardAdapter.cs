namespace ApexMapper.App.Diagnostics.LogTail;

/// <summary>
/// Production <see cref="IClipboard"/> backed by <see cref="System.Windows.Clipboard"/>.
/// Tests should pass a fake instead so they don't take an STA lock on the
/// shared OS clipboard.
/// </summary>
public sealed class WindowsClipboardAdapter : IClipboard
{
    /// <inheritdoc />
    public void SetText(string text) => System.Windows.Clipboard.SetText(text);
}
