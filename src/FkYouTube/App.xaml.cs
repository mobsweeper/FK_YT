using System.Windows;

namespace FkYouTube;
public partial class App : Application
{
    private Mutex? _instance;
    private FileStream? _dataOwnership;
    private InstanceActivation? _activation;
    private bool _activationPending;
    private bool _activationWindowReady;
    private readonly StartupFailureBoundary _startup;
    public App()
    {
        _startup=new(error=>MessageBox.Show(StartupFailureMessage(error),"FK_YT"),()=>Shutdown(-1));
    }
    protected override async void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException+=(_,args)=>
        {
            // async void callbacks can fail while startup awaits the toolchain.
            // Before Show succeeds they must terminate startup explicitly.
            if(_startup.TryHandleException(args.Exception)){args.Handled=true;return;}
            MessageBox.Show(UiText.T("오류가 발생했습니다. 작업을 다시 시도하거나 앱을 재시작하세요.\n","An error occurred. Try the action again or restart the app.\n","エラーが発生しました。もう一度操作するか、アプリを再起動してください。\n")+args.Exception.GetType().Name,"FK_YT");args.Handled=true;
        };
        await _startup.RunAsync(async()=>
        {
            base.OnStartup(e);
            var rootArg=Array.IndexOf(e.Args,"--root");
            var paths=new RuntimePaths(rootArg>=0 && rootArg+1<e.Args.Length?e.Args[rootArg+1]:null);
            _instance=new Mutex(true,paths.InstanceMutexName,out var first);
            if(!first){await RestoreExistingInstanceAsync(paths);return;}
            // Retain the root mutex for older same-root releases, and claim shared
            // Data before toolchain/session/DB initialization or any worker starts.
            try { _dataOwnership=paths.AcquireDataOwnership(); }
            catch(IOException error) when(error.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021))
            {
                await RestoreExistingInstanceAsync(paths);return;
            }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(UiText.T("데이터 폴더를 잠글 수 없어 시작하지 않았습니다. 폴더 권한과 저장소 상태를 확인해 주세요.","FK_YT could not lock the data folder and did not start. Check folder permissions and storage availability.","データフォルダーをロックできないため起動しませんでした。フォルダーの権限と保存先の状態を確認してください。"),"FK_YT");
                Shutdown();return;
            }
            // Listen before slow toolchain initialization. A second launch queues
            // a restore request even while the first window is still being created.
            _activation=new InstanceActivation(paths,QueueInstanceActivation);
            // Freeze the verified local tool selection before constructing services
            // or starting any worker. Startup does not check for online updates.
            await Playback.LiveFromStartTransport.CleanupStaleAsync(paths);
            await paths.InitializeToolchainAsync();
            if(_startup.Failed)return;
            var window=new MainWindow(paths);MainWindow=window;window.Show();
            // Loaded is async void: later continuations belong to the visible
            // window's ordinary runtime handler, rather than the startup catch.
            _startup.MarkWindowShown();
            CompleteInstanceActivationStartup(window);
        });
    }
    private void CompleteInstanceActivationStartup(MainWindow window)
    {
        _activationWindowReady=true;
        if(_activationPending){_activationPending=false;window.RestoreFromExternalLaunch();}
    }
    private async Task RestoreExistingInstanceAsync(RuntimePaths paths)
    {
        if(!await InstanceActivation.TryNotifyAsync(paths))
            MessageBox.Show(UiText.T("기존 FK_YT 창을 자동으로 열지 못했습니다. 트레이 아이콘이나 다른 Windows 세션을 확인해 주세요. 이전 버전이 실행 중이면 종료 후 다시 실행해 주세요.",
                "The existing FK_YT window could not be restored automatically. Check the tray icon or other Windows sessions. If an older version is running, exit it and try again.",
                "実行中の FK_YT ウィンドウを自動で復元できませんでした。通知領域のアイコンや別の Windows セッションを確認してください。旧バージョンが実行中の場合は終了してから再度起動してください。"),"FK_YT");
        Shutdown();
    }
    private void QueueInstanceActivation()
    {
        if(Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)return;
        try{_=Dispatcher.BeginInvoke(() =>
        {
            if(_startup.Failed)return;
            if(_activationWindowReady && MainWindow is MainWindow window)window.RestoreFromExternalLaunch();
            else _activationPending=true;
        });}
        catch(InvalidOperationException)when(Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished){ }
    }
    private static string StartupFailureMessage(Exception error)=>UiText.T(
        "FK_YT를 시작하지 못해 앱을 종료합니다. 폴더 권한과 저장소 상태를 확인한 뒤 다시 실행해 주세요.\n",
        "FK_YT could not start and will close. Check folder permissions and storage availability, then try again.\n",
        "FK_YT を起動できないため終了します。フォルダーの権限と保存先の状態を確認してから、再度起動してください。\n")+error.GetType().Name;
    protected override void OnExit(ExitEventArgs e){_activation?.Dispose();_dataOwnership?.Dispose();_instance?.Dispose();base.OnExit(e);}
}

// Keep the failure policy testable without showing a modal dialog or creating
// another WPF Application. Ownership is released only by App.OnExit, never here.
internal sealed class StartupFailureBoundary(Action<Exception> reportFailure,Action shutdown)
{
    private bool _windowShown;
    internal bool Failed{get;private set;}
    internal void MarkWindowShown(){if(!Failed)_windowShown=true;}
    internal async Task RunAsync(Func<Task> startup)
    {
        try{await startup();}
        catch(Exception error){Fail(error);}
    }
    internal bool TryHandleException(Exception error)
    {
        if(_windowShown)return false;
        Fail(error);return true;
    }
    private void Fail(Exception error)
    {
        if(Failed)return;
        Failed=true;
        try{reportFailure(error);}
        finally{shutdown();}
    }
}
