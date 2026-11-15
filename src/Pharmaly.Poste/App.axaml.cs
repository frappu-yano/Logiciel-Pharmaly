using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Pharmaly.Poste.Services;
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
            var viewModel = new MainViewModel(new ServerClient(settings), MainViewModel.DefaultSearchDelay);
            desktop.MainWindow = new MainWindow(viewModel, options);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
