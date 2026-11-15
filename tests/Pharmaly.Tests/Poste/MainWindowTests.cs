using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
    public async Task La_fenetre_s_ouvre_et_un_scan_au_clavier_ajoute_une_ligne()
    {
        var viewModel = new MainViewModel(new FakeServer(), TimeSpan.Zero);
        var window = new MainWindow(viewModel, new LaunchOptions(null, false, null));
        window.Show();

        Assert.Equal("Pharmaly Gestion", window.Title);
        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        Assert.True(searchBox.IsFocused);

        // Un lecteur de codes-barres envoie les chiffres puis Entrée, comme un clavier.
        window.KeyTextInput(FakeServer.Creme.Barcode!);
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await Task.Delay(50);

        Assert.Single(viewModel.Lines);
        Assert.Equal("1 850,10 DA", window.FindControl<TextBlock>("TotalLabel")!.Text);
        Assert.True(searchBox.IsFocused);
        window.Close();
    }
}
