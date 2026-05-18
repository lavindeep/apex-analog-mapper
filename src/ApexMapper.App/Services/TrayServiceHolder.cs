namespace ApexMapper.App.Services;

/// <summary>
/// Carrier singleton used to pass the production <see cref="TrayService"/>
/// (constructed after WPF resources are loaded) into the DI container before
/// any consumer resolves <see cref="ITrayService"/>.
///
/// App.xaml.cs registers this holder with the live <see cref="TrayService"/>
/// value prior to building the <see cref="Microsoft.Extensions.DependencyInjection.IServiceProvider"/>.
/// The <see cref="AppCompositionRoot"/> factory for <see cref="ITrayService"/>
/// then reads <see cref="Value"/> from it — falling back to
/// <see cref="StubTrayService"/> when running in the test harness.
/// </summary>
public sealed class TrayServiceHolder
{
    public ITrayService? Value { get; set; }
}
