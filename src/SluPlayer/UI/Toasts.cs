using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SluPlayer.UI;

/// <summary>Short notifications stacked in a corner panel of a window; each fades out after a few seconds.</summary>
internal static class Toasts
{
    public static void Show(Panel host, string message, ToastKind kind)
    {
        var accent = (Brush)host.FindResource(kind switch
        {
            ToastKind.Success => "Brush.Success",
            ToastKind.Warning => "Brush.Warning",
            ToastKind.Error => "Brush.Danger",
            _ => "Brush.Accent",
        });
        var toast = new Border
        {
            Background = (Brush)host.FindResource("Brush.Surface3"),
            BorderBrush = (Brush)host.FindResource("Brush.BorderStrong"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10, 16, 10),
            Margin = new Thickness(0, 8, 0, 0),
            MaxWidth = 460,
            Opacity = 0,
            Child = new DockPanel
            {
                Children =
                {
                    new Border { Width = 4, CornerRadius = new CornerRadius(2), Background = accent, Margin = new Thickness(0, 1, 12, 1) },
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        host.Children.Add(toast);
        toast.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(kind == ToastKind.Error ? 6 : 3.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
            fade.Completed += (_, _) => host.Children.Remove(toast);
            toast.BeginAnimation(UIElement.OpacityProperty, fade);
        };
        timer.Start();
    }
}
