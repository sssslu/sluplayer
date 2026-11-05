using System.Windows;
using System.Windows.Controls;
using SluPlayer.Core.Player;

namespace SluPlayer.UI.Dialogs;

/// <summary>"URL 열기": address box (pre-filled from the clipboard) + open button.</summary>
public sealed class OpenUrlDialog : DialogWindow
{
    private readonly TextBox _box;

    private OpenUrlDialog()
    {
        Title = "URL 열기";
        DialogIcon = (string)FindResource("Icon.Link");

        _box = new TextBox { Width = 480, Tag = "URL" };
        try
        {
            if (Clipboard.ContainsText())
            {
                var t = Clipboard.GetText().Trim();
                if (MediaFormats.IsUrl(t)) _box.Text = t;
            }
        }
        catch { /* clipboard busy */ }

        var open = new Button { Content = "열기", IsDefault = true, MinWidth = 96, Style = (Style)FindResource("Btn.Primary") };
        var cancel = new Button { Content = "취소", IsCancel = true, MinWidth = 96, Margin = new Thickness(8, 0, 0, 0), Style = (Style)FindResource("Btn.Secondary") };
        open.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_box.Text)) DialogResult = true;
        };

        Content = new StackPanel
        {
            Children =
            {
                _box,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0), Children = { open, cancel } },
            },
        };
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    /// <summary>Returns the entered address, or null when cancelled.</summary>
    public static string? Prompt(Window owner)
    {
        var dlg = new OpenUrlDialog { Owner = owner };
        return dlg.ShowDialog() == true ? dlg._box.Text.Trim() : null;
    }
}
