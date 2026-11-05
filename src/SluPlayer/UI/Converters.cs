using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SluPlayer.UI;

/// <summary>bool → Visibility. ConverterParameter "invert" flips it; "hidden" uses Hidden instead of Collapsed.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = value switch
        {
            bool v => v,
            int i => i != 0,
            long l => l != 0,
            string s => !string.IsNullOrEmpty(s),
            null => false,
            _ => true,
        };
        var p = parameter as string ?? "";
        if (p.Contains("invert", StringComparison.OrdinalIgnoreCase)) b = !b;
        return b ? Visibility.Visible : p.Contains("hidden", StringComparison.OrdinalIgnoreCase) ? Visibility.Hidden : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Visible;
}
