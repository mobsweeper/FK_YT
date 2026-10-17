namespace FkYouTube;

public partial class MainWindow
{
    partial void RefreshLocalizedUi()
    {
        // Only repaint existing state. Language is not an account, feed, playback,
        // subtitle or recommendation preference and must not restart any of them.
        RefreshMainWindowTextState();
        AccountLabel.Text = GetAccountLabelText();
        UpdateToolState();
        RefreshToolUpdateUiText();
        RefreshBrowseUiText();
        RefreshFeatureUiText();
        RefreshHistoryOutboxUi();
        RefreshQueueUiText();
        RefreshInteractionUiText();
        RefreshPlayerViewModeUiText();
        RefreshPopoutUiText();
        if (!string.IsNullOrEmpty(ItemCount.Text))
            ItemCount.Text = UiText.F("{0:N0}개", "{0:N0} items", "{0:N0} 件", _items.Count);
        UpdatePauseControls();
        RefreshLiveControls(_snapshot);
        if (_snapshot is {} snapshot)
        {
            DecoderLabel.Text = snapshot.HardwareDecoder is null or "unavailable"
                ? UiText.T("디코더 확인 중", "Checking decoder", "デコーダーを確認中")
                : string.IsNullOrWhiteSpace(snapshot.HardwareDecoder) || snapshot.HardwareDecoder == "no"
                    ? UiText.T("CPU 디코딩", "CPU decoding", "CPU デコード")
                : "HW · " + snapshot.HardwareDecoder;
        }
        if (_statusTextFactory is {} status) StatusText.Text = status();
    }
}
