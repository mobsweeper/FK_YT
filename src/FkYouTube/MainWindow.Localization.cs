using System.Windows.Controls;

namespace FkYouTube;

public partial class MainWindow
{
    private bool _updatingUiLanguage;

    // Called before XAML is created, so startup never flashes a different UI language.
    private void InitializeUiLanguage()
    {
        _settings.UiLanguage = UiText.NormalizeLanguage(_settings.UiLanguage);
        UiText.SetLanguage(_settings.UiLanguage);
    }

    private void InitializeUiLanguageSelector()
    {
        _updatingUiLanguage = true;
        try
        {
            foreach (ComboBoxItem item in UiLanguageCombo.Items)
                if (item.Tag?.ToString() == UiText.Language) { UiLanguageCombo.SelectedItem = item; break; }
        }
        finally { _updatingUiLanguage = false; }
    }

    private void UiLanguage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUiLanguage) return;
        if (!_ready || AccountWorkBlocked) { InitializeUiLanguageSelector(); return; }
        if (UiLanguageCombo.SelectedItem is not ComboBoxItem { Tag: string selected }) return;
        var language = UiText.NormalizeLanguage(selected);
        if (language == UiText.Language) return;
        var previous = _settings.UiLanguage;
        _settings.UiLanguage = language;
        if (!TrySaveSettings())
        {
            _settings.UiLanguage = previous;
            InitializeUiLanguageSelector();
            return;
        }
        UiText.SetLanguage(language);
        RefreshLocalizedUi();
    }

    partial void RefreshLocalizedUi();
}
