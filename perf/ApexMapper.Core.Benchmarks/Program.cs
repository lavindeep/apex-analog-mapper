using BenchmarkDotNet.Running;

namespace ApexMapper.Core.Benchmarks;

public static class Program
{
    public static int Main(string[] args)
    {
        BenchmarkRunner.Run<MappingTickBenchmark>(args: args);
        return 0;
    }
}
