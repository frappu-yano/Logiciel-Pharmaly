using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Pharmaly.Poste.Modules.Products;

public sealed partial class StockTransferView : UserControl, IProductFormView
{
    public StockTransferView()
    {
        InitializeComponent();
        CancelButton.Click += (_, _) => CancelAndRefocus();
        SubmitButton.Click += (_, _) => Dispatcher.UIThread.Post(Refocus);
        ResultsList.Tapped += async (_, _) => await AddAsync();
    }

    private StockTransferViewModel? ViewModel => DataContext as StockTransferViewModel;

    /// <summary>Le curseur va sur l'emplacement de départ : un mauvais emplacement est l'erreur à éviter.</summary>
    public void FocusDefault() => FromBox.Focus();

    public async Task HandleKeyAsync(KeyEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (viewModel.IsEditing && SearchBox.IsFocused && viewModel.Lookup.Query.Length > 0)
            {
                // Premier Échap : efface la recherche en cours, sans fermer le transfert.
                viewModel.Lookup.Clear();
                return;
            }

            CancelAndRefocus();
            return;
        }

        if (viewModel.IsConfirming || viewModel.IsBusy)
        {
            // Pendant la confirmation, le clavier ne sert qu'à y répondre.
            e.Handled = true;
            if (e.Key == Key.Enter)
            {
                await viewModel.SubmitAsync();
                Refocus();
            }

            return;
        }

        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.Enter when control:
                e.Handled = true;
                await viewModel.SubmitAsync();
                Refocus();
                break;

            case Key.Enter when FromBox.IsFocused:
                e.Handled = true;
                ToBox.Focus();
                break;

            case Key.Enter when ToBox.IsFocused || NoteBox.IsFocused:
                e.Handled = true;
                SearchBox.Focus();
                break;

            case Key.Enter when SearchBox.IsFocused:
                e.Handled = true;
                await AddAsync();
                break;

            case Key.Enter when QuantityBox.IsFocused:
                e.Handled = true;
                if (viewModel.ApplyLineQuantity())
                {
                    SearchBox.Focus();
                }
                else
                {
                    QuantityBox.SelectAll();
                }

                break;

            case Key.Tab when SearchBox.IsFocused && viewModel.HasSelectedLine && e.KeyModifiers == KeyModifiers.None:
                e.Handled = true;
                QuantityBox.Focus();
                QuantityBox.SelectAll();
                break;

            case Key.Down when SearchBox.IsFocused:
                e.Handled = true;
                Move(viewModel, +1);
                break;

            case Key.Up when SearchBox.IsFocused:
                e.Handled = true;
                Move(viewModel, -1);
                break;

            case Key.Delete when SearchBox.IsFocused && viewModel.Lookup.Query.Length == 0:
                e.Handled = true;
                viewModel.RemoveSelectedLine();
                break;
        }
    }

    private async Task AddAsync()
    {
        if (ViewModel is { } viewModel && await viewModel.AddFromQueryAsync())
        {
            LinesList.ScrollIntoView(viewModel.Lines.IndexOf(viewModel.SelectedLine!));
        }

        SearchBox.Focus();
    }

    /// <summary>↑ ↓ : dans les résultats de recherche s'il y en a, sinon dans les lignes du transfert.</summary>
    private void Move(StockTransferViewModel viewModel, int delta)
    {
        if (viewModel.Lookup.HasResults)
        {
            if (viewModel.Lookup.MoveSelection(delta) is >= 0 and var result)
            {
                ResultsList.ScrollIntoView(result);
            }
        }
        else if (viewModel.MoveLineSelection(delta) is >= 0 and var line)
        {
            LinesList.ScrollIntoView(line);
        }
    }

    private void CancelAndRefocus()
    {
        ViewModel?.Cancel();
        Refocus();
    }

    private void Refocus()
    {
        if (ViewModel is { } viewModel)
        {
            FormFocus.Restore(this, viewModel, SearchBox);
        }
    }
}
