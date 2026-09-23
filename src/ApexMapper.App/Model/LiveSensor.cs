using System.ComponentModel;
using System.Diagnostics;
using ApexMapper.Core.Sensors;
using ApexMapper.Windows.Hid;
using ApexMapper.Windows.Session;

namespace ApexMapper.App.Model;

/// <summary>Sensor readings outside a session, for calibration, the try-it check and the response preview.</summary>
public interface ILiveSensor
{
    /// <summary>
    /// Copies the newest reading of all 70 sensors, and their filtered copies when asked.
    /// False when there is no fresh reading of every group.
    /// </summary>
    bool TryRead(Span<ushort> raw, Span<ushort> filtered = default);

    /// <summary>Why there are no readings while someone wants them, or null.</summary>
    string? Problem { get; }

    /// <summary>Says whether a reader wants readings. The keyboard is read only while someone does.</summary>
    void Want(object reader, bool wanted);
}

/// <summary>
/// Reads every sensor group of the chosen keyboard while a card wants readings, the
/// board may be read (<see cref="Board.CanReadSensors"/>) and no session is up. Two
/// pollers on one board would read each other's replies, so this one stops the moment
/// the workspace says a session is starting, before the session opens the board. It
/// polls without signatures: a wrong stored signature would otherwise fail every
/// reading, and calibration, which records new ones, could never run. The canary then
/// runs every five cycles. UI thread only.
/// </summary>
public sealed class LiveSensor : ILiveSensor, IDisposable
{
    private static readonly int[] AllGroups = [.. Enumerable.Range(1, SensorRequest.GroupCount)];

    private readonly Workspace _workspace;
    private readonly Func<Guid, SensorSnapshot, PollerConfig, SensorPoller> _create;
    private readonly HashSet<object> _readers = [];
    private SensorPoller? _poller;
    private SensorSnapshot? _snapshot;
    private Guid _board;

    public LiveSensor(Workspace workspace, Func<Guid, SensorSnapshot, PollerConfig, SensorPoller>? create = null)
    {
        _workspace = workspace;
        _create = create ?? SensorPoller.ForKeyboard;
        _workspace.PropertyChanged += OnWorkspaceChanged;
    }

    /// <summary>For tests: a poller is running.</summary>
    internal bool IsPolling => _poller is not null;

    public string? Problem => _poller is { State: not PollerState.Running and not PollerState.Starting, FaultReason: { } reason } ? reason : null;

    public void Want(object reader, bool wanted)
    {
        if (wanted ? _readers.Add(reader) : _readers.Remove(reader))
        {
            Update();
        }
    }

    public bool TryRead(Span<ushort> raw, Span<ushort> filtered = default)
    {
        if (_snapshot is not { } snapshot)
        {
            return false;
        }
        for (var attempt = 0; attempt < SensorSnapshot.MaxReadAttempts; attempt++)
        {
            if (!snapshot.TryBeginRead(out var generation))
            {
                continue;
            }
            var usable = snapshot.IsFresh(Stopwatch.GetTimestamp()) && EveryGroupRead(snapshot);
            snapshot.Raw.CopyTo(raw);
            if (!filtered.IsEmpty)
            {
                snapshot.Filtered.CopyTo(filtered);
            }
            if (snapshot.EndRead(generation))
            {
                return usable;
            }
        }
        return false;
    }

    public void Dispose()
    {
        _workspace.PropertyChanged -= OnWorkspaceChanged;
        StopPolling();
    }

    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Workspace.Board) or nameof(Workspace.Session))
        {
            Update();
        }
    }

    private void Update()
    {
        var board = _workspace.Board;
        var wanted = _readers.Count > 0 && board is { CanReadSensors: true } && _workspace.Session == SessionState.Idle;
        if (_poller is not null && (!wanted || board!.Id != _board))
        {
            StopPolling();
        }
        if (wanted && _poller is null)
        {
            _board = board!.Id;
            _snapshot = new SensorSnapshot();
            _poller = _create(_board, _snapshot, PollerConfig.For(AllGroups));
            _poller.Start();
        }
    }

    private void StopPolling()
    {
        _poller?.Dispose();
        _poller = null;
        _snapshot = null;
    }

    private static bool EveryGroupRead(SensorSnapshot snapshot)
    {
        for (var group = 0; group < SensorRequest.GroupCount; group++)
        {
            if (!snapshot.WasRead(group * SensorProtocol.SensorsPerGroup))
            {
                return false;
            }
        }
        return true;
    }
}
