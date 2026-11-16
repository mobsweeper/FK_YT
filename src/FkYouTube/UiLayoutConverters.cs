using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace FkYouTube;

// Thumbnail geometry is presentation-only; source images and cached bytes are unchanged.
public sealed class ThumbnailAspectConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double width && double.IsFinite(width) && width > 0 ? width * 9 / 16 : 132d;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}

public sealed class QueuedVideoDetailConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is VideoItem item ? item.LocalDetailFactory?.Invoke() ?? item.LocalDetail ?? "" : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}
