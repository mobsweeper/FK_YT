using System.Windows;

namespace FkYouTube;
public partial class App : Application
{
    private Mutex? _instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException+=(_,args)=>
        {
            var smoke=Array.FindIndex(e.Args,a=>a.EndsWith("-smoke-output",StringComparison.Ordinal));
            if(smoke>=0 && smoke+1<e.Args.Length)
            {Directory.CreateDirectory(e.Args[smoke+1]);File.WriteAllText(Path.Combine(e.Args[smoke+1],"fatal.txt"),args.Exception.ToString());args.Handled=true;Shutdown(1);return;}
            MessageBox.Show(UiText.T("오류가 발생했습니다. 작업을 다시 시도하거나 앱을 재시작하세요.\n","An error occurred. Try the action again or restart the app.\n","エラーが発生しました。もう一度操作するか、アプリを再起動してください。\n")+args.Exception.GetType().Name,"FK_YT");args.Handled=true;
        };
        var rootArg=Array.IndexOf(e.Args,"--root");
        var paths=new RuntimePaths(rootArg>=0 && rootArg+1<e.Args.Length?e.Args[rootArg+1]:null);
        _instance=new Mutex(true,paths.InstanceMutexName,out var first);
        if(!first){MessageBox.Show(UiText.T("이 폴더의 FK_YT가 이미 실행 중입니다.","FK_YT is already running from this folder.","このフォルダーの FK_YT はすでに実行中です。"),"FK_YT");Shutdown();return;}
        var window=new MainWindow(paths,e.Args);MainWindow=window;window.Show();
    }
    protected override void OnExit(ExitEventArgs e){_instance?.Dispose();base.OnExit(e);}
}
