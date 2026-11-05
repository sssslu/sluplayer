using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using SluPlayer.Services.Downloads;
using SluPlayer.UI;

namespace SluPlayer.ViewModels.Online;

/// <summary>The analysis card for one video: preview, kind/quality/format and the download button.</summary>
public sealed class VideoAnalysisViewModel : DownloadOptionsViewModel
{
    public VideoAnalysisViewModel(IShell shell, MediaInfo info, Action<VideoAnalysisViewModel> download) : base(shell)
    {
        Info = info;
        var video = info.Formats.Where(f => f.IsUsable && f.HasVideo).ToList();
        var list = new List<QualityChoice> { new("best", null, "최고 화질") };
        foreach (var g in video.GroupBy(QualityTiers.TierOf).Where(g => g.Key > 0).OrderByDescending(g => g.Key))
        {
            // The exact cap (short side) keeps odd sizes such as 1920×800 in their tier.
            list.Add(new QualityChoice(g.Key.ToString(CultureInfo.InvariantCulture), g.Max(f => f.ShortSide),
                QualityTiers.Label(g.Key, g.Max(f => f.Fps), g.Any(f => f.IsHdr))));
        }
        SetQualities(list);
        DownloadCommand = new RelayCommand(() => download(this));
    }

    public MediaInfo Info { get; }
    public string Title => Info.Title;
    public string? ThumbnailUrl => Info.Thumbnail;
    public string MetaText => OnlineFormat.Join(Info.Uploader, OnlineFormat.Duration(Info.Duration));

    public IRelayCommand DownloadCommand { get; }

    /// <summary>The list request for the current choice.</summary>
    public DownloadRequest CreateRequest() => new(
        Info.WebpageUrl, BuildOptions(), Info.Title, Info.Uploader, Info.Thumbnail, Info.Duration,
        Key: UrlTools.Key(Info.WebpageUrl, Info.ExtractorKey, Info.Id));
}
