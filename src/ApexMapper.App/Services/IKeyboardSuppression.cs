using ApexMapper.Core.Keys;

namespace ApexMapper.App.Services;

public interface IKeyboardSuppression
{
    event Action<string>? Faulted;
    bool IsTargetForeground { get; }
    IDisposable Enable(int processId, IReadOnlyCollection<KeyId> analogKeys, IReadOnlyCollection<KeyId> digitalKeys);
    void ApplyTo(KeyStateStore store);
}
