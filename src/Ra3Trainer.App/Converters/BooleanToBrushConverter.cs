using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Ra3Trainer.App.Converters;

/// <summary>
/// Maps a bool to one of two brushes — used by the HUD LED dots next to each feature
/// (enabled = lit green, disabled = unlit olive) and anywhere else a state needs a lamp.
/// </summary>
public sealed class BooleanToBrushConverter : IValueConverter
{
    public Brush? TrueBrush { get; set; }

    public Brush? FalseBrush { get; set; }

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool flag && flag ? TrueBrush : FalseBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
