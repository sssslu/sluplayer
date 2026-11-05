using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SluPlayer.Services;

namespace SluPlayer.UI.Views.Online;

/// <summary>
/// Loads web thumbnails asynchronously: <c>&lt;Border on:RemoteImage.Url="{Binding Thumb}"/&gt;</c> fills the border
/// (its CornerRadius clips the picture). Decoded bitmaps are kept in a small in-memory LRU; nothing goes to disk.
/// </summary>
public static class RemoteImage
{
    public static readonly DependencyProperty UrlProperty = DependencyProperty.RegisterAttached(
        "Url", typeof(string), typeof(RemoteImage), new PropertyMetadata(null, OnUrlChanged));

    public static readonly DependencyProperty DecodeWidthProperty = DependencyProperty.RegisterAttached(
        "DecodeWidth", typeof(int), typeof(RemoteImage), new PropertyMetadata(320));

    public static string? GetUrl(DependencyObject d) => (string?)d.GetValue(UrlProperty);
    public static void SetUrl(DependencyObject d, string? value) => d.SetValue(UrlProperty, value);
    public static int GetDecodeWidth(DependencyObject d) => (int)d.GetValue(DecodeWidthProperty);
    public static void SetDecodeWidth(DependencyObject d, int value) => d.SetValue(DecodeWidthProperty, value);

    private const int MemoryCapacity = 200;
    private static readonly Dictionary<string, LinkedListNode<(string Key, BitmapSource Bmp)>> Map = new();
    private static readonly LinkedList<(string Key, BitmapSource Bmp)> Lru = new();
    private static readonly Dictionary<string, Task<BitmapSource?>> InFlight = new();
    private static readonly SemaphoreSlim Throttle = new(4, 4);
    private static readonly Lazy<HttpClient> Http = new(() =>
    {
        var c = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        })
        { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) SluPlayer/1.0");
        return c;
    });

    private static async void OnUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border b) return;
        var url = e.NewValue as string;
        b.Background = Brushes.Transparent;
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            var bmp = await GetAsync(url, GetDecodeWidth(d));
            if (bmp != null && GetUrl(d) == url) b.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
        }
        catch (Exception ex)
        {
            Log.Debug($"thumbnail failed ({url}): {ex.Message}");
        }
    }

    /// <summary>UI thread only (the maps are not synchronised).</summary>
    private static Task<BitmapSource?> GetAsync(string url, int decodeWidth)
    {
        var key = decodeWidth + "|" + url;
        if (Map.TryGetValue(key, out var node))
        {
            Lru.Remove(node);
            Lru.AddFirst(node);
            return Task.FromResult<BitmapSource?>(node.Value.Bmp);
        }
        if (InFlight.TryGetValue(key, out var pending)) return pending;
        var task = LoadAsync(url, key, decodeWidth);
        InFlight[key] = task;
        return task;
    }

    private static async Task<BitmapSource?> LoadAsync(string url, string key, int decodeWidth)
    {
        try
        {
            var bmp = await Task.Run(() => DownloadAsync(url, decodeWidth));
            if (bmp != null)
            {
                Map[key] = Lru.AddFirst((key, bmp));
                while (Lru.Count > MemoryCapacity)
                {
                    Map.Remove(Lru.Last!.Value.Key);
                    Lru.RemoveLast();
                }
            }
            return bmp;
        }
        finally
        {
            InFlight.Remove(key);
        }
    }

    private static async Task<BitmapSource?> DownloadAsync(string url, int decodeWidth)
    {
        byte[] bytes;
        await Throttle.WaitAsync();
        try
        {
            using var resp = await Http.Value.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;
            bytes = await resp.Content.ReadAsByteArrayAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
        finally
        {
            Throttle.Release();
        }
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null; // not an image WPF can decode (WebP without the codec…): the placeholder stays
        }
    }
}
