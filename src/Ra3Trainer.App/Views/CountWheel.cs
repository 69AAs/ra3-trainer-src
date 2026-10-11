using System.Windows;
using System.Windows.Input;

namespace Ra3Trainer.App.Views;

/// <summary>Routes only real wheel events on the count control, without focus or input injection.</summary>
public static class CountWheel
{
    public static readonly DependencyProperty CommandProperty = DependencyProperty.RegisterAttached(
        "Command", typeof(ICommand), typeof(CountWheel), new PropertyMetadata(null, Changed));
    public static void SetCommand(DependencyObject target, ICommand value) => target.SetValue(CommandProperty, value);
    public static ICommand? GetCommand(DependencyObject target) => (ICommand?)target.GetValue(CommandProperty);
    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not UIElement element) return;
        if (args.OldValue is not null) element.PreviewMouseWheel -= OnWheel;
        if (args.NewValue is not null) element.PreviewMouseWheel += OnWheel;
    }
    private static void OnWheel(object sender, MouseWheelEventArgs args)
    {
        if (args.Delta == 0) return;
        var command = GetCommand((DependencyObject)sender);
        var steps = Math.Sign(args.Delta) * Math.Max(1, Math.Abs(args.Delta / 120));
        if (command?.CanExecute(steps) != true) return;
        command.Execute(steps);
        args.Handled = true;
    }
}
