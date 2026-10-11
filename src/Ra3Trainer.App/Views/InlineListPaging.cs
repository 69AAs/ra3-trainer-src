using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Ra3Trainer.App.Views;

/// <summary>Mouse-only paging of an existing inline list. Never creates another presentation source.</summary>
public static class InlineListPaging
{
    public static readonly DependencyProperty TargetProperty = DependencyProperty.RegisterAttached(
        "Target", typeof(string), typeof(InlineListPaging), new PropertyMetadata(null, Changed));
    public static string? GetTarget(DependencyObject item) => (string?)item.GetValue(TargetProperty);
    public static void SetTarget(DependencyObject item, string value) => item.SetValue(TargetProperty, value);
    private static void Changed(DependencyObject item, DependencyPropertyChangedEventArgs args)
    {
        if (item is not Button button) return;
        button.Click -= Page;
        if (args.NewValue is string) button.Click += Page;
    }
    private static void Page(object sender, RoutedEventArgs args)
    {
        var button = (Button)sender;
        var parts = GetTarget(button)?.Split(':');
        if (parts is not { Length: 2 } || Window.GetWindow(button)?.FindName(parts[0]) is not DependencyObject target) return;
        var scroll = FindScroll(target);
        if (scroll is not null)
        {
            var page = scroll.ViewportHeight;
            scroll.ScrollToVerticalOffset(Math.Clamp(scroll.VerticalOffset + (parts[1] == "up" ? -page : page), 0, scroll.ScrollableHeight));
        }
    }
    private static ScrollViewer? FindScroll(DependencyObject node)
    {
        if (node is ScrollViewer scroll) return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (FindScroll(VisualTreeHelper.GetChild(node, i)) is { } child) return child;
        return null;
    }
}
