using ApexMapper.App.Model;
using ApexMapper.App.Mvvm;
using ApexMapper.Windows.Output;

namespace ApexMapper.App.ViewModels;

/// <summary>
/// The setup card: whether the ViGEmBus driver is installed and running, with a button
/// to its official release page when it is missing, checked again whenever the window
/// comes back to the front. The app never downloads or runs the installer (O11).
/// </summary>
public sealed class SetupViewModel : ObservableObject
{
    public const string DriverPage = "https://github.com/nefarius/ViGEmBus/releases";
    public const string DriverVersion = "1.22.0";

    private readonly AppServices _services;
    private DriverState _driver;

    public SetupViewModel(AppServices services)
    {
        _services = services;
        OpenDriverPage = new Command(() => _services.Open(DriverPage));
        OpenDataFolder = new Command(() => _services.Open(_services.DataFolder));
        Recheck();
    }

    public string DriverText => _driver switch
    {
        DriverState.Running => "ViGEmBus is installed and running.",
        DriverState.NotStarted => "ViGEmBus is installed but not running. Restart the PC to finish installing it.",
        DriverState.Missing => $"ViGEmBus is not installed. The mapper needs it to create the virtual controller. Install version {DriverVersion} from its release page, then come back to this window.",
        _ => "Windows would not say whether ViGEmBus is installed. Start will find out.",
    };

    public bool DriverMissing => _driver == DriverState.Missing;

    public bool DriverReady => _driver == DriverState.Running;

    public string VersionText => $"Apex Analog Mapper {_services.AppVersion}";

    public Command OpenDriverPage { get; }

    public Command OpenDataFolder { get; }

    /// <summary>Asks the service manager again; the window calls this when it is activated.</summary>
    public void Recheck()
    {
        DriverState state;
        try
        {
            state = _services.DriverState();
        }
        catch (Exception e)
        {
            _services.Log("Driver check failed: " + e.Message);
            state = DriverState.Unknown;
        }
        if (Set(ref _driver, state, nameof(DriverText)))
        {
            Raise(nameof(DriverMissing));
            Raise(nameof(DriverReady));
        }
    }
}
