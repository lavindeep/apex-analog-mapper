using Xunit;

namespace ApexMapper.Windows.Tests;

/// <summary>The hook, the pump and the foreground tracker are one per process, so tests that start them run one class at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessSingletons
{
    public const string Name = "process singletons";
}
