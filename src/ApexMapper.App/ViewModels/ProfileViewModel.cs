using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using ApexMapper.App.Model;
using ApexMapper.App.Mvvm;
using ApexMapper.App.Storage;
using ApexMapper.Core.Bindings;
using ApexMapper.Core.Calibration;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Profiles;
using ApexMapper.Core.Sensors;
using ApexMapper.Windows.Input;
using ApexMapper.Windows.Session;

namespace ApexMapper.App.ViewModels;

/// <summary>One profile in the list.</summary>
/// <param name="Problem">Why the file could not be used, or what was done about it; null when it loaded cleanly.</param>
public sealed record ProfileItem(string Id, string Name, string? Problem)
{
    /// <summary>What a screen reader says for the item.</summary>
    public override string ToString() => Name;
}

/// <summary>
/// The profile card. The chosen profile is the active one, the one Start maps, and the
/// editor works on a copy of it until Save. Saving the active profile while a session
/// runs stops the session when the saved text differs from what the session compiled;
/// resetting or deleting it does too (E8). Keys are captured from Raw Input, which gives
/// the exact scan code; a reserved key, one the hook reports differently, and one another
/// binding uses are refused (B6). The preview draws the chosen binding's response and,
/// from the live sensor, where its key is right now, in counts as well as depth (A18).
/// </summary>
public sealed class ProfileViewModel : ObservableObject
{
    public const double PreviewWidth = 200;
    public const double PreviewHeight = 120;
    private const ushort Escape = 0x01;

    /// <summary>
    /// Presses stamped this soon after a capture begins are not for it. Enter clicks a
    /// focused button on its key down, and that press reaches Raw Input at about the
    /// moment the capture starts.
    /// </summary>
    public static readonly long CaptureArmTicks = Stopwatch.Frequency / 10;

    private readonly AppServices _services;
    private readonly Workspace _workspace;
    private readonly ushort[] _raw = new ushort[SensorProtocol.SensorCount];
    private IReadOnlyList<ProfileItem> _profiles = [];
    private ProfileItem? _selected;
    private Profile? _saved;
    private string _name = "";
    private BindingRowViewModel? _selectedRow;
    private bool _dirty;
    private string? _message;
    private Capture? _capture;
    private string? _prompt;
    private bool _busy;
    private readonly HashSet<ScanCode> _held = [];
    private bool _isOpen;
    private PointCollection _curve = [];
    private string? _previewText;
    private Point? _marker;
    private string? _remembered;

    /// <summary>What the next key press is for, and the stamp a press must be at or after. <see cref="Row"/> is null while adding a binding.</summary>
    private sealed record Capture(BindingRowViewModel? Row, bool Negative, bool Axis, ScanCode? First = null, long From = 0);

    public ProfileViewModel(AppServices services, Workspace workspace, string? remembered)
    {
        _services = services;
        _workspace = workspace;
        Save = new Command(() => _ = SaveAsync(), () => CanEdit && _dirty);
        Discard = new Command(DiscardEdits, () => _dirty);
        New = new Command(CreateProfile, () => CanSwitch);
        Delete = new Command(() => _ = DeleteAsync(), () => _selected is { Id: not DefaultProfiles.ForzaId } && !_dirty && !_busy);
        Reset = new Command(() => _ = ResetAsync(), () => _selected is not null && !_dirty && !_busy);
        AddKey = new Command(() => BeginCapture(new Capture(null, false, false)), () => CanEdit);
        AddAxis = new Command(() => BeginCapture(new Capture(null, true, true)), () => CanEdit);
        Remove = new Command(RemoveRow, () => CanEdit && _selectedRow is not null);
        CaptureKey = new Command(() => BeginCapture(new Capture(_selectedRow, false, _selectedRow!.IsAxis)), () => CanEdit && _selectedRow is not null);
        CaptureNegativeKey = new Command(() => BeginCapture(new Capture(_selectedRow, true, true)), () => CanEdit && _selectedRow is { IsAxis: true });
        CancelCapture = new Command(() => EndCapture(null), () => _capture is not null);
        _workspace.PropertyChanged += OnWorkspaceChanged;
        _remembered = remembered;
        Reload(remembered ?? DefaultProfiles.ForzaId, byUser: false);
    }

    public IReadOnlyList<ProfileItem> Profiles
    {
        get => _profiles;
        private set => Set(ref _profiles, value);
    }

    /// <summary>The active profile. Switching waits for unsaved edits and for the session to stop. A null from the view is ignored.</summary>
    public ProfileItem? Selected
    {
        get => _selected;
        set
        {
            if (value is null || value.Id == _selected?.Id || !CanSwitch)
            {
                Raise(nameof(Selected));
                return;
            }
            Reload(value.Id);
        }
    }

    public bool CanSwitch => !_dirty && !_workspace.SessionActive && _capture is null && !_busy;

    /// <summary>Why the profile list is locked, or null.</summary>
    public string? SwitchHint => _workspace.SessionActive ? "Stop mapping to switch profiles."
        : _dirty ? "Save or discard your changes to switch profiles."
        : null;

    /// <summary>Why Delete is unavailable for the Forza profile.</summary>
    public string? DeleteHint => _selected?.Id == DefaultProfiles.ForzaId ? "The Forza profile cannot be deleted. Reset puts its defaults back." : null;

    /// <summary>The chosen profile loaded and can be edited: no key capture, and no save waiting for the session to stop.</summary>
    public bool CanEdit => _saved is not null && _capture is null && !_busy;

    /// <summary>Beside Save: what saving now does.</summary>
    public string? SaveNote => !_dirty ? null
        : _workspace.RunningProfileText is not null ? "Saving stops mapping. Press Start again to use the changes."
        : "Unsaved changes";

    public string? Problem => _selected?.Problem;

    public string Name
    {
        get => _name;
        set
        {
            if (Set(ref _name, value))
            {
                MarkDirty();
            }
        }
    }

    public ObservableCollection<BindingRowViewModel> Rows { get; } = [];

    public BindingRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (Set(ref _selectedRow, value))
            {
                RefreshCommands();
                UpdateCurve();
                WantSensor();
            }
        }
    }

    public bool IsDirty => _dirty;

    /// <summary>Why a save, delete or reset failed, or what is wrong with the edits.</summary>
    public string? Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    /// <summary>While capturing: which key to press, or why the last one was refused.</summary>
    public string? Prompt
    {
        get => _prompt;
        private set => Set(ref _prompt, value);
    }

    public bool IsCapturing => _capture is not null;

    /// <summary>The card is expanded; the preview reads the sensor only then.</summary>
    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (Set(ref _isOpen, value))
            {
                WantSensor();
            }
        }
    }

    /// <summary>The chosen binding's response, depth across and output up, in a <see cref="PreviewWidth"/> by <see cref="PreviewHeight"/> box.</summary>
    public PointCollection Curve
    {
        get => _curve;
        private set => Set(ref _curve, value);
    }

    /// <summary>Where the key is on the curve right now, or null when there is no reading.</summary>
    public Point? Marker
    {
        get => _marker;
        private set => Set(ref _marker, value);
    }

    public string? PreviewText
    {
        get => _previewText;
        private set => Set(ref _previewText, value);
    }

    public Command Save { get; }

    public Command Discard { get; }

    public Command New { get; }

    public Command Delete { get; }

    public Command Reset { get; }

    public Command AddKey { get; }

    public Command AddAxis { get; }

    public Command Remove { get; }

    /// <summary>Captures the key of the chosen binding, or the positive key of an axis.</summary>
    public Command CaptureKey { get; }

    public Command CaptureNegativeKey { get; }

    /// <summary>Stops waiting for a key: the card's button, and the window when it loses focus, since Raw Input keeps seeing keys typed elsewhere.</summary>
    public Command CancelCapture { get; }

    /// <summary>Forgets every key held, for when a key-up may have gone missing: see <see cref="MainViewModel.OnActivated"/>.</summary>
    public void ForgetHeldKeys() => _held.Clear();

    /// <summary>
    /// A key event from Raw Input, drained on the UI thread. Every event keeps track of
    /// which keys are down, so an auto-repeat is never taken for a new press. While
    /// capturing, the first new press stamped after the capture began is the key.
    /// </summary>
    public void OnKey(in RawKeyEvent key)
    {
        if (key.Device == 0)
        {
            return;
        }
        if (!key.Down)
        {
            _held.Remove(key.Code);
            return;
        }
        if (!_held.Add(key.Code) || _capture is not { } capture || key.Ticks < capture.From)
        {
            return;
        }
        if (key.Code.Value == Escape)
        {
            EndCapture(null);
            return;
        }
        if (Refusal(capture, key.Code) is { } refusal)
        {
            Prompt = refusal;
            return;
        }
        if (capture.Row is { } row)
        {
            if (capture.Negative)
            {
                row.NegativeKey = key.Code;
            }
            else
            {
                row.Key = key.Code;
            }
            EndCapture(null);
        }
        else if (capture.Axis && capture.First is null)
        {
            _capture = capture with { First = key.Code };
            Prompt = "Now press the key for the other direction: right, or up. Esc cancels.";
        }
        else
        {
            EndCapture(capture.Axis ? NewAxisRow(capture.First!.Value, key.Code) : NewKeyRow(key.Code));
        }
    }

    /// <summary>Moves the preview marker. Called by the window's timer.</summary>
    public void Tick()
    {
        if (!PreviewWanted || _selectedRow is not { ShapesOutput: true } row)
        {
            return;
        }
        if (!_services.Sensor.TryRead(_raw))
        {
            Marker = null;
            PreviewText = _services.Sensor.Problem ?? "Waiting for the keyboard's readings.";
            return;
        }
        var deepest = (Key: row.Key, Depth: -1f, Raw: 0, Calibrated: false);
        foreach (var key in row.IsAxis ? new[] { row.NegativeKey, row.Key } : [row.Key])
        {
            if (_workspace.Calibration?.Keys.TryGetValue(key, out var calibration) == true)
            {
                var raw = _raw[calibration.SensorIndex];
                var depth = Normalizer.Depth(calibration, raw);
                if (depth > deepest.Depth)
                {
                    deepest = (key, depth, raw, true);
                }
            }
            else if (deepest.Depth < 0f && _workspace.Map.TryGetSensorIndex(key, out var index))
            {
                deepest = (key, deepest.Depth, _raw[index], false);
            }
        }
        var name = _services.KeyName(deepest.Key);
        if (!deepest.Calibrated)
        {
            Marker = null;
            PreviewText = _workspace.Map.Supports(deepest.Key)
                ? $"{name}: {deepest.Raw} counts. Calibrate {name} to see its depth here."
                : $"{name} has no sensor, so it is on or off.";
            return;
        }
        var output = row.CurrentResponse.Map(deepest.Depth);
        Marker = new Point(deepest.Depth * PreviewWidth, (1 - output) * PreviewHeight);
        PreviewText = $"{name}: {deepest.Raw} counts, {deepest.Depth:P0} pressed, output {output:P0}";
    }

    private bool PreviewWanted => _isOpen && _selectedRow is { ShapesOutput: true } && !_workspace.SessionActive;

    private void WantSensor()
    {
        _services.Sensor.Want(this, PreviewWanted);
        if (!PreviewWanted)
        {
            Marker = null;
            PreviewText = null;
        }
    }

    /// <summary>Lists the profiles again and makes <paramref name="id"/> the active one, falling back to Forza.</summary>
    private void Reload(string id, bool byUser = true)
    {
        var entries = _services.Profiles.List();
        Profiles = [.. entries.Select(e => new ProfileItem(e.Id, e.Profile?.Name ?? e.Id, e.Problem))];
        var entry = entries.FirstOrDefault(e => e.Id == id) ?? entries[0];
        _selected = Profiles.First(p => p.Id == entry.Id);
        Raise(nameof(Selected));
        Raise(nameof(Problem));
        Raise(nameof(DeleteHint));
        if (entry.Id != _remembered)
        {
            _remembered = entry.Id;
            _workspace.Remember(_services.Settings, s => s with { ActiveProfile = entry.Id }, byUser);
        }
        _saved = entry.Profile;
        _workspace.ActiveProfile = entry.Profile;
        LoadEditor();
    }

    /// <summary>Puts the saved profile in the editor, axes first since they are what most profiles are for.</summary>
    private void LoadEditor()
    {
        if (_capture is not null)
        {
            EndCapture(null);
        }
        _name = _saved?.Name ?? "";
        Raise(nameof(Name));
        Rows.Clear();
        if (_saved is not null)
        {
            foreach (var axis in _saved.Axes)
            {
                Rows.Add(BindingRowViewModel.From(axis, _services.KeyName, OnRowChanged));
            }
            foreach (var key in _saved.Keys)
            {
                Rows.Add(BindingRowViewModel.From(key, _services.KeyName, OnRowChanged));
            }
        }
        SelectedRow = Rows.FirstOrDefault(r => r.ShapesOutput) ?? Rows.FirstOrDefault();
        SetDirty(false);
        Message = null;
    }

    private Profile BuildProfile() => new(
        _selected!.Id,
        _name.Trim(),
        [.. Rows.Where(r => !r.IsAxis).Select(r => r.ToKeyBinding())],
        [.. Rows.Where(r => r.IsAxis).Select(r => r.ToAxisBinding())]);

    internal async Task SaveAsync()
    {
        var profile = BuildProfile();
        if (EditProblem() is { } problem)
        {
            Message = problem;
            return;
        }
        if (profile.Validate() is { } invalid)
        {
            Message = invalid;
            return;
        }
        try
        {
            _services.Profiles.Save(profile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Message = "The profile could not be saved: " + e.Message;
            return;
        }
        _services.Log($"Profile \"{profile.Id}\" saved.");
        await StopIfTheRunningProfileChanged(ProfileJson.Serialize(profile));
        Reload(profile.Id);
    }

    internal async Task DeleteAsync()
    {
        var item = _selected!;
        if (!await _services.Dialogs.ConfirmAsync("Delete profile", $"Delete the profile \"{item.Name}\"? This cannot be undone.{StopsMapping()}", "Delete"))
        {
            return;
        }
        await StopIfTheRunningProfileChanged(null);
        try
        {
            _services.Profiles.Delete(item.Id);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Message = "The profile could not be deleted: " + e.Message;
            return;
        }
        _services.Log($"Profile \"{item.Id}\" deleted.");
        Reload(DefaultProfiles.ForzaId);
    }

    internal async Task ResetAsync()
    {
        var item = _selected!;
        if (!await _services.Dialogs.ConfirmAsync("Reset profile", $"Replace the bindings of \"{item.Name}\" with the Forza defaults?{StopsMapping()}", "Reset"))
        {
            return;
        }
        Profile reset;
        try
        {
            (reset, _) = _services.Profiles.Reset(item.Id);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Message = "The profile could not be reset: " + e.Message;
            return;
        }
        _services.Log($"Profile \"{item.Id}\" reset.");
        await StopIfTheRunningProfileChanged(ProfileJson.Serialize(reset));
        Reload(item.Id);
    }

    /// <summary>
    /// E8: a running session stops when the active profile it compiled is no longer what
    /// is saved. Null text means the profile is going away. The editor holds still until
    /// the session has stopped, since the reload after would drop anything typed meanwhile.
    /// </summary>
    private async Task StopIfTheRunningProfileChanged(string? savedText)
    {
        if (_workspace.RunningProfileText is not { } running || savedText == running)
        {
            return;
        }
        SetBusy(true);
        try
        {
            await _services.Session.StopAsync(EndReason.ProfileEdited);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private string StopsMapping() => _workspace.RunningProfileText is null ? "" : " This stops mapping.";

    /// <summary>What the edits would save wrongly, in the card's words. The profile's own check backs this up.</summary>
    private string? EditProblem()
    {
        if (Rows.GroupBy(r => r.Target).FirstOrDefault(g => g.Count() > 1) is { } shared)
        {
            return $"{BindingRowViewModel.NameOf(shared.Key)} is used by more than one binding. Choose another for one of them.";
        }
        if (Rows.FirstOrDefault(r => r.ShapesOutput && r.Deadzone >= r.Saturation) is { } row)
        {
            return $"{row.KeysText}: the dead zone must be less than Full output at.";
        }
        return null;
    }

    /// <summary>Why a key cannot be taken for this capture, or null.</summary>
    private string? Refusal(Capture capture, ScanCode key)
    {
        const string Again = " Press another key, or Esc to cancel.";
        var name = _services.KeyName(key);
        if (key.IsReserved)
        {
            return "Ctrl, Alt, Windows and F12 cannot be mapped." + Again;
        }
        if (!key.IsBindable)
        {
            return $"{name} cannot be mapped: the app cannot block it reliably." + Again;
        }
        var otherDirection = capture.Row is { IsAxis: true } axis ? (capture.Negative ? axis.Key : axis.NegativeKey) : capture.First;
        if (otherDirection == key)
        {
            return "The two directions need different keys." + Again;
        }
        if (Rows.FirstOrDefault(r => r != capture.Row && r.Uses(key)) is { } other)
        {
            return $"{name} is already bound to {other.TargetText}." + Again;
        }
        return null;
    }

    private void CreateProfile()
    {
        var taken = _profiles.Select(p => p.Id).ToHashSet();
        var number = 2;
        while (taken.Contains($"profile-{number}"))
        {
            number++;
        }
        var profile = DefaultProfiles.Forza() with { Id = $"profile-{number}", Name = $"Profile {number}" };
        try
        {
            _services.Profiles.Save(profile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Message = "The profile could not be created: " + e.Message;
            return;
        }
        Reload(profile.Id);
    }

    private void DiscardEdits() => LoadEditor();

    private void RemoveRow()
    {
        var index = Rows.IndexOf(_selectedRow!);
        Rows.RemoveAt(index);
        SelectedRow = Rows.Count == 0 ? null : Rows[Math.Min(index, Rows.Count - 1)];
        MarkDirty();
    }

    private BindingRowViewModel NewKeyRow(ScanCode key)
    {
        var used = Rows.Select(r => r.Target).ToHashSet();
        var target = BindingRowViewModel.KeyTargets.Select(t => t.Value).FirstOrDefault(t => !used.Contains(t));
        return BindingRowViewModel.From(new KeyBinding(key, target, Core.Response.Response.Linear, 0f, 0f), _services.KeyName, OnRowChanged);
    }

    private BindingRowViewModel NewAxisRow(ScanCode negative, ScanCode positive)
    {
        var used = Rows.Select(r => r.Target).ToHashSet();
        var target = BindingRowViewModel.AxisTargets.Select(t => t.Value).FirstOrDefault(t => !used.Contains(t), PadTarget.LeftStickX);
        return BindingRowViewModel.From(
            new AxisBinding(negative, positive, target, Core.Response.Response.Linear, 0f, 0f, ConflictRule.LastInputWins, AxisMode.Position, AxisBinding.DefaultRateMs, AxisBinding.DefaultReturnMs),
            _services.KeyName,
            OnRowChanged);
    }

    private void BeginCapture(Capture capture)
    {
        _capture = capture with { From = _services.Timestamp() + CaptureArmTicks };
        Message = null;
        Prompt = capture switch
        {
            { Row: null, Axis: true } => "Press the key for one direction: left, or down. Esc cancels.",
            { Row: null } => "Press the key to add. Esc cancels.",
            { Negative: true } => "Press the key for left, or down. Esc cancels.",
            { Axis: true } => "Press the key for right, or up. Esc cancels.",
            _ => "Press the new key. Esc cancels.",
        };
        Raise(nameof(IsCapturing));
        RefreshCommands();
    }

    private void EndCapture(BindingRowViewModel? added)
    {
        _capture = null;
        Prompt = null;
        Raise(nameof(IsCapturing));
        if (added is not null)
        {
            Rows.Add(added);
            SelectedRow = added;
            MarkDirty();
        }
        RefreshCommands();
    }

    private void OnRowChanged(BindingRowViewModel row)
    {
        MarkDirty();
        if (row == _selectedRow)
        {
            UpdateCurve();
            WantSensor();
        }
    }

    private void MarkDirty() => SetDirty(true);

    private void SetDirty(bool dirty)
    {
        if (Set(ref _dirty, dirty, nameof(IsDirty)))
        {
            Raise(nameof(CanSwitch));
            Raise(nameof(SwitchHint));
            Raise(nameof(SaveNote));
        }
        RefreshCommands();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Raise(nameof(CanSwitch));
        RefreshCommands();
    }

    private void UpdateCurve()
    {
        var points = new PointCollection();
        if (_selectedRow is { ShapesOutput: true } row)
        {
            var response = row.CurrentResponse;
            const int Steps = 50;
            for (var i = 0; i <= Steps; i++)
            {
                var depth = i / (float)Steps;
                points.Add(new Point(depth * PreviewWidth, (1 - response.Map(depth)) * PreviewHeight));
            }
        }
        points.Freeze();
        Curve = points;
    }

    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Workspace.SessionActive))
        {
            Raise(nameof(CanSwitch));
            Raise(nameof(SwitchHint));
            RefreshCommands();
            WantSensor();
        }
        else if (e.PropertyName == nameof(Workspace.RunningProfileText))
        {
            Raise(nameof(SaveNote));
        }
    }

    private void RefreshCommands()
    {
        Raise(nameof(CanEdit));
        foreach (var command in new[] { Save, Discard, New, Delete, Reset, AddKey, AddAxis, Remove, CaptureKey, CaptureNegativeKey, CancelCapture })
        {
            command.Refresh();
        }
    }
}
