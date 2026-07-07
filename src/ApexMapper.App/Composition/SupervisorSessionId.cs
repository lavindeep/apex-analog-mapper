using System.Diagnostics;
using System.Globalization;

namespace ApexMapper.App.Composition;

/// <summary>
/// The identifier shared by both ends of the supervisor IPC. The tray passes it
/// to the spawned supervisor process (<c>--session &lt;id&gt;</c>) and derives
/// the pipe name from the same value through <c>SupervisorClient</c>, so the
/// two ends can only ever rendezvous on the same pipe. The value is the current
/// Windows logon session id: each logon session gets its own supervisor,
/// single-instance mutex, and virtual pad.
/// </summary>
public sealed record SupervisorSessionId(string Value)
{
    public static SupervisorSessionId Current() =>
        new(Process.GetCurrentProcess().SessionId.ToString(CultureInfo.InvariantCulture));
}
