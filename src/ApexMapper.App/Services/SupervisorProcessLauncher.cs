using System.Diagnostics;
using System.IO;

namespace ApexMapper.App.Services;

/// <summary>
/// Launches <c>ApexMapper.Supervisor.exe</c> from the directory the App itself
/// runs from, passing the shared session id. A missing executable is a loud
/// error, never a silent no-op: without a supervisor there is no virtual pad,
/// and pretending otherwise would leave the user with a mapper that looks
/// enabled but outputs nothing.
/// </summary>
public sealed class SupervisorProcessLauncher : ISupervisorProcessLauncher
{
    private const string ExecutableName = "ApexMapper.Supervisor.exe";

    private readonly string _sessionId;
    private readonly string _exePath;

    /// <param name="exePath">
    /// Test seam; production resolves the executable next to the App's own
    /// binaries (<see cref="AppContext.BaseDirectory"/>).
    /// </param>
    public SupervisorProcessLauncher(string sessionId, string? exePath = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        _sessionId = sessionId;
        _exePath = exePath ?? Path.Combine(AppContext.BaseDirectory, ExecutableName);
    }

    public string? EnsureRunning()
    {
        if (!File.Exists(_exePath))
        {
            return $"Supervisor executable not found at '{_exePath}'. Reinstall Apex Analog Mapper.";
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _exePath,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("--session");
            startInfo.ArgumentList.Add(_sessionId);

            // Deliberately not awaited or tracked: a duplicate launch defers to
            // the running instance via the per-session mutex and exits 0.
            using var process = Process.Start(startInfo);
            return process is null
                ? "The supervisor process failed to start."
                : null;
        }
        catch (Exception ex)
        {
            return $"Failed to launch the supervisor: {ex.Message}";
        }
    }
}
