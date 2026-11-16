using System.ComponentModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Pharmaly.Poste.Modules.Sales;

/// <summary>Vrai quand la valeur liée est égale au paramètre : sert à n'afficher que le panneau de l'étape en cours.</summary>
public sealed class EqualityConverter : IValueConverter
{
    public static readonly EqualityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Equals(value, parameter);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Écran de vente. Tout se fait au clavier et au lecteur : les touches arrivent ici avant les
/// contrôles, et le curseur est replacé au bon endroit à chaque changement d'étape.
/// </summary>
public sealed partial class SalesView : UserControl, IModuleView
{
    private SalesViewModel? observed;

    public SalesView()
    {
        InitializeComponent();

        // Un clic sur une ligne ou un bouton ne doit pas laisser le clavier sans destinataire.
        // L'historique a plusieurs champs où l'on clique : il gère lui-même son curseur.
        AddHandler(
            PointerReleasedEvent,
            (_, _) =>
            {
                if (ViewModel is { IsHistory: false })
                {
                    FocusAfterLayout();
                }
            },
            RoutingStrategies.Bubble,
            handledEventsToo: true);

        // Le « * » du clavier principal n'arrive pas toujours avec son symbole dans la touche
        // (selon la disposition du clavier) : il est alors reconnu au texte saisi.
        AddHandler(TextInputEvent, OnTextInput, RoutingStrategies.Tunnel);
    }

    private async void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Text != SalesShortcuts.CashSymbol || ViewModel is not { IsCart: true } viewModel)
        {
            return;
        }

        // Jamais écrit dans un champ : sur le panier, il ouvre les espèces ; dans une étape, il est ignoré.
        e.Handled = true;
        if (viewModel.IsCartEditable)
        {
            await viewModel.BeginCashAsync();
        }
    }

    private SalesViewModel? ViewModel => DataContext as SalesViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (observed is not null)
        {
            observed.PropertyChanged -= OnViewModelChanged;
        }

        observed = ViewModel;
        if (observed is not null)
        {
            observed.PropertyChanged += OnViewModelChanged;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs change)
    {
        switch (change.PropertyName)
        {
            case nameof(SalesViewModel.Screen) or nameof(SalesViewModel.Step) or nameof(SalesViewModel.IsConfirmingOpening):
                FocusAfterLayout();
                break;
            case nameof(SalesViewModel.SelectedLine) when ViewModel?.SelectedLine is { } line:
                LinesList.ScrollIntoView(line);
                break;
            case nameof(SalesViewModel.SelectedResult) when ViewModel?.SelectedResult is { } result:
                ResultsList.ScrollIntoView(result);
                break;
            case nameof(SalesViewModel.SelectedCustomerResult) when ViewModel?.SelectedCustomerResult is { } customer:
                CustomerList.ScrollIntoView(customer);
                break;
        }
    }

    /// <summary>Le curseur va là où l'employé travaille : la recherche, ou le champ de l'étape ouverte.</summary>
    public void FocusDefault()
    {
        if (ViewModel is not { } viewModel)
        {
            Focus();
            return;
        }

        if (viewModel.IsHistory)
        {
            HistoryView.FocusDefault();
            return;
        }

        var target = TargetFor(viewModel);
        if (target is TextBox { IsKeyboardFocusWithin: true })
        {
            return;
        }

        if (!target.Focus())
        {
            Focus();
        }

        if (target is TextBox box && !ReferenceEquals(box, SearchBox) && !ReferenceEquals(box, CustomerSearchBox))
        {
            // La valeur proposée est sélectionnée : la frappe la remplace.
            box.SelectAll();
        }
    }

    private Control TargetFor(SalesViewModel viewModel) => viewModel.Screen switch
    {
        SalesScreen.CashClosed when viewModel is { CanOpenRegister: true, IsConfirmingOpening: false } => OpeningFloatBox,
        SalesScreen.Cart => viewModel.Step switch
        {
            CheckoutStep.None => SearchBox,
            CheckoutStep.Quantity => QuantityBox,
            CheckoutStep.Discount => DiscountBox,
            CheckoutStep.CashAmount => ReceivedBox,
            CheckoutStep.UnpaidCustomer => CustomerSearchBox,
            CheckoutStep.UnpaidNewCustomer => NewCustomerPhoneBox.IsFocused ? NewCustomerPhoneBox : NewCustomerNameBox,
            _ => this,
        },
        _ => this,
    };

    private void FocusAfterLayout() => Dispatcher.UIThread.Post(FocusDefault, DispatcherPriority.Loaded);

    public async Task HandleKeyAsync(KeyEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        switch (viewModel.Screen)
        {
            case SalesScreen.Loading:
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    await viewModel.RefreshCashSessionAsync();
                }

                break;

            case SalesScreen.CashClosed:
                await HandleCashClosedKeyAsync(viewModel, e);
                break;

            case SalesScreen.History:
                await HandleHistoryKeyAsync(viewModel, e);
                break;

            case SalesScreen.Cart when viewModel.IsStepOpen:
                await HandleStepKeyAsync(viewModel, e);
                break;

            case SalesScreen.Cart:
                await HandleCartKeyAsync(viewModel, e);
                break;
        }
    }

    private static bool IsControl(KeyEventArgs e) => e.KeyModifiers.HasFlag(KeyModifiers.Control);

    private static bool IsLineKey(KeyEventArgs e) =>
        e.Key is Key.Up or Key.Down or Key.Delete || IsSymbol(e, Key.Add, "+") || IsSymbol(e, Key.Subtract, "-")
        || IsSymbol(e, Key.Divide, "/");

    private static bool IsSymbol(KeyEventArgs e, Key numericPadKey, string symbol) => e.Key == numericPadKey || e.KeySymbol == symbol;

    private static async Task HandleCashClosedKeyAsync(SalesViewModel viewModel, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.H when IsControl(e):
                e.Handled = true;
                await viewModel.OpenHistoryAsync();
                break;

            case Key.Enter:
                e.Handled = true;
                if (viewModel.IsConfirmingOpening)
                {
                    await viewModel.ConfirmCashOpeningAsync();
                }
                else if (viewModel.CanOpenRegister)
                {
                    viewModel.RequestCashOpening();
                }
                else
                {
                    await viewModel.RefreshCashSessionAsync();
                }

                break;

            case Key.Escape:
                e.Handled = true;
                viewModel.CancelCashOpening();
                break;

            default:
                // Pendant la confirmation, aucune frappe ne modifie le montant affiché.
                e.Handled = viewModel.IsConfirmingOpening;
                break;
        }
    }

    private async Task HandleHistoryKeyAsync(SalesViewModel viewModel, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !viewModel.History.IsStepOpen)
        {
            e.Handled = true;
            await viewModel.CloseHistoryAsync();
            return;
        }

        await HistoryView.HandleKeyAsync(e);
    }

    private async Task HandleCartKeyAsync(SalesViewModel viewModel, KeyEventArgs e)
    {
        // Encaissement : chaque issue a sa touche. Aucune ne confirme : elle ouvre une étape.
        if (SalesShortcuts.IsCash(e))
        {
            e.Handled = true;
            await viewModel.BeginCashAsync();
            return;
        }

        if (SalesShortcuts.IsCard(e))
        {
            e.Handled = true;
            await viewModel.BeginCardAsync();
            return;
        }

        var queryIsEmpty = string.IsNullOrEmpty(viewModel.Query);
        if (queryIsEmpty && IsLineKey(e))
        {
            // Une touche frappée juste après un scan porte sur la ligne que ce scan ajoute. Elle est
            // déclarée traitée avant l'attente : sinon le caractère partirait dans la recherche.
            e.Handled = true;
            await viewModel.WaitForPendingScansAsync();
        }

        switch (e.Key)
        {
            case SalesShortcuts.UnpaidKey:
                e.Handled = true;
                await viewModel.BeginUnpaidAsync();
                break;

            case SalesShortcuts.DiscountKey:
                e.Handled = true;
                await viewModel.BeginDiscountEditAsync();
                break;

            case Key.H when IsControl(e):
                e.Handled = true;
                await viewModel.OpenHistoryAsync();
                break;

            case Key.Enter:
                e.Handled = true;
                await viewModel.SubmitQueryAsync();
                break;

            case Key.Down or Key.Up:
                e.Handled = true;
                var delta = e.Key == Key.Down ? 1 : -1;
                if (viewModel.HasResults)
                {
                    viewModel.MoveResultSelection(delta);
                }
                else
                {
                    viewModel.MoveLineSelection(delta);
                }

                break;

            case Key.Delete when queryIsEmpty && IsControl(e):
                e.Handled = true;
                await viewModel.RequestAbandonAsync();
                break;

            case Key.Delete when queryIsEmpty:
                e.Handled = true;
                viewModel.RemoveSelectedLine();
                break;

            case Key.Escape:
                e.Handled = true;
                viewModel.Query = string.Empty;
                break;

            case Key.Tab:
                // Le curseur ne quitte pas la recherche.
                e.Handled = true;
                break;

            default:
                if (!queryIsEmpty)
                {
                    break;
                }

                if (IsSymbol(e, Key.Add, "+"))
                {
                    e.Handled = true;
                    viewModel.IncreaseSelectedLine();
                }
                else if (IsSymbol(e, Key.Subtract, "-"))
                {
                    e.Handled = true;
                    viewModel.DecreaseSelectedLine();
                }
                else if (IsSymbol(e, Key.Divide, "/"))
                {
                    e.Handled = true;
                    await viewModel.BeginQuantityEditAsync();
                }

                break;
        }
    }

    /// <summary>
    /// Une étape est ouverte : le clavier ne sert qu'à elle. Les étapes sans champ de saisie
    /// consomment toutes les touches ; les autres laissent passer la frappe vers leur champ.
    /// </summary>
    private async Task HandleStepKeyAsync(SalesViewModel viewModel, KeyEventArgs e)
    {
        var step = viewModel.Step;
        var hasTextBox = step is CheckoutStep.Quantity or CheckoutStep.Discount or CheckoutStep.CashAmount
            or CheckoutStep.UnpaidCustomer or CheckoutStep.UnpaidNewCustomer;
        if (!hasTextBox || e.Key is Key.Enter or Key.Escape or Key.Tab or Key.F7 or Key.F8 or Key.F9 || SalesShortcuts.IsCash(e))
        {
            e.Handled = true;
        }

        if (e.Key == Key.Escape)
        {
            viewModel.CancelStep();
            return;
        }

        switch (step)
        {
            case CheckoutStep.Quantity when e.Key == Key.Enter:
                await viewModel.ConfirmQuantityAsync();
                break;

            case CheckoutStep.Discount when e.Key == Key.Enter:
                viewModel.ConfirmDiscount();
                break;

            case CheckoutStep.Abandon when e.Key == Key.Enter:
                viewModel.ConfirmAbandon();
                break;

            case CheckoutStep.CashAmount when e.Key == Key.Enter:
                viewModel.ValidateCashAmount();
                break;

            case CheckoutStep.CashConfirm or CheckoutStep.CardConfirm or CheckoutStep.UnpaidConfirm when e.Key == Key.Enter:
                await viewModel.ConfirmSaleAsync();
                break;

            case CheckoutStep.CashConfirm when e.Key is >= Key.D0 and <= Key.D9 or >= Key.NumPad0 and <= Key.NumPad9:
                // Des chiffres pendant la confirmation (lecture de code-barres parasite, montant
                // à corriger) : retour à la saisie du montant, rien n'est confirmé.
                viewModel.CancelStep();
                break;

            // Réponse du TPE : une lettre par réponse, jamais Entrée.
            case CheckoutStep.CardPending when e.Key == SalesShortcuts.CardAcceptedKey && e.KeyModifiers == KeyModifiers.None:
                await viewModel.AnswerCardAsync(true);
                break;

            case CheckoutStep.CardPending when e.Key == SalesShortcuts.CardRefusedKey && e.KeyModifiers == KeyModifiers.None:
                await viewModel.AnswerCardAsync(false);
                break;

            case CheckoutStep.UnpaidCustomer when e.Key == Key.Enter:
                await viewModel.ChooseCustomerAsync();
                break;

            case CheckoutStep.UnpaidCustomer when e.Key == Key.N && IsControl(e):
                e.Handled = true;
                viewModel.BeginNewCustomer();
                break;

            case CheckoutStep.UnpaidCustomer when e.Key is Key.Down or Key.Up:
                e.Handled = true;
                viewModel.MoveCustomerSelection(e.Key == Key.Down ? 1 : -1);
                break;

            case CheckoutStep.UnpaidNewCustomer when e.Key == Key.Enter && NewCustomerNameBox.IsFocused:
                NewCustomerPhoneBox.Focus();
                break;

            case CheckoutStep.UnpaidNewCustomer when e.Key == Key.Tab:
                (NewCustomerNameBox.IsFocused ? NewCustomerPhoneBox : NewCustomerNameBox).Focus();
                break;

            case CheckoutStep.UnpaidNewCustomer when e.Key == Key.Enter:
                await viewModel.CreateCustomerAsync();
                if (viewModel.Step == CheckoutStep.UnpaidNewCustomer)
                {
                    NewCustomerNameBox.Focus();
                }

                break;

            case CheckoutStep.Unconfirmed when e.Key == Key.Enter:
                await viewModel.RetrySaleAsync();
                break;

            case CheckoutStep.TotalChanged when e.Key == Key.Enter:
                await viewModel.AcknowledgeTotalChangeAsync();
                break;

            case CheckoutStep.Done when e.Key == Key.Enter:
                viewModel.StartNextSale();
                break;
        }
    }
}
