using System.Windows.Controls;
using SluPlayer.ViewModels;

namespace SluPlayer.UI.Views;

/// <summary>
/// The download view (link → analysis → download list), hosted by the download window. Creating it restores the
/// download list; the engine housekeeping/update check runs when it is first shown. See <see cref="OnlineViewModel"/>.
/// </summary>
public partial class OnlineView : UserControl, IPage, IDisposable
{
    private readonly OnlineViewModel _vm;

    public OnlineView(IShell shell)
    {
        InitializeComponent();
        _vm = new OnlineViewModel(shell);
        DataContext = _vm;
        Loaded += (_, _) => _vm.OnShown();
    }

    /// <summary>Analyses a link handed over by another part of the app ("다운로드" in the player).</summary>
    public void Analyze(string url)
    {
        _vm.UrlText = url;
        _ = _vm.AnalyzeAsync(url, null);
    }

    /// <summary>Called each time the hosting window is shown.</summary>
    public void OnNavigatedTo() => _vm.OnShown();

    public void Dispose() => _vm.Dispose();
}
