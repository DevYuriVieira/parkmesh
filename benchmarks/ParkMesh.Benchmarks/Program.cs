using BenchmarkDotNet.Running;

namespace ParkMesh.Benchmarks;

internal static class BenchmarksEntry
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--load", StringComparison.OrdinalIgnoreCase)))
        {
            return await LoadHarnessRunner.RunAsync(args);
        }

        BenchmarkSwitcher.FromAssembly(typeof(BenchmarksEntry).Assembly).Run(args);
        return 0;
    }
}
