using System;
using System.Security.Principal;
using System.Threading;

namespace ApexMapper.App.SingleInstance;

public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    public bool IsPrimary { get; }

    public SingleInstanceGuard() : this(BuildMutexName()) { }

    public SingleInstanceGuard(string mutexName)
    {
        _mutex = new Mutex(initiallyOwned: true, name: mutexName, out var createdNew);
        IsPrimary = createdNew;
    }

    private static string BuildMutexName()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? "anon";
        return $"Local\\ApexMapper.App.{sid}";
    }

    public void Dispose()
    {
        if (IsPrimary)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { /* not owner */ }
        }
        _mutex.Dispose();
    }
}
