using System.Windows;
using System.Windows.Controls;
using FkYouTube.Services;

namespace FkYouTube.Controls;

public sealed class ThumbnailImage : Image
{
    public static ThumbnailCache? Cache { get; set; }
    public static readonly DependencyProperty UrlProperty = DependencyProperty.Register(nameof(Url), typeof(string), typeof(ThumbnailImage), new PropertyMetadata(null, Changed));
    public string? Url { get => (string?)GetValue(UrlProperty); set => SetValue(UrlProperty, value); }
    private int _generation;
    private CancellationTokenSource? _request;

    public ThumbnailImage()
    {
        Loaded += (_, _) => Load();
        Unloaded += (_, _) => CancelLoad();
    }

    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ThumbnailImage)d).Load();

    private void CancelLoad()
    {
        _generation++;
        _request?.Cancel();
        _request = null;
        Source = null;
    }

    private async void Load()
    {
        CancelLoad();
        var cache = Cache;
        if (!IsLoaded || cache == null) return;
        var generation = _generation;
        var request = _request = new CancellationTokenSource();
        try
        {
            var image = await cache.GetAsync(Url, request.Token);
            if (generation == _generation && IsLoaded && !request.IsCancellationRequested) Source = image;
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_request, request)) _request = null;
            request.Dispose();
        }
    }
}
