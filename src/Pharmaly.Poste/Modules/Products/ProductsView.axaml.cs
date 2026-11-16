using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Pharmaly.Poste.Modules.Products;

/// <summary>Ce que l'écran hôte attend de la vue d'un formulaire du module Produits.</summary>
public interface IProductFormView
{
    /// <summary>Place le curseur dans le premier champ à remplir.</summary>
    void FocusDefault();

    /// <summary>Reçoit chaque touche avant les contrôles. Mettre <c>e.Handled</c> à vrai pour une touche consommée.</summary>
    Task HandleKeyAsync(KeyEventArgs e);
}

public sealed partial class ProductsView : UserControl, IModuleView
{
    private const double PageScrollShare = 0.8;

    private ProductsViewModel? subscribed;

    public ProductsView()
    {
        InitializeComponent();
        ResultsList.Tapped += async (_, _) =>
        {
            if (ViewModel is { HasActiveForm: false } viewModel)
            {
                await viewModel.OpenSelectedAsync();
                FocusDefault();
            }
        };
    }

    private ProductsViewModel? ViewModel => DataContext as ProductsViewModel;

    private IProductFormView? FormView => FormHost.Content as IProductFormView;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Subscribe();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Subscribe();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // La vue est remplacée à chaque changement de module : le modèle de vue ne doit pas la retenir.
        if (subscribed is not null)
        {
            subscribed.PropertyChanged -= OnViewModelChanged;
            subscribed = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void Subscribe()
    {
        if (ReferenceEquals(subscribed, ViewModel))
        {
            return;
        }

        if (subscribed is not null)
        {
            subscribed.PropertyChanged -= OnViewModelChanged;
        }

        subscribed = ViewModel;
        if (subscribed is not null)
        {
            subscribed.PropertyChanged += OnViewModelChanged;
            ShowForm();
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs change)
    {
        switch (change.PropertyName)
        {
            case nameof(ProductsViewModel.ActiveForm):
                ShowForm();
                break;
            case nameof(ProductsViewModel.Detail):
                DetailScroll.Offset = default;
                break;
        }
    }

    /// <summary>Place la vue du formulaire ouvert, ou rend le curseur à la recherche quand il se ferme.</summary>
    private void ShowForm()
    {
        Control? view = ViewModel?.ActiveForm switch
        {
            ProductCreateViewModel => new ProductCreateView(),
            ProductEditViewModel => new ProductEditView(),
            SalePriceViewModel => new SalePriceView(),
            StockAdjustmentViewModel => new StockAdjustmentView(),
            StockTransferViewModel => new StockTransferView(),
            _ => null,
        };
        if (view is not null)
        {
            view.DataContext = ViewModel!.ActiveForm;
        }

        FormHost.Content = view;
        Dispatcher.UIThread.Post(FocusDefault, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Le curseur va dans le formulaire ouvert, sinon dans la recherche, texte sélectionné :
    /// la frappe ou le scan suivant remplace la saisie précédente.
    /// </summary>
    public void FocusDefault()
    {
        if (FormView is { } form)
        {
            form.FocusDefault();
        }
        else if (SearchBox.IsEffectivelyEnabled)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
        else
        {
            Focus();
        }
    }

    public async Task HandleKeyAsync(KeyEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        if (viewModel.HasActiveForm)
        {
            if (FormView is { } form)
            {
                await form.HandleKeyAsync(e);
            }

            return;
        }

        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.N when control:
                e.Handled = true;
                viewModel.StartCreate();
                break;

            case Key.F7:
                e.Handled = true;
                viewModel.StartEdit();
                break;

            case Key.F8:
                e.Handled = true;
                viewModel.StartPriceChange();
                break;

            case Key.F9:
                e.Handled = true;
                viewModel.StartAdjustment();
                break;

            case Key.F10:
                e.Handled = true;
                await viewModel.StartTransferAsync();
                break;

            case Key.Enter when SearchBox.IsFocused:
                e.Handled = true;
                await viewModel.OpenFromSearchAsync();
                SearchBox.SelectAll();
                break;

            case Key.Down when SearchBox.IsFocused:
                e.Handled = true;
                ScrollToResult(viewModel.Lookup.MoveSelection(+1));
                break;

            case Key.Up when SearchBox.IsFocused:
                e.Handled = true;
                ScrollToResult(viewModel.Lookup.MoveSelection(-1));
                break;

            case Key.PageDown:
                e.Handled = true;
                ScrollDetail(+1);
                break;

            case Key.PageUp:
                e.Handled = true;
                ScrollDetail(-1);
                break;

            case Key.Escape:
                e.Handled = true;
                viewModel.Lookup.Clear();
                FocusDefault();
                break;
        }
    }

    private void ScrollToResult(int index)
    {
        if (index >= 0)
        {
            ResultsList.ScrollIntoView(index);
        }
    }

    /// <summary>Page suivante / précédente : fait défiler la fiche sans quitter la recherche.</summary>
    private void ScrollDetail(int direction) =>
        DetailScroll.Offset = DetailScroll.Offset.WithY(
            Math.Max(0, DetailScroll.Offset.Y + direction * DetailScroll.Viewport.Height * PageScrollShare));
}

/// <summary>Gestes communs aux vues des formulaires.</summary>
internal static class FormFocus
{
    /// <summary>
    /// Pendant une confirmation les champs sont figés : le curseur va sur la vue elle-même.
    /// De retour en saisie, il revient dans <paramref name="editField"/> s'il n'est plus dans un champ.
    /// </summary>
    public static void Restore(UserControl view, ProductFormViewModel form, InputElement editField)
    {
        if (form.IsConfirming)
        {
            view.Focus();
        }
        else if (!view.IsKeyboardFocusWithin || view.IsFocused)
        {
            editField.Focus();
        }
    }
}
