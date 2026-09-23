using ApexMapper.App.Model;
using ApexMapper.App.Storage;
using ApexMapper.Core.Calibration;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Profiles;
using ApexMapper.Core.Sensors;
using ApexMapper.Windows.Devices;
using ApexMapper.Windows.Hid;
using ApexMapper.Windows.Input;
using ApexMapper.Windows.Output;
using ApexMapper.Windows.Session;

namespace ApexMapper.App.Tests.ViewModels;

/// <summary>A session the test drives by hand. It never installs a hook or connects a pad.</summary>
internal sealed class FakeSession : IMappingSession
{
    public event Action<SessionState>? StateChanged;

    public SessionState State { get; set; }

    public SessionEnd? LastEnd { get; set; }

    public bool RestartRequired { get; set; }

    public SessionEnd? NotStartable { get; set; }

    public SessionStatus? NextStatus { get; set; }

    public List<SessionRequest> Starts { get; } = [];

    public List<EndReason> Stops { get; } = [];

    public SessionEnd? WhyNotStartable(Guid keyboard) => NotStartable;

    public Task<SessionEnd?> StartAsync(SessionRequest request)
    {
        Starts.Add(request);
        Move(SessionState.Running);
        return Task.FromResult<SessionEnd?>(null);
    }

    public Task StopAsync(EndReason reason)
    {
        Stops.Add(reason);
        LastEnd = SessionEnd.For(reason);
        Move(SessionState.Idle);
        return Task.CompletedTask;
    }

    public SessionStatus Status() => NextStatus is { } status
        ? status with { State = State, LastEnd = LastEnd, RestartRequired = RestartRequired }
        : new SessionStatus(State, LastEnd, true, true, false, 0, null, false, 0, 0, RestartRequired);

    public void Move(SessionState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }
}

/// <summary>Sensor readings the test sets. <see cref="Reading"/> null means none are fresh.</summary>
internal sealed class FakeSensor : ILiveSensor
{
    public HashSet<object> Readers { get; } = [];

    public ushort[]? Reading { get; set; }

    public string? Problem { get; set; }

    public bool Wanted => Readers.Count > 0;

    /// <summary>Every sensor at a rest-like 850 counts, slots with no key at 5.</summary>
    public static ushort[] AtRest()
    {
        var raw = new ushort[SensorProtocol.SensorCount];
        Array.Fill(raw, (ushort)850);
        foreach (var absent in new[] { 27, 40, 65, 68, 69 })
        {
            raw[absent] = 5;
        }
        return raw;
    }

    public bool TryRead(Span<ushort> raw, Span<ushort> filtered = default)
    {
        if (Reading is not { } reading)
        {
            return false;
        }
        reading.CopyTo(raw);
        if (!filtered.IsEmpty)
        {
            reading.CopyTo(filtered);
        }
        return true;
    }

    public void Want(object reader, bool wanted)
    {
        if (wanted)
        {
            Readers.Add(reader);
        }
        else
        {
            Readers.Remove(reader);
        }
    }
}

internal sealed class FakeKeyEvents : IKeyEvents
{
    public Queue<RawKeyEvent> Pending { get; } = new();

    public Dictionary<nint, Guid> Containers { get; } = new();

    public void Press(ScanCode key, nint device = 1) => Pending.Enqueue(new RawKeyEvent(key, true, device, 0));

    public bool TryRead(out RawKeyEvent key) => Pending.TryDequeue(out key);

    public Guid? ContainerOf(nint device) => Containers.TryGetValue(device, out var container) ? container : null;
}

internal sealed class FakeDialogs : IDialogs
{
    public bool Answer { get; set; } = true;

    public string? SavePath { get; set; }

    public List<string> Asked { get; } = [];

    public Task<bool> ConfirmAsync(string title, string message, string confirm)
    {
        Asked.Add(title);
        return Task.FromResult(Answer);
    }

    public string? AskSavePath(string suggestedName) => SavePath;
}

/// <summary>
/// The services a card needs, with stores in a temp folder and fakes for everything
/// that would touch the keyboard, the driver or the screen. Posting runs inline.
/// </summary>
internal sealed class AppHarness : IDisposable
{
    public static readonly Guid Tkl = new("27373de1-4206-11f1-b9e4-14ac60fcc13e");
    public static readonly Guid Gen3 = new("33333333-4206-11f1-b9e4-14ac60fcc13e");
    public static readonly KeyboardInfo TklInfo = new(Tkl, 0x1614, "Apex Pro TKL", Known: true, HasVendorInterface: true);
    public static readonly KeyboardInfo Gen3Info = new(Gen3, 0x1642, "Apex Pro TKL Gen 3", Known: true, HasVendorInterface: true);
    public const string Firmware = "4.16.8";
    public const string Game = @"C:\Games\ForzaHorizon6\ForzaHorizon6.exe";

    private readonly TempDirectory _dir = new();

    public AppHarness()
    {
        Keyboards = new KeyboardDiscovery(() => [.. Boards]);
        Services = new AppServices
        {
            Session = Session,
            Keyboards = Keyboards,
            Sensor = Sensor,
            KeyEvents = Keys,
            Profiles = new ProfileStore(_dir.File("profiles")),
            Calibrations = new CalibrationStore(_dir.File("calibration")),
            Settings = new SettingsStore(_dir.File("settings.json")),
            Dialogs = Dialogs,
            Post = action => action(),
            ReadFirmware = board => { FirmwareRequests.Add(board); return Task.FromResult(FirmwareOf(board)); },
            ListWindows = () => { WindowScans++; return Windows; },
            DriverState = () => DriverCheck(),
            KeyName = key => Names.GetValueOrDefault(key, key.ToString()),
            Log = Log.Add,
            NowMs = () => Now,
            Open = Opened.Add,
            Restart = () => Restarts++,
        };
    }

    public static readonly Dictionary<ScanCode, string> Names = new()
    {
        [DefaultProfiles.Key.W] = "W",
        [DefaultProfiles.Key.A] = "A",
        [DefaultProfiles.Key.S] = "S",
        [DefaultProfiles.Key.D] = "D",
    };

    public FakeSession Session { get; } = new();

    public FakeSensor Sensor { get; } = new();

    public FakeKeyEvents Keys { get; } = new();

    public FakeDialogs Dialogs { get; } = new();

    public KeyboardDiscovery Keyboards { get; }

    public List<KeyboardInfo> Boards { get; } = [TklInfo];

    public List<Guid> FirmwareRequests { get; } = [];

    public Func<Guid, FirmwareReading> FirmwareOf { get; set; } = _ => new FirmwareReading(Firmware, null, null);

    public IReadOnlyList<GameWindow> Windows { get; set; } = [];

    public int WindowScans { get; private set; }

    public Func<DriverState> DriverCheck { get; set; } = () => DriverState.Running;

    public List<string> Log { get; } = [];

    public List<string> Opened { get; } = [];

    public long Now { get; set; } = 1_000_000;

    public int Restarts { get; private set; }

    public AppServices Services { get; }

    public Workspace Workspace { get; } = new();

    public AppSettings SavedSettings => Services.Settings.Load().Settings;

    /// <summary>A path in the test's temp folder.</summary>
    public string File(string name) => _dir.File(name);

    /// <summary>Saves a full calibration of W, S, A and D on the TKL, as the calibration card would.</summary>
    public void CalibrateForza()
    {
        var store = Services.Calibrations;
        store.Put(Tkl, Firmware, DefaultProfiles.Key.W, KeyCalibration.Create(850, 3900, 40, 16));
        store.Put(Tkl, Firmware, DefaultProfiles.Key.S, KeyCalibration.Create(850, 3900, 40, 30));
        store.Put(Tkl, Firmware, DefaultProfiles.Key.A, KeyCalibration.Create(850, 3900, 40, 29));
        store.Put(Tkl, Firmware, DefaultProfiles.Key.D, KeyCalibration.Create(850, 3900, 40, 31));
        store.PutSignature(Tkl, Firmware, 2, new GroupSignature(0x2000));
        store.PutSignature(Tkl, Firmware, 3, new GroupSignature(0x1000));
    }

    /// <summary>A chosen, readable board with its calibration loaded, as the keyboard and calibration cards leave the workspace.</summary>
    public void ChooseTkl()
    {
        Workspace.Board = new Board(TklInfo, new FirmwareReading(Firmware, null, null), Consented: false);
        Workspace.Calibration = Services.Calibrations.Load(Tkl, Firmware);
    }

    /// <summary>Everything Start needs: a calibrated board, the game and the Forza profile.</summary>
    public void MakeReady()
    {
        CalibrateForza();
        ChooseTkl();
        Workspace.GamePath = Game;
        Workspace.ActiveProfile = DefaultProfiles.Forza();
    }

    /// <summary>A reading with every key at rest except <paramref name="index"/>, which reads <paramref name="value"/>.</summary>
    public static ushort[] Holding(int index, ushort value)
    {
        var raw = FakeSensor.AtRest();
        raw[index] = value;
        return raw;
    }

    public void Dispose()
    {
        Keyboards.Dispose();
        _dir.Dispose();
    }
}
