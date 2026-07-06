using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ApexMapper.Output.Detection;

/// <summary>
/// Windows implementation of <see cref="IProcessEnumerator"/> over the
/// Toolhelp32 snapshot API. One <c>CreateToolhelp32Snapshot</c> sweep yields
/// every process's pid, parent pid, and image name without opening a handle per
/// process, which is what the anti-cheat and Steam scans need.
///
/// <see cref="ProcessSnapshot.ExecutablePath"/> is ALWAYS <c>null</c> here: the
/// scans identify processes by image name and lineage only, and the foreground
/// executable's path arrives via <see cref="ForegroundContext"/>. Querying a
/// path would mean opening a handle to every process on the machine — ambient
/// behavior this project deliberately avoids.
///
/// A failed or partial sweep THROWS rather than returning a short list: the
/// anti-cheat scan treats an enumerator failure as "cannot attest absence" and
/// fails closed, so a silently truncated snapshot would be a fail-open hole.
///
/// POLICY: <see cref="ProcessSnapshot.EnvironmentVariables"/> is ALWAYS an empty
/// dictionary for other processes. Reading a foreign process's environment block
/// requires ReadProcessMemory against that process, which violates the
/// no-game-memory-access policy. We deliberately do not attempt it.
///
/// The type constructs on any OS (so composition roots stay testable), but every
/// enumeration member throws <see cref="PlatformNotSupportedException"/> off
/// Windows rather than faulting inside a P/Invoke.
/// </summary>
public sealed class WindowsProcessEnumerator : IProcessEnumerator
{
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const int ERROR_NO_MORE_FILES = 18;
    private static readonly IntPtr InvalidHandleValue = new(-1);
    private static readonly IReadOnlyDictionary<string, string> EmptyEnvironment =
        new Dictionary<string, string>();

    public IReadOnlyList<ProcessSnapshot> Enumerate()
    {
        EnsureWindows();

        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == InvalidHandleValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateToolhelp32Snapshot failed.");
        }

        var results = new List<ProcessSnapshot>();
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snapshot, ref entry))
            {
                // A process snapshot always contains at least this process, so a
                // FALSE here is an error, never a legitimately empty sweep.
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Process32First failed.");
            }

            do
            {
                var pid = unchecked((int)entry.th32ProcessID);
                var parentPid = unchecked((int)entry.th32ParentProcessID);
                results.Add(new ProcessSnapshot(pid, parentPid, entry.szExeFile, null, EmptyEnvironment));
            }
            while (Process32Next(snapshot, ref entry));

            var lastError = Marshal.GetLastWin32Error();
            if (lastError != ERROR_NO_MORE_FILES)
            {
                // The walk ended early: a partial list could hide a running
                // anti-cheat service, so surface it instead of returning less.
                throw new Win32Exception(lastError, "Process32Next failed before the end of the snapshot.");
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return results;
    }

    public ProcessSnapshot? GetById(int processId)
    {
        EnsureWindows();

        foreach (var process in Enumerate())
        {
            if (process.ProcessId == processId)
            {
                return process;
            }
        }

        return null;
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "WindowsProcessEnumerator requires the Toolhelp32 API and only runs on Windows.");
        }
    }

    // ---------------------------------------------------------------------
    // P/Invoke — plain DllImport, matching the repo's WinEventInterop style
    // (no source generators; CsWin32 was deliberately removed).
    // ---------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32FirstW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32NextW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
