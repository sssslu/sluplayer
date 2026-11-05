using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using SluPlayer.Services.Downloads;

namespace SluPlayer.UI.Views.Online;

/// <summary>DownloadState → status colour (accent while working, success/danger when finished).</summary>
public sealed class DownloadStateBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            DownloadState.Completed => "Brush.Success",
            DownloadState.Failed => "Brush.Danger",
            DownloadState.Paused or DownloadState.Cancelled or DownloadState.Queued => "Brush.TextTertiary",
            _ => parameter as string == "text" ? "Brush.TextSecondary" : "Brush.Accent",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>bool IsAudio → music / video glyph (thumbnail placeholders).</summary>
public sealed class KindGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Application.Current.TryFindResource(value is true ? "Icon.Music" : "Icon.Video") as string ?? "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
