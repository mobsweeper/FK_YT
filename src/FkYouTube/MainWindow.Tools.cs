using System.Windows;
using System.Windows.Controls;
using FkYouTube.Services;

namespace FkYouTube;

public partial class MainWindow
{
    private ToolchainUpdateService? _toolUpdates;
    private ToolchainUpdateInfo? _toolUpdateInfo;
    private ToolchainStatus? _toolStatus;
    private CancellationTokenSource? _toolUpdateCts;
    private Task _toolUpdateTask = Task.CompletedTask;
    private ToolchainUpdateService ToolUpdates => _toolUpdates ??= new(_paths);
    private ToolchainTool SelectedTool => (ToolUpdateChoice.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "deno"
        ? ToolchainTool.Deno : ToolchainTool.YtDlp;

    private void InitializeToolUpdates()
    {
        ToolUpdateChoice.SelectionChanged += (_, _) =>
        {
            _toolUpdateInfo = null;
            if (_toolUpdateCts is null) RefreshToolStatusText();
            RefreshToolUpdateControls();
        };
        // Local state only. No update check or network request at startup.
        _toolUpdateTask = RunToolOperationAsync(async token =>
        {
            _toolStatus = await ToolUpdates.GetStatusAsync(token);
            RefreshToolStatusText();
        });
    }

    private void RefreshToolStatusText()
    {
        if (_toolStatus is not { } status) return;
        ToolUpdateState.Text = $"yt-dlp {status.YtDlpVersion} · Deno {status.DenoVersion}";
        if (!string.IsNullOrWhiteSpace(status.PendingDescription))
            ToolUpdateState.Text += "\n" + UiText.T("재시작 후 적용: ", "Apply after restart: ", "再起動後に適用: ") + status.PendingDescription;
        if (!string.IsNullOrWhiteSpace(status.StartupMessage)) ToolUpdateState.Text += "\n" + status.StartupMessage;
    }

    private void RefreshToolUpdateInfoText(ToolchainUpdateInfo info)
    {
        ToolUpdateState.Text = info.IsUpdateAvailable
            ? UiText.F("현재 {0} → 공식 안정판 {1}\n실험적 업데이트입니다. 정상 재생 중이면 현재 버전 유지를 권장합니다.",
                "Current {0} → official stable {1}\nExperimental update. Keep your current version if playback works normally.",
                "現在 {0} → 公式安定版 {1}\n実験的な更新です。正常に再生できる場合は現在の版を推奨します。", info.InstalledVersion, info.Version)
            : UiText.F("최신 안정판을 사용 중입니다: {0}", "Using the latest stable release: {0}", "最新の安定版を使用中: {0}", info.InstalledVersion);
    }

    private void RefreshToolUpdateUiText()
    {
        if (_toolUpdateCts is not null) return;
        // Keep an already checked candidate visible when language changes.
        if (_toolUpdateInfo is { } info && info.Tool == SelectedTool) RefreshToolUpdateInfoText(info);
        else RefreshToolStatusText();
        RefreshToolUpdateControls();
    }

    private void RefreshToolUpdateControls()
    {
        var busy = _toolUpdateCts is not null;
        ToolUpdateChoice.IsEnabled = !busy && !_closing;
        ToolCheckButton.IsEnabled = !busy && !_closing;
        ToolDownloadButton.IsEnabled = !busy && !_closing && _toolUpdateInfo is { IsUpdateAvailable: true } info && info.Tool == SelectedTool;
        ToolRollbackButton.IsEnabled = !busy && !_closing && _toolStatus?.CanRollback == true;
        ToolBundledButton.IsEnabled = !busy && !_closing;
        ToolCancelButton.IsEnabled = busy && !_closing;
    }

    private async Task RunToolOperationAsync(Func<CancellationToken, Task> operation)
    {
        if (_closing || _toolUpdateCts is not null) return;
        using var cts = new CancellationTokenSource();
        _toolUpdateCts = cts;
        RefreshToolUpdateControls();
        try { await operation(cts.Token); }
        catch (OperationCanceledException)
        {
            if (!_closing) ToolUpdateState.Text = UiText.T("취소했습니다. 현재 도구는 유지됩니다.", "Canceled. Current tools are unchanged.", "キャンセルしました。現在のツールは維持されます。");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException
            or System.Net.Http.HttpRequestException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception
            or KeyNotFoundException or FormatException or ArgumentException)
        {
            if (!_closing) ToolUpdateState.Text = UiText.T("도구 작업을 완료하지 못했습니다. 네트워크·저장 공간·호환성을 확인하세요. 현재 도구는 유지됩니다.",
                "The tool operation failed. Check network access, free space, and compatibility. Current tools are unchanged.",
                "ツール操作を完了できませんでした。ネットワーク・空き容量・互換性を確認してください。現在のツールは維持されます。") + "\n" + error.Message;
        }
        finally
        {
            if (ReferenceEquals(_toolUpdateCts, cts)) _toolUpdateCts = null;
            if (!_closing) RefreshToolUpdateControls();
        }
    }

    private async void ToolCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_toolUpdateCts is not null || _closing) return;
        var tool = SelectedTool;
        _toolUpdateInfo = null;
        _toolUpdateTask = RunToolOperationAsync(async token =>
        {
            ToolUpdateState.Text = UiText.T("공식 안정판을 확인하는 중…", "Checking official stable releases…", "公式安定版を確認中…");
            var info = await ToolUpdates.CheckAsync(tool, token);
            if (_closing || token.IsCancellationRequested) return;
            _toolUpdateInfo = info;
            RefreshToolUpdateInfoText(info);
        });
        await _toolUpdateTask;
    }

    private async void ToolDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_toolUpdateCts is not null || _closing || _toolUpdateInfo is not { IsUpdateAvailable: true } info || info.Tool != SelectedTool) return;
        _toolUpdateTask = RunToolOperationAsync(async token =>
        {
            var progress = new Progress<ToolchainUpdateProgress>(p =>
            {
                if (_closing || token.IsCancellationRequested) return;
                ToolUpdateState.Text = p.Message + (p.TotalBytes is > 0
                    ? $" · {p.BytesReceived / 1048576d:N1} / {p.TotalBytes / 1048576d:N1} MiB" : "");
            });
            await ToolUpdates.StageAsync(info, progress, token);
            _toolUpdateInfo = null;
            // A normal return means the pending journal was committed. A late
            // Cancel must not hide that successful restart reservation.
            _toolStatus = await ToolUpdates.GetStatusAsync(CancellationToken.None);
            RefreshToolStatusText();
        });
        await _toolUpdateTask;
    }

    private async void ToolRollback_Click(object sender, RoutedEventArgs e)
    {
        if (_toolUpdateCts is not null || _closing) return;
        _toolUpdateTask = RunToolOperationAsync(async token =>
        {
            try { await ToolUpdates.RollbackAsync(token); }
            catch
            {
                // A verified-invalid previous set may have been remembered even
                // though no reservation was published. Refresh only button state;
                // the outer handler must retain the original failure explanation.
                try { if (!_closing) _toolStatus = await ToolUpdates.GetStatusAsync(CancellationToken.None); }
                catch { } // Status refresh must never replace the operation error.
                throw;
            }
            _toolUpdateInfo = null;
            _toolStatus = await ToolUpdates.GetStatusAsync(CancellationToken.None);
            RefreshToolStatusText();
        });
        await _toolUpdateTask;
    }

    private async void ToolBundled_Click(object sender, RoutedEventArgs e)
    {
        if (_toolUpdateCts is not null || _closing) return;
        _toolUpdateTask = RunToolOperationAsync(async token =>
        {
            await ToolUpdates.RestoreBundledAsync(token);
            _toolUpdateInfo = null;
            _toolStatus = await ToolUpdates.GetStatusAsync(CancellationToken.None);
            RefreshToolStatusText();
        });
        await _toolUpdateTask;
    }

    private void ToolCancel_Click(object sender, RoutedEventArgs e) => _toolUpdateCts?.Cancel();
    private async Task DrainToolUpdatesAsync()
    {
        _toolUpdateCts?.Cancel();
        await _toolUpdateTask;
    }
}
