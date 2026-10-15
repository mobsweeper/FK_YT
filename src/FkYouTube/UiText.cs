using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace FkYouTube;

// App UI language is independent of YouTube requests, account region and subtitle choice.
public static class UiText
{
    private static string _language = "ko";
    public static string Language => _language;
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(_language switch
    {
        "en" => "en-US", "ja" => "ja-JP", _ => "ko-KR"
    });
    public static UiLanguageSource Source { get; } = new();
    public static event EventHandler? LanguageChanged;

    public static string NormalizeLanguage(string? language) => language?.Trim().ToLowerInvariant() switch
    {
        "en" => "en", "ja" => "ja", _ => "ko"
    };

    public static void SetLanguage(string? language)
    {
        var normalized = NormalizeLanguage(language);
        if (_language == normalized) return;
        _language = normalized;
        Source.NotifyLanguageChanged();
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    public static string T(string ko, string en, string ja) => _language switch
    {
        "en" => en, "ja" => ja, _ => ko
    };

    public static string F(string ko, string en, string ja, params object?[] args) =>
        string.Format(Culture, T(ko, en, ja), args);
}

public sealed class UiLanguageSource : INotifyPropertyChanged
{
    internal UiLanguageSource() { }
    public string Language => UiText.Language;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void NotifyLanguageChanged() => PropertyChanged?.Invoke(this, new(nameof(Language)));
}

[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public string Ko { get; set; } = "";
    public string En { get; set; } = "";
    public string Ja { get; set; } = "";
    public override object ProvideValue(IServiceProvider serviceProvider) => new Binding(nameof(UiLanguageSource.Language))
    {
        Source = UiText.Source,
        Mode = BindingMode.OneWay,
        Converter = new TextConverter(Ko, En, Ja)
    }.ProvideValue(serviceProvider);

    private sealed class TextConverter(string ko, string en, string ja) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => UiText.T(ko, en, ja);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}

// Re-evaluate app-owned computed labels while preserving the bound video/comment data.
[MarkupExtensionReturnType(typeof(object))]
public sealed class LocValueExtension : MarkupExtension
{
    public string? Path { get; set; }
    public IValueConverter? Converter { get; set; }
    public string? Ko { get; set; }
    public string? En { get; set; }
    public string? Ja { get; set; }
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new MultiBinding { Mode = BindingMode.OneWay, Converter = new ValueConverter(Path, Converter, Ko, En, Ja) };
        binding.Bindings.Add(new Binding());
        binding.Bindings.Add(new Binding(nameof(UiLanguageSource.Language)) { Source = UiText.Source });
        return binding.ProvideValue(serviceProvider);
    }

    private sealed class ValueConverter(string? path, IValueConverter? converter, string? ko, string? en, string? ja) : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var item = values[0];
            if (item == DependencyProperty.UnsetValue) return "";
            var value = string.IsNullOrEmpty(path) ? item : item?.GetType().GetProperty(path)?.GetValue(item);
            if (converter != null) return converter.Convert(value!, targetType, parameter, UiText.Culture);
            return ko != null && en != null && ja != null ? UiText.F(ko, en, ja, value) : value ?? "";
        }
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            targetTypes.Select(_ => Binding.DoNothing).ToArray();
    }
}

public sealed class LocalizedFormatConverter : IMultiValueConverter
{
    public string Ko { get; set; } = "";
    public string En { get; set; } = "";
    public string Ja { get; set; } = "";
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        UiText.F(Ko, En, Ja, values.Skip(1).Select(value => value == DependencyProperty.UnsetValue ? "" : value).ToArray());
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        targetTypes.Select(_ => Binding.DoNothing).ToArray();
}
