using System.Diagnostics;
using System.Text.Json;
using Pharmaly.Poste.Services;

namespace Pharmaly.Poste.Modules.Diagnostic;

public sealed record BenchmarkReport(
    bool ServerReachable,
    int Searches,
    double SearchMedianMs,
    double SearchP95Ms,
    int LinesAdded,
    double AddLineAverageMs,
    double MemoryMb,
    double StartupSeconds)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string ToDisplay() => ServerReachable
        ? $"Essai terminé : {Searches} recherches, médiane {SearchMedianMs:F0} ms, 95 % sous {SearchP95Ms:F0} ms ; "
          + $"{LinesAdded} lignes ajoutées en {AddLineAverageMs:F1} ms chacune ; mémoire {MemoryMb:F0} Mo."
        : "Essai impossible : le serveur ne répond pas.";

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}

public static class Benchmark
{
    private static readonly string[] Terms =
    [
        "creme", "gel net", "serum", "shamp", "solaire 50", "baume", "vitamine", "dermalia",
        "hydra 200", "masque nuit", "savon", "lotion", "botaniq serum", "huile", "bebe", "anti-age",
    ];

    private const int Rounds = 5;

    public static async Task<BenchmarkReport> RunAsync(DiagnosticViewModel viewModel)
    {
        await viewModel.CheckServerAsync();
        if (!viewModel.IsConnected)
        {
            return new BenchmarkReport(false, 0, 0, 0, 0, 0, Measurements.ReadWorkingSetMegabytes(), viewModel.StartupSeconds);
        }

        viewModel.ClearLines();
        viewModel.Measurements.Reset();
        var addDuration = TimeSpan.Zero;
        var linesAdded = 0;

        for (var round = 0; round < Rounds; round++)
        {
            foreach (var term in Terms)
            {
                await viewModel.SearchNowAsync(term);
                if (viewModel.Results.Count == 0)
                {
                    continue;
                }

                var product = viewModel.Results[(round + linesAdded) % viewModel.Results.Count];
                var stopwatch = Stopwatch.StartNew();
                viewModel.AddLine(product);
                stopwatch.Stop();
                addDuration += stopwatch.Elapsed;
                linesAdded++;
            }
        }

        viewModel.RefreshMemory();
        return new BenchmarkReport(
            true,
            viewModel.Measurements.SearchCount,
            viewModel.Measurements.Percentile(0.5),
            viewModel.Measurements.Percentile(0.95),
            linesAdded,
            linesAdded == 0 ? 0 : addDuration.TotalMilliseconds / linesAdded,
            Measurements.ReadWorkingSetMegabytes(),
            viewModel.StartupSeconds);
    }
}
