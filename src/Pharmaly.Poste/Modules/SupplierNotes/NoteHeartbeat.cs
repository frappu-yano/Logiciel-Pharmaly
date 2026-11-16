using Avalonia.Threading;

namespace Pharmaly.Poste.Modules.SupplierNotes;

/// <summary>
/// Minuterie du battement : tant que l'écran de saisie d'un bon est ouvert, <see cref="Elapsed"/>
/// est levé à intervalle régulier pour prolonger le verrou. Remplaçable dans les tests.
/// </summary>
public interface INoteHeartbeat
{
    event EventHandler? Elapsed;

    void Start();

    void Stop();
}

public sealed class NoteHeartbeat : INoteHeartbeat
{
    /// <summary>Le bail du verrou dure deux minutes côté serveur : quatre battements par bail.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);

    private readonly TimeSpan interval;
    private DispatcherTimer? timer;

    public NoteHeartbeat(TimeSpan interval) => this.interval = interval;

    public event EventHandler? Elapsed;

    public void Start()
    {
        // La minuterie n'est créée qu'à l'ouverture d'un bon : le module n'en a pas besoin avant.
        if (timer is null)
        {
            timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = interval };
            timer.Tick += (_, _) => Elapsed?.Invoke(this, EventArgs.Empty);
        }

        timer.Start();
    }

    public void Stop() => timer?.Stop();
}
