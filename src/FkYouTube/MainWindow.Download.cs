using System.ComponentModel;
using System.Windows;
using Microsoft.Win32;
using FkYouTube.Services;

namespace FkYouTube;

public partial class MainWindow
{
    private Func<string>? _downloadUiText;
    private void SetDownloadUiText(Func<string> text)
    { _downloadUiText=text;DownloadState.Text=text(); }
    private void RefreshDownloadUiText()
    { if(_downloadUiText is {} text)DownloadState.Text=text(); }

    private CancellationTokenSource? _downloadCts;
    private Task _downloadTask=Task.CompletedTask;
    private async void Download_Click(object sender,RoutedEventArgs e)
    {
        if(_current==null || _closing)return;
        if(_downloadCts!=null){SetStatus(()=>UiText.T("현재 다운로드를 완료하거나 취소한 뒤 다시 저장해 주세요.","Finish or cancel the current download before saving again.","現在のダウンロードを完了またはキャンセルしてから、もう一度保存してください。"));return;}
        var video=_current;var height=QualityCombo.SelectedItem is int selected?selected:_settings.MaxHeight;
        var dialog=new SaveFileDialog{Title=height==0?UiText.T("영상 저장 · 최고 화질 (자동)","Save video · Highest quality (Auto)","動画を保存 · 最高画質（自動）"):UiText.F("영상 저장 · 최대 {0}p","Save video · Up to {0}p","動画を保存 · 最大 {0}p",height),FileName=DownloadService.SuggestedName(video),Filter=UiText.T("영상 파일 (*.mkv)|*.mkv","Video files (*.mkv)|*.mkv","動画ファイル (*.mkv)|*.mkv"),DefaultExt=".mkv",AddExtension=true,OverwritePrompt=true};
        if(dialog.ShowDialog(this)!=true)return;
        _downloadTask=DownloadCurrentAsync(video,height,dialog.FileName);await _downloadTask;
    }
    private async Task DownloadCurrentAsync(VideoItem video,int height,string destination)
    {
        using var cts=new CancellationTokenSource();_downloadCts=cts;
        DownloadButton.IsEnabled=false;DownloadStrip.Visibility=Visibility.Visible;CancelDownloadButton.Visibility=Visibility.Visible;SetDownloadUiText(()=>height==0?UiText.T("다운로드 준비 중 · 최고 화질 (자동)","Preparing download · Highest quality (Auto)","ダウンロードを準備中 · 最高画質（自動）"):UiText.F("다운로드 준비 중 · 최대 {0}p","Preparing download · Up to {0}p","ダウンロードを準備中 · 最大 {0}p",height));
        var progress=new DownloadProgressLifetime(update=>{if(!_closing && ReferenceEquals(_downloadCts,cts))SetDownloadUiText(()=>DownloadProgressMessage(update));});
        try
        {
            using var lease=AcquirePlaybackSession();
            var receipt=new ProcessStopReceipt();lease.AttachConsumer(receipt);
            var result=await new DownloadService(_paths).DownloadAsync(video.Url,height,destination,lease.Path,progress,cts.Token,_settings.Incognito?null:_session.SelectedChannel,receipt);
            progress.Complete();
            if(!_closing && ReferenceEquals(_downloadCts,cts))SetDownloadUiText(()=>DownloadCompletionMessage(result));
        }
        catch(OperationCanceledException){progress.Complete();if(!_closing && ReferenceEquals(_downloadCts,cts))SetDownloadUiText(()=>UiText.T("다운로드 취소됨","Download canceled","ダウンロードをキャンセルしました"));}
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {progress.Complete();if(!_closing && ReferenceEquals(_downloadCts,cts))SetDownloadUiText(()=>ex.Message);}
        finally{progress.Complete();if(ReferenceEquals(_downloadCts,cts))_downloadCts=null;DownloadButton.IsEnabled=true;CancelDownloadButton.Visibility=Visibility.Collapsed;}
    }
    private static string DownloadCompletionMessage(DownloadResult result)=>UiText.F("다운로드 완료 · {0}","Download complete · {0}","ダウンロード完了 · {0}",Path.GetFileName(result.Destination))+
        (result.BackupPath is null?"":UiText.F(" · 기존 파일 보관: {0}"," · Previous file kept: {0}"," · 既存ファイルを保存: {0}",Path.GetFileName(result.BackupPath)));
    private static string DownloadProgressMessage(DownloadProgressUpdate update)=>update.Kind switch
    {
        DownloadProgressKind.Downloading=>UiText.F("다운로드 {0:0.0}% · 영상/음성 단계별 진행","Download {0:0.0}% · progress per video/audio stage","ダウンロード {0:0.0}% · 動画・音声の段階別進行状況",update.Percent),
        DownloadProgressKind.Merging or DownloadProgressKind.Remuxing=>UiText.T("영상·음성 파일을 합치는 중…","Merging video and audio files…","動画と音声ファイルを結合中…"),
        _=>throw new ArgumentOutOfRangeException(nameof(update))
    };

    private sealed class DownloadProgressLifetime:IProgress<DownloadProgressUpdate>
    {
        private readonly object _gate=new();
        private readonly IProgress<bool> _dispatch;
        private readonly Action<DownloadProgressUpdate> _report;
        private DownloadProgressUpdate? _latest;
        private bool _queued,_completed;
        public DownloadProgressLifetime(Action<DownloadProgressUpdate> report)
        { _report=report;_dispatch=new Progress<bool>(_=>DeliverLatest()); }
        public void Report(DownloadProgressUpdate update)
        {
            lock(_gate)
            {
                if(_completed)return;
                _latest=update;
                if(_queued)return;
                _queued=true;
            }
            try{_dispatch.Report(true);}
            catch{lock(_gate)_queued=false;throw;}
        }
        private void DeliverLatest()
        {
            lock(_gate)
            {
                _queued=false;
                var update=_latest;_latest=null;
                // Complete and UI delivery share this boundary: a queued update
                // cannot replace the completion/cancellation/error message.
                if(!_completed && update is not null)_report(update);
            }
        }
        public void Complete(){lock(_gate){_completed=true;_latest=null;}}
    }
    private void CancelDownload_Click(object sender,RoutedEventArgs e)=>_downloadCts?.Cancel();
    private async Task CancelDownloadAsync(){_downloadCts?.Cancel();await _downloadTask;}
}
