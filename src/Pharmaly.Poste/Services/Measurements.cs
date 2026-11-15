using System.Diagnostics;

namespace Pharmaly.Poste.Services;

/// <summary>Mesures de rapidité et de mémoire affichées en bas de l'écran d'essai.</summary>
public sealed class Measurements
{
    private readonly List<double> searchDurations = [];

    public int SearchCount => searchDurations.Count;

    public double LastSearchMilliseconds { get; private set; }

    public void RecordSearch(TimeSpan duration)
    {
        LastSearchMilliseconds = duration.TotalMilliseconds;
        searchDurations.Add(duration.TotalMilliseconds);
    }

    public double Percentile(double fraction)
    {
        if (searchDurations.Count == 0)
        {
            return 0;
        }

        var sorted = searchDurations.Order().ToArray();
        var rank = (int)Math.Ceiling(fraction * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }

    public void Reset()
    {
        searchDurations.Clear();
        LastSearchMilliseconds = 0;
    }

    /// <summary>Mémoire physique occupée par le programme, en Mo.</summary>
    public static double ReadWorkingSetMegabytes()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return process.WorkingSet64 / 1024d / 1024d;
    }

    public static TimeSpan ReadTimeSinceProcessStart()
    {
        using var process = Process.GetCurrentProcess();
        return DateTime.Now - process.StartTime;
    }
}
