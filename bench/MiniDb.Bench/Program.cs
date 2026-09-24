using BenchmarkDotNet.Running;

using MiniDb.Bench;

// dotnet run -c Release --project bench/MiniDb.Bench -- measure [--out file]
// dotnet run -c Release --project bench/MiniDb.Bench -- concurrency [--out file]
// dotnet run -c Release --project bench/MiniDb.Bench -- --filter '*'
if (args.Length > 0 && args[0] is "measure" or "concurrency")
{
    string report = args[0] == "measure" ? Measurements.Run() : Concurrency.Run();
    Console.Write(report);
    int at = Array.IndexOf(args, "--out");
    if (at >= 0 && at + 1 < args.Length)
    {
        File.WriteAllText(args[at + 1], report);
    }
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Load).Assembly).Run(args);
