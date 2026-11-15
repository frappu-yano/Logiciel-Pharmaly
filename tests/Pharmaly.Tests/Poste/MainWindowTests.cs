using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Pharmaly.Poste;
using Pharmaly.Poste.ViewModels;
using Pharmaly.Poste.Views;
using Pharmaly.Tests.Poste;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Pharmaly.Tests.Poste;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class MainWindowTests
{
    [AvaloniaFact]
    public async Task Identification_au_clavier_puis_scan_puis_verrouillage()
    {
        var server = new FakeServer();
        var viewModel = new MainViewModel(server, TimeSpan.Zero);
        var session = new SessionViewModel(server);
        var window = new MainWindow(viewModel, session, new LaunchOptions(null, false, null));
        window.Show();
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();

        // Niveau 1 : compte de caisse.
        Assert.True(window.FindControl<TextBox>("RegisterLoginBox")!.IsFocused);
        Assert.False(window.FindControl<Control>("WorkArea")!.IsVisible);
        window.KeyTextInput(FakeServer.RegisterLogin);
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        window.KeyTextInput(FakeServer.RegisterPassword);
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();

        // Niveau 2 : employé.
        Assert.Equal(SessionStage.EmployeeSignIn, session.Stage);
        Assert.True(window.FindControl<TextBox>("EmployeeNumberBox")!.IsFocused);
        window.KeyTextInput(FakeServer.EmployeeNumber.ToString());
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        window.KeyTextInput(FakeServer.EmployeeSecret);
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(SessionStage.Work, session.Stage);
        Assert.True(window.FindControl<Control>("WorkArea")!.IsVisible);
        Assert.Equal("Samia Test", window.FindControl<TextBlock>("EmployeeLabel")!.Text);
        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        Assert.True(searchBox.IsFocused);

        // Un lecteur de codes-barres envoie les chiffres puis Entrée, comme un clavier.
        window.KeyTextInput(FakeServer.Creme.Barcode!);
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(viewModel.Lines);
        Assert.Equal("1 850,10 DA", window.FindControl<TextBlock>("TotalLabel")!.Text);
        Assert.True(searchBox.IsFocused);

        // F12 verrouille : l'écran de travail disparaît et les lignes sont effacées.
        window.KeyPressQwerty(PhysicalKey.F12, RawInputModifiers.None);
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(SessionStage.EmployeeSignIn, session.Stage);
        Assert.False(window.FindControl<Control>("WorkArea")!.IsVisible);
        Assert.Empty(viewModel.Lines);
        window.Close();
    }
}
