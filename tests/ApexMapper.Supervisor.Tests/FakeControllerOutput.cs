using ApexMapper.Core.Pipeline;
using ApexMapper.Output;

namespace ApexMapper.Supervisor.Tests;

/// <summary>
/// Recording pad: captures every call in order (thread-safe) and can be
/// configured to throw from Connect, Submit, Zero, or Disconnect. A throwing
/// call is still recorded first, so ordering assertions see the attempt.
/// </summary>
internal sealed class FakeControllerOutput : IControllerOutput
{
    private readonly object _lock = new();
    private readonly List<string> _calls = new();
    private readonly List<VirtualPadState> _submitted = new();

    public Exception? ThrowOnConnect { get; set; }
    public Exception? ThrowOnSubmit { get; set; }
    public Exception? ThrowOnZero { get; set; }
    public Exception? ThrowOnDisconnect { get; set; }

    /// <summary>Set when Zero is entered, so a test can hold a teardown open
    /// (via <see cref="ZeroGate"/>) while delivering concurrent frames.</summary>
    public ManualResetEventSlim? ZeroEntered { get; set; }

    /// <summary>When assigned, Zero blocks until signalled. The wait is bounded
    /// so a forgotten gate fails the test instead of wedging the run.</summary>
    public ManualResetEventSlim? ZeroGate { get; set; }

    public bool IsConnected { get; private set; }

    public string? LastError => null;

    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_lock)
            {
                return _calls.ToArray();
            }
        }
    }

    public IReadOnlyList<VirtualPadState> Submitted
    {
        get
        {
            lock (_lock)
            {
                return _submitted.ToArray();
            }
        }
    }

    public void Connect()
    {
        lock (_lock)
        {
            _calls.Add("connect");
            if (ThrowOnConnect is not null)
            {
                throw ThrowOnConnect;
            }

            IsConnected = true;
        }
    }

    public void Submit(in VirtualPadState state)
    {
        lock (_lock)
        {
            _calls.Add("submit");
            _submitted.Add(state);
            if (ThrowOnSubmit is not null)
            {
                throw ThrowOnSubmit;
            }
        }
    }

    public void Zero()
    {
        // Signal and park outside the recording lock so Calls stays readable
        // while a gated teardown is held open.
        ZeroEntered?.Set();
        ZeroGate?.Wait(TimeSpan.FromSeconds(5));
        lock (_lock)
        {
            _calls.Add("zero");
            if (ThrowOnZero is not null)
            {
                throw ThrowOnZero;
            }
        }
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            _calls.Add("disconnect");
            if (ThrowOnDisconnect is not null)
            {
                throw ThrowOnDisconnect;
            }

            IsConnected = false;
        }
    }
}
