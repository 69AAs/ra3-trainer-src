using System.Windows;
using Ra3Trainer.App.ViewModels;

namespace Ra3Trainer.App.Views;

public partial class ReinforcementUnitPickerWindow : Window
{
    public ReinforcementUnitPickerWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private ReinforcementUnitPickerViewModel? ViewModel => DataContext as ReinforcementUnitPickerViewModel;

    /// <summary>The existing picker temporarily replaces the main console; every close path restores it.</summary>
    public bool? ShowWithOwnerHidden()
    {
        var owner = Owner;
        var restore = owner?.IsVisible == true;
        try
        {
            if (restore) owner!.Hide();
            return ShowDialog();
        }
        finally
        {
            if (restore && owner is MainWindow main) main.ShowWithoutActivation();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.RequestClose += OnRequestClose;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.RequestClose -= OnRequestClose;
        }
    }

    private void OnRequestClose(bool? dialogResult)
    {
        Dispatcher.Invoke(() =>
        {
            DialogResult = dialogResult ?? false;
        });
    }
}
