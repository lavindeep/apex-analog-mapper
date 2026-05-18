using BenchmarkDotNet.Running;

namespace ApexMapper.Core.Benchmarks;

public static class Program
{
    public static int Main(string[] args)
    {
        BenchmarkSwitcher.FromTypes(new[]
        {
            typeof(MappingTickBenchmark),
            typeof(KeyStateStoreBenchmark),
        }).Run(args);
        return 0;
    }
}
