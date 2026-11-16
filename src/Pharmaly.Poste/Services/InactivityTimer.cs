using Avalonia.Threading;

namespace Pharmaly.Poste.Services;

/// <summary>
/// Minuterie d'inactivité : <see cref="Elapsed"/> est levé quand le délai s'écoule sans
/// appel à <see cref="Restart"/>. Remplaçable dans les tests pour ne rien attendre.
/// </summary>
public interface IInactivityTimer
{
    event EventHandler? Elapsed;

    /// <summary>Repart de zéro (une touche ou un mouvement de souris vient d'avoir lieu).</summary>
    void Restart();

    void Stop();
}

public sealed class InactivityTimer : IInactivityTimer
{
    private readonly DispatcherTimer timer;

    public InactivityTimer(TimeSpan delay)
    {
        timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Elapsed?.Invoke(this, EventArgs.Empty);
        };
    }

    public event EventHandler? Elapsed;

    public void Restart()
    {
        timer.Stop();
        timer.Start();
    }

    public void Stop() => timer.Stop();
}
