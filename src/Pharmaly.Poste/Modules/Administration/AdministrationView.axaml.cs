using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Pharmaly.Poste.Modules.Administration;

public sealed partial class AdministrationView : UserControl, IModuleView
{
    public AdministrationView() => InitializeComponent();

    private AdministrationViewModel? ViewModel => DataContext as AdministrationViewModel;

    /// <summary>Vrai quand le curseur attend sur la liste des employés, qui n'est pas encore arrivée du serveur.</summary>
    private bool listAwaitsFocus;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (ViewModel is { } viewModel)
        {
            viewModel.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(AdministrationViewModel.Tab))
                {
                    FocusAfterLayout();
                }

                // La liste arrive du serveur après l'ouverture du module : si le curseur attendait
                // sur la liste vide, il passe sur l'employé sélectionné.
                if (change.PropertyName == nameof(AdministrationViewModel.SelectedEmployee) && listAwaitsFocus)
                {
                    FocusAfterLayout();
                }
            };
        }
    }

    /// <summary>Le curseur va sur la liste de l'onglet : employés (↑ ↓ puis Entrée pour aller aux droits) ou motifs.</summary>
    public void FocusDefault()
    {
        if (ViewModel is { IsReturnReasonsTab: true })
        {
            ReturnReasonsView.FocusDefault();
            return;
        }

        // Les lignes viennent peut-être d'être rechargées : elles doivent exister avant de recevoir le curseur.
        EmployeeList.UpdateLayout();
        if (EmployeeList.SelectedIndex >= 0 && EmployeeList.ContainerFromIndex(EmployeeList.SelectedIndex) is { } container)
        {
            listAwaitsFocus = false;
            container.Focus();
        }
        else
        {
            listAwaitsFocus = true;
            EmployeeList.Focus();
        }
    }

    public async Task HandleKeyAsync(KeyEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (!viewModel.IsConfirming && !viewModel.ReturnReasons.IsConfirming && control && e.Key is Key.E or Key.M)
        {
            e.Handled = true;
            await viewModel.ShowTabAsync(e.Key == Key.M ? AdministrationTab.ReturnReasons : AdministrationTab.Employees);
            FocusAfterLayout();
            return;
        }

        if (viewModel.IsReturnReasonsTab)
        {
            await ReturnReasonsView.HandleKeyAsync(e);
            return;
        }

        if (viewModel.IsConfirming)
        {
            // Tant que la confirmation est affichée, le clavier ne sert qu'à y répondre.
            e.Handled = true;
            if (e.Key == Key.Enter)
            {
                await viewModel.ConfirmAsync();
                FocusAfterLayout();
            }
            else if (e.Key == Key.Escape)
            {
                viewModel.CancelConfirmation();
            }

            return;
        }

        switch (e.Key)
        {
            case Key.S when control:
                e.Handled = true;
                if (viewModel.SavePermissionsCommand.CanExecute(null))
                {
                    await viewModel.SavePermissionsAsync();
                    FocusAfterLayout();
                }

                break;

            case Key.N when control:
                e.Handled = true;
                NewNumberBox.Focus();
                break;

            case Key.Escape:
                e.Handled = true;
                FocusDefault();
                break;

            case Key.Enter when NewNumberBox.IsFocused:
                e.Handled = true;
                NewNameBox.Focus();
                break;

            case Key.Enter when NewNameBox.IsFocused:
                e.Handled = true;
                NewSecretBox.Focus();
                break;

            case Key.Enter when NewSecretBox.IsFocused:
                e.Handled = true;
                await viewModel.CreateEmployeeAsync();
                if (viewModel.ErrorMessage.Length == 0)
                {
                    FocusAfterLayout();
                }
                else
                {
                    NewNumberBox.Focus();
                }

                break;

            case Key.Enter when NewSecretResetBox.IsFocused:
                e.Handled = true;
                viewModel.RequestSecretReset();
                break;

            case Key.Enter when EmployeeList.IsKeyboardFocusWithin:
                e.Handled = true;
                FocusFirstPermission();
                break;
        }
    }

    /// <summary>Après un rechargement de la liste, replace le curseur une fois les lignes reconstruites.</summary>
    private void FocusAfterLayout() => Dispatcher.UIThread.Post(FocusDefault, DispatcherPriority.Loaded);

    private void FocusFirstPermission()
    {
        var first = PermissionsPanel.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault(box => box.IsEffectivelyEnabled);
        if (first is not null)
        {
            first.Focus();
        }
        else
        {
            NewSecretResetBox.Focus();
        }
    }
}
