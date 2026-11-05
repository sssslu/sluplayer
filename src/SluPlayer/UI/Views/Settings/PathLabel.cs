using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SluPlayer.UI.Views.SettingsPage;

/// <summary>
/// Shows a file-system path in one line, shortening the middle like Explorer ("C:\Users\me\…\SluPlayer\Downloads")
/// so the drive and the folder name both stay visible. The full path is in the tooltip.
/// </summary>
public sealed class PathLabel : TextBlock
{
    public static readonly DependencyProperty PathProperty = DependencyProperty.Register(
        nameof(Path), typeof(string), typeof(PathLabel), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure, (d, _) => ((PathLabel)d).UpdateText()));

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder), typeof(string), typeof(PathLabel), new PropertyMetadata("", (d, _) => ((PathLabel)d).UpdateText()));

    private double _lastWidth = -1;

    public PathLabel()
    {
        TextWrapping = TextWrapping.NoWrap;
        TextTrimming = TextTrimming.CharacterEllipsis;
        VerticalAlignment = VerticalAlignment.Center;
        SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Width - _lastWidth) > 0.5) UpdateText();
        };
    }

    public string Path
    {
        get => (string)GetValue(PathProperty);
        set => SetValue(PathProperty, value);
    }

    /// <summary>Shown (dimmed) when <see cref="Path"/> is empty.</summary>
    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    private void UpdateText()
    {
        var path = Path ?? "";
        ToolTip = string.IsNullOrEmpty(path) ? null : path;
        if (path.Length == 0)
        {
            Text = Placeholder;
            Opacity = 0.6;
            return;
        }
        Opacity = 1;
        double width = ActualWidth;
        _lastWidth = width;
        Text = width <= 0 ? path : Shorten(path, width);
    }

    private string Shorten(string path, double maxWidth)
    {
        if (Measure(path) <= maxWidth) return path;
        var parts = path.Split(System.IO.Path.DirectorySeparatorChar);
        if (parts.Length < 3) return path; // let TextTrimming handle it
        // Keep the root and as many trailing folders as fit: "C:\Users\…\a\b\c".
        string root = parts[0] + System.IO.Path.DirectorySeparatorChar;
        int head = parts.Length > 3 ? 2 : 1;
        for (; head >= 1; head--)
        {
            for (int tail = parts.Length - head - 1; tail >= 1; tail--)
            {
                string candidate = string.Join(System.IO.Path.DirectorySeparatorChar, parts.Take(head)) +
                                   System.IO.Path.DirectorySeparatorChar + "…" + System.IO.Path.DirectorySeparatorChar +
                                   string.Join(System.IO.Path.DirectorySeparatorChar, parts.Skip(parts.Length - tail));
                if (Measure(candidate) <= maxWidth) return candidate;
            }
        }
        return root + "…" + System.IO.Path.DirectorySeparatorChar + parts[^1];
    }

    private double Measure(string text)
    {
        // Measured in the same formatting mode as it is drawn: Display mode text is a little wider than Ideal.
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection,
            new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Brushes.Black, null,
            TextOptions.GetTextFormattingMode(this), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return ft.WidthIncludingTrailingWhitespace + Padding.Left + Padding.Right + 2;
    }
}
