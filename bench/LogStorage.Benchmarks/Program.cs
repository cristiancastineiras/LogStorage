using System.Reflection;
using BenchmarkDotNet.Running;

// Ejecuta los benchmarks:
//   dotnet run -c Release --project bench/LogStorage.Benchmarks -- --filter *
//   dotnet run -c Release --project bench/LogStorage.Benchmarks -- --filter *Ingest*
BenchmarkSwitcher.FromAssembly(Assembly.GetExecutingAssembly()).Run(args);
