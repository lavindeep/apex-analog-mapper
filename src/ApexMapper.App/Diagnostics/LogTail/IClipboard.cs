namespace ApexMapper.App.Diagnostics.LogTail;

/// <summary>
/// Indirection over <see cref="System.Windows.Clipboard"/> so the log-tail
/// view-model can be exercised in unit tests without touching the OS
/// clipboard (which requires an STA WPF dispatcher).
/// </summary>
public interface IClipboard
{
    /// <summary>Copies <paramref name="text"/> to the clipboard.</summary>
    void SetText(string text);
}
