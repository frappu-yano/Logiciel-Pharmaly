using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Pharmaly.Poste.Modules.Diagnostic;
using Pharmaly.Poste.Services;
using Pharmaly.Poste.Services.Server;
using Pharmaly.Poste.ViewModels;
using Pharmaly.Poste.Views;

namespace Pharmaly.Poste;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var options = LaunchOptions.Parse(desktop.Args ?? []);
            var settings = PosteSettings.Load(options.ServerUrl);
            var tokenStore = new PosteTokenStore();
            var pinStore = new ServerPinStore();
            if (options.ForgetServer)
            {
                // La connexion du poste appartenait à l'ancien serveur : elle part avec lui.
                pinStore.Clear();
                tokenStore.Clear();
            }

            // L'essai automatique tourne sans personne pour comparer l'empreinte : celui qui le
            // lance la fournit. Hors essai, cette variable n'est jamais lue.
            var trust = new ServerTrust(
                settings.ServerUrl,
                pinStore,
                options.AutoBenchmark ? Environment.GetEnvironmentVariable(LaunchOptions.TrialFingerprintVariable) : null);
            var clients = ServerClients.Create(new ServerTransport(settings, tokenStore, trust));
            var shell = new ShellViewModel(
                clients, new InactivityTimer(ShellViewModel.InactivityLockDelay), DiagnosticViewModel.DefaultSearchDelay, trust: trust);
            desktop.MainWindow = new MainWindow(shell, options);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
