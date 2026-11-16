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
            var clients = ServerClients.Create(new ServerTransport(settings, new PosteTokenStore()));
            var shell = new ShellViewModel(
                clients, new InactivityTimer(ShellViewModel.InactivityLockDelay), DiagnosticViewModel.DefaultSearchDelay);
            desktop.MainWindow = new MainWindow(shell, options);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
