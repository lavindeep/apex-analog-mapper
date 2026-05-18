using System.Windows.Controls;

namespace ApexMapper.App.Diagnostics.LogTail;

/// <summary>
/// Hosts the diagnostics log tail. All logic lives in
/// <see cref="LogTailViewModel"/>; the code-behind is the
/// <c>InitializeComponent</c> shell so the VM stays unit-testable.
/// </summary>
public partial class LogTailView : UserControl
{
    public LogTailView() => InitializeComponent();
}
