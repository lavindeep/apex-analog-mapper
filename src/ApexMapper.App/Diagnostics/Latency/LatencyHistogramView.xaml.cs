using System.Windows.Controls;

namespace ApexMapper.App.Diagnostics.Latency;

/// <summary>
/// Hosts a single OxyPlot bar chart bound to a <see cref="LatencyHistogramViewModel"/>.
/// All chart wiring lives in the view-model; the code-behind is intentionally
/// the InitializeComponent shell so logic stays unit-testable.
/// </summary>
public partial class LatencyHistogramView : UserControl
{
    public LatencyHistogramView() => InitializeComponent();
}
