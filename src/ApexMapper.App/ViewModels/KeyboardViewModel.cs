using System.ComponentModel;
using System.IO;
using ApexMapper.App.Model;
using ApexMapper.App.Mvvm;
using ApexMapper.Core.Sensors;
using ApexMapper.Windows.Devices;
using ApexMapper.Windows.Hid;

namespace ApexMapper.App.ViewModels;

/// <summary>One board in the keyboard list.</summary>
public sealed record BoardItem(KeyboardInfo Info, string Detail)
{
    public Guid Id => Info.ContainerId;

    public string Name => Info.Name;

    /// <summary>What a screen reader says for the item.</summary>
    public override string ToString() => $"{Name}, {Detail}";
}

/// <summary>
/// The keyboard card: the Apex Pro boards plugged in, the remembered choice, and the
/// firmware the chosen one reports. An unverified model gets the try-it flow: the
/// firmware request is the only thing sent until the user agrees to the risk, then a
/// check that its sensors move when a key is pressed, the learn step per key on the
/// calibration card, and a capture export for a GitHub issue. The firmware is never
/// asked while a session runs: the session's poller has the board open.
/// </summary>
public sealed class KeyboardViewModel : ObservableObject
{
    public const int CheckTimeoutMs = 10_000;

    public const string NewIssuePage = "https://github.com/lavindeep/apex-analog-mapper/issues/new";

    public const string ConsentText =
        "To read how far each key is pressed, the app sends this keyboard the request that works on the Apex Pro and " +
        "Apex Pro TKL. No one has checked how this model handles it. It will most likely answer the same way. If the " +
        "keyboard acts oddly afterwards, unplug it and plug it back in.";

    private readonly AppServices _services;
    private readonly Workspace _workspace;
    private readonly HashSet<Guid> _consented;
    private readonly ushort[] _raw = new ushort[SensorProtocol.SensorCount];
    private readonly ushort[] _filtered = new ushort[SensorProtocol.SensorCount];
    private IReadOnlyList<BoardItem> _boards = [];
    private BoardItem? _selected;
    private Guid? _remembered;
    private int _probe;
    private bool _reading;
    private CheckPhase _check;
    private long _checkSince;
    private LearnStep? _checkStep;
    private (ushort[] Raw, ushort[] Filtered)? _restCapture;
    private (ushort[] Raw, ushort[] Filtered)? _heldCapture;
    private Guid? _capturedFrom;
    private string? _checkText;
    private bool _exported;

    private enum CheckPhase
    {
        None,
        Rest,
        Pressing,
    }

    public KeyboardViewModel(AppServices services, Workspace workspace, Guid? remembered, IEnumerable<Guid>? consented)
    {
        _services = services;
        _workspace = workspace;
        _remembered = remembered;
        _consented = [.. consented ?? []];
        Consent = new Command(GiveConsent, () => NeedsConsent);
        Check = new Command(StartCheck, () => CanCheck);
        Export = new Command(ExportCapture, () => _selected is not null);
        OpenIssue = new Command(() => _services.Open(NewIssuePage));
        _services.Keyboards.Changed += list => _services.Post(() => OnKeyboards(list));
        _workspace.PropertyChanged += OnWorkspaceChanged;
    }

    public IReadOnlyList<BoardItem> Boards
    {
        get => _boards;
        private set => Set(ref _boards, value);
    }

    /// <summary>The chosen board while it is connected. A null from the view (its list was replaced) is ignored.</summary>
    public BoardItem? Selected
    {
        get => _selected;
        set
        {
            if (value is null || value.Id == _selected?.Id || _workspace.SessionActive)
            {
                return;
            }
            _remembered = value.Id;
            _workspace.Remember(_services.Settings, s => s with { Keyboard = value.Id });
            _selected = value;
            Raise(nameof(Selected));
            Raise(nameof(Summary));
            Raise(nameof(Missing));
            Sync();
        }
    }

    public bool CanChoose => !_workspace.SessionActive;

    /// <summary>The card header: the chosen board, or why there is none.</summary>
    public string Summary => _selected?.Name ?? (_boards.Count == 0 ? "Not plugged in" : "No keyboard chosen");

    /// <summary>What to do while no board is chosen, or null.</summary>
    public string? Missing => _selected is not null ? null
        : _boards.Count == 0 ? "Plug in your Apex Pro. It shows up here as soon as Windows finds it."
        : _remembered is not null ? "The keyboard chosen last time is not plugged in. Choose one of these."
        : "Choose your keyboard.";

    public string? FirmwareText => _reading ? "Reading the firmware..."
        : _workspace.Board is not { } board ? null
        : board.Firmware.Version is { } version ? $"Firmware {version}"
        : "The firmware could not be read. " + board.Firmware.Problem;

    /// <summary>
    /// The firmware is not one the app was tested on (H7). It warns and never refuses.
    /// An untested model says so in its own banner, so this stays quiet for one.
    /// </summary>
    public string? FirmwareWarning => _workspace.Board is { Verified: true, Firmware.Version: { } version, FirmwareVerified: false }
        ? $"The app has not been tested with firmware {version}. It will most likely work. If the keyboard's replies look wrong, " +
          "the analog keys switch to on and off and the status card says why."
        : null;

    /// <summary>The board is a model the app has not been verified on; the banner stays for as long as it is chosen (H6).</summary>
    public bool IsUnverified => _workspace.Board is { Verified: false };

    /// <summary>Try-it step 2: the firmware answered and the user has not yet agreed to the sensor request.</summary>
    public bool NeedsConsent => _workspace.Board is { Verified: false, Consented: false, Firmware.Version: not null } && !_workspace.SessionActive;

    public bool CanCheck => _workspace.Board is { Verified: false, CanReadSensors: true } && _check == CheckPhase.None && !_workspace.SessionActive;

    /// <summary>The sensor check is waiting for readings or a key press.</summary>
    public bool IsChecking => _check != CheckPhase.None;

    /// <summary>Try-it step 3: whether the sensors respond to a key press.</summary>
    public string? CheckText
    {
        get => _checkText;
        private set => Set(ref _checkText, value);
    }

    public Command Consent { get; }

    public Command Check { get; }

    public Command Export { get; }

    /// <summary>A capture was saved this run, so the card offers the page to attach it to.</summary>
    public bool Exported
    {
        get => _exported;
        private set => Set(ref _exported, value);
    }

    public Command OpenIssue { get; }

    /// <summary>Takes a newer keyboard list. Raised off the UI thread by discovery and posted here.</summary>
    public void OnKeyboards(IReadOnlyList<KeyboardInfo> list)
    {
        Boards = [.. list.Where(k => k.Known).Select(k => new BoardItem(k, DetailOf(k)))];
        if (_remembered is null && Boards.Count == 1)
        {
            _remembered = Boards[0].Id;
            _workspace.Remember(_services.Settings, s => s with { Keyboard = _remembered }, byUser: false);
        }
        _selected = Boards.FirstOrDefault(b => b.Id == _remembered);
        Raise(nameof(Selected));
        Raise(nameof(Summary));
        Raise(nameof(Missing));
        Sync();
    }

    /// <summary>Drives the sensor check. Called by the window's timer.</summary>
    public void Tick(long nowMs)
    {
        if (_check == CheckPhase.None)
        {
            return;
        }
        if (!_services.Sensor.TryRead(_raw, _filtered))
        {
            if (nowMs - _checkSince > CheckTimeoutMs)
            {
                EndCheck(_services.Sensor.Problem is { } problem
                    ? "The keyboard's sensors could not be read: " + problem
                    : "No readings came from the keyboard's sensors.");
            }
            return;
        }
        if (_check == CheckPhase.Rest)
        {
            // Enter may have clicked the button and still be on its way up.
            if (nowMs - _checkSince < CalibrationViewModel.SettleMs)
            {
                return;
            }
            _restCapture = ([.. _raw], [.. _filtered]);
            _checkStep = new LearnStep(_raw);
            _check = CheckPhase.Pressing;
            _checkSince = nowMs;
            CheckText = "Press any key all the way down and hold it.";
            return;
        }
        _checkStep!.Observe(_raw);
        if (_checkStep.AnythingMoved())
        {
            _heldCapture = ([.. _raw], [.. _filtered]);
            EndCheck("The sensors respond to key presses. Calibrate each key on the calibration card; the learn step finds its sensor.");
        }
        else if (nowMs - _checkSince > CheckTimeoutMs)
        {
            EndCheck("No sensor moved when a key was pressed, so this keyboard may not answer the way the tested ones do. " +
                "Please export a capture and attach it to a GitHub issue.");
        }
    }

    /// <summary>What a GitHub issue needs to add this board to the tested list (H6).</summary>
    internal CaptureExport BuildExport(KeyboardInfo info, Board? board)
    {
        var replies = new List<CapturedReply>();
        if (board?.Firmware.Reply is { } firmware)
        {
            replies.Add(new CapturedReply("firmware", 0, Convert.ToHexString(firmware)));
        }
        foreach (var (label, capture) in new[] { ("at rest", _restCapture), ("key held", _heldCapture) })
        {
            if (capture is { } c && _capturedFrom == info.ContainerId)
            {
                for (var group = 1; group <= SensorRequest.GroupCount; group++)
                {
                    var first = (group - 1) * SensorProtocol.SensorsPerGroup;
                    var reply = SensorProtocol.BuildGroupReply(
                        c.Raw.AsSpan(first, SensorProtocol.SensorsPerGroup),
                        c.Filtered.AsSpan(first, SensorProtocol.SensorsPerGroup));
                    replies.Add(new CapturedReply(label, group, Convert.ToHexString(reply)));
                }
            }
        }
        return new CaptureExport(
            _services.AppVersion,
            DateTimeOffset.Now,
            info.ProductId,
            info.Name,
            board?.Firmware.Version ?? board?.Firmware.Problem ?? "not read",
            info.ContainerId.ToString("D"),
            info.VendorInputLength,
            info.VendorOutputLength,
            replies);
    }

    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Workspace.Board):
                Raise(nameof(FirmwareText));
                Raise(nameof(FirmwareWarning));
                Raise(nameof(IsUnverified));
                RefreshTryIt();
                break;
            case nameof(Workspace.SessionActive):
                Raise(nameof(CanChoose));
                // The session takes the board, so the check would only time out.
                if (_workspace.SessionActive && IsChecking)
                {
                    EndCheck("The check stopped when mapping started. Run it again once mapping stops.");
                }
                RefreshTryIt();
                if (!_workspace.SessionActive)
                {
                    Sync();
                }
                break;
        }
    }

    /// <summary>
    /// Makes the workspace's board match the choice: asks a newly chosen or replugged
    /// board for its firmware, or clears the board when it is gone. Waits while a
    /// session runs; the session pauses and resumes on the keyboard by itself.
    /// </summary>
    private void Sync()
    {
        if (_workspace.SessionActive)
        {
            return;
        }
        if (_selected is null)
        {
            _probe++;
            SetReading(false);
            _workspace.Board = null;
        }
        else if (_workspace.Board?.Info != _selected.Info && !_reading)
        {
            _ = ReadFirmwareAsync(_selected);
        }
    }

    private async Task ReadFirmwareAsync(BoardItem item)
    {
        var probe = ++_probe;
        CancelCheck();
        // Cleared first: the live sensor stops before the request, so the two never share the board.
        _workspace.Board = null;
        SetReading(true);
        FirmwareReading reading;
        try
        {
            reading = await _services.ReadFirmware(item.Id);
        }
        catch (Exception e)
        {
            // Nothing thrown by the HID library may leave the card stuck on "reading".
            reading = new FirmwareReading(null, null, e.Message);
        }
        if (probe != _probe)
        {
            return;
        }
        SetReading(false);
        _workspace.Board = new Board(item.Info, reading, _consented.Contains(item.Id));
        _services.Log(reading.Version is { } version
            ? $"Keyboard {item.Name} ({item.Info.ProductId:X4}), firmware {version}."
            : $"Keyboard {item.Name} ({item.Info.ProductId:X4}): {reading.Problem}");
        // The board may have changed while the request was out.
        if (_selected?.Info != item.Info)
        {
            Sync();
        }
    }

    private void SetReading(bool reading)
    {
        if (Set(ref _reading, reading, nameof(FirmwareText)))
        {
            _workspace.ReadingFirmware = reading;
            RefreshTryIt();
        }
    }

    private void GiveConsent()
    {
        if (_workspace.Board is not { } board)
        {
            return;
        }
        _consented.Add(board.Id);
        // Added to what the file holds, which may know boards this run started without.
        _workspace.Remember(_services.Settings, s => s with { ConsentedKeyboards = [.. (s.ConsentedKeyboards ?? []).Append(board.Id).Distinct()] });
        _services.Log($"Consent given to read the sensors of unverified keyboard {board.Info.Name} ({board.Info.ProductId:X4}).");
        _workspace.Board = board with { Consented = true };
        StartCheck();
    }

    private void StartCheck()
    {
        _check = CheckPhase.Rest;
        _checkSince = _services.NowMs();
        _restCapture = null;
        _heldCapture = null;
        _capturedFrom = _workspace.Board?.Id;
        CheckText = "Reading the sensors. Keep your hands off the keyboard for a moment.";
        _services.Sensor.Want(this, true);
        RefreshTryIt();
    }

    private void EndCheck(string text)
    {
        CancelCheck();
        CheckText = text;
        _services.Log("Sensor check: " + text);
    }

    private void CancelCheck()
    {
        _check = CheckPhase.None;
        _checkStep = null;
        _services.Sensor.Want(this, false);
        RefreshTryIt();
    }

    private void ExportCapture()
    {
        // Taken before the dialog: an unplug handled inside its modal loop clears the choice and the board.
        var info = _selected!.Info;
        var board = _workspace.Board is { } b && b.Id == info.ContainerId ? b : null;
        var path = _services.Dialogs.AskSavePath($"apex-capture-{info.ProductId:x4}.json");
        if (path is null)
        {
            return;
        }
        try
        {
            File.WriteAllText(path, BuildExport(info, board).ToJson());
            CheckText = "Saved. Attach the file to a new issue on GitHub.";
            Exported = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            CheckText = "The capture could not be saved: " + e.Message;
        }
    }

    private void RefreshTryIt()
    {
        Raise(nameof(NeedsConsent));
        Raise(nameof(CanCheck));
        Raise(nameof(IsChecking));
        Consent.Refresh();
        Check.Refresh();
        Export.Refresh();
    }

    private static string DetailOf(KeyboardInfo info) => (KnownKeyboards.Find(info.ProductId)?.Verified, info.HasVendorInterface) switch
    {
        (_, false) => "Its sensor interface was not found",
        (true, _) => "Tested",
        _ => "Not tested yet: try it",
    };
}
