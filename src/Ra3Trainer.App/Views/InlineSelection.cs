using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Ra3Trainer.App.Views;

/// <summary>Selects an inline row through a non-focusable button, without ListBoxItem.Focus().</summary>
public static class InlineSelection
{
    public static readonly DependencyProperty SelectOnClickProperty = DependencyProperty.RegisterAttached(
        "SelectOnClick", typeof(bool), typeof(InlineSelection), new PropertyMetadata(false, OnChanged));

    public static bool GetSelectOnClick(DependencyObject target) => (bool)target.GetValue(SelectOnClickProperty);
    public static void SetSelectOnClick(DependencyObject target, bool value) => target.SetValue(SelectOnClickProperty, value);

    private static void OnChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not Button button) return;
        if ((bool)args.OldValue) button.Click -= OnClick;
        if ((bool)args.NewValue) button.Click += OnClick;
    }

    private static void OnClick(object sender, RoutedEventArgs args)
    {
        var button = (Button)sender;
        if (FindList(button) is { } list && ItemsControl.ContainerFromElement(list, button) is ListBoxItem item)
            item.SetCurrentValue(Selector.IsSelectedProperty, true);
    }

    private static ListBox? FindList(DependencyObject child)
    {
        for (var parent = System.Windows.Media.VisualTreeHelper.GetParent(child); parent is not null;
             parent = System.Windows.Media.VisualTreeHelper.GetParent(parent))
            if (parent is ListBox list) return list;
        return null;
    }
}
