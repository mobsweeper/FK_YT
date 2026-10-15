using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using FkYouTube.ProcessSupport;
using FkYouTube.Services;

internal static class ProcessCleanupRegression
{
    public static async Task RunAsync(string workspace)
    {
        await NativeJobLifetimeAsync(workspace);
        await DeferredStageAsync();
        await FailedStageRecoveryAsync();
    }

    private static async Task NativeJobLifetimeAsync(string workspace)
    {
        var owner=OwnedProcess.Start(Path.Combine(workspace,"runtime","python","python.exe"),
            ["-B","-c","import time; time.sleep(60)"],workspace);
        // This witness retains its own native wait handle. Concurrent owner
        // disposal may unregister events on owner.Process before exit delivery.
        using var witness=Process.GetProcessById(owner.Id);_=witness.SafeHandle;
        var exited=witness.WaitForExitAsync();
        try
        {
            var gate=typeof(OwnedProcess).GetField("_jobLifetime",BindingFlags.Instance|BindingFlags.NonPublic)?.GetValue(owner);
            Check(gate is not null,"Native job calls and close have no shared lifetime gate.");
            using var queryStarted=new ManualResetEventSlim();using var killStarted=new ManualResetEventSlim();using var disposeStarted=new ManualResetEventSlim();
            Task Start(ManualResetEventSlim ready,Action action)=>Task.Factory.StartNew(()=>
            {
                ready.Set();
                try{action();}catch(ObjectDisposedException){}
            },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
            Task query,kill,dispose;
            lock(gate!)
            {
                query=Start(queryStarted,()=>owner.GetProcessIds());
                kill=Start(killStarted,owner.Kill);
                dispose=Start(disposeStarted,owner.Dispose);
                Check(queryStarted.Wait(TimeSpan.FromSeconds(3)) && killStarted.Wait(TimeSpan.FromSeconds(3)) && disposeStarted.Wait(TimeSpan.FromSeconds(3)),
                    "Lifetime workers did not reach their calls.");
                Check(!query.Wait(100) && !kill.Wait(100) && !dispose.Wait(100),
                    "A native query, termination, or close escaped the held job lifetime gate.");
                Check((nint)typeof(OwnedProcess).GetField("_job",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(owner)! != 0,
                    "Dispose closed a job while its lifetime gate was held.");
            }
            await Task.WhenAll(query,kill,dispose).WaitAsync(TimeSpan.FromSeconds(5));
            await exited.WaitAsync(TimeSpan.FromSeconds(5));
            Check(witness.HasExited,"Concurrent close did not terminate the exact witnessed child.");
            Exception? failure=null;try{owner.GetProcessIds();}catch(Exception error){failure=error;}
            Check(failure is ObjectDisposedException,"A disposed owned job fabricated an empty process-list receipt.");
        }
        finally{owner.Dispose();await exited.WaitAsync(TimeSpan.FromSeconds(5));}
    }

    private static async Task DeferredStageAsync()
    {
        var stage=(string)Invoke(typeof(DownloadService),"CreateStageDirectory")!;
        var receipt=new ProcessStopReceipt();Invoke(receipt,"MarkStarted");
        File.WriteAllText(Path.Combine(stage,"video.part"),"synthetic partial download");
        try
        {
            Invoke(typeof(DownloadService),"QueueStageCleanup",stage,receipt);
            var timer=Stopwatch.StartNew();Exception? failure=null;
            try{await (Task)Invoke(typeof(DownloadService),"DrainStageCleanupAsync",TimeSpan.FromMilliseconds(80))!;}
            catch(Exception error){failure=error;}
            Check(failure is IOException && timer.Elapsed<TimeSpan.FromSeconds(2),"Deferred stage drain did not report a bounded pending result.");
            Check(Directory.Exists(stage),"A pending receipt deleted a live consumer's stage.");
            Invoke(receipt,"ConfirmStopped");
            await (Task)Invoke(typeof(DownloadService),"DrainStageCleanupAsync",TimeSpan.FromSeconds(3))!;
            Check(!Directory.Exists(stage),"Late stop proof did not drain the retained stage cleanup.");
        }
        finally
        {
            Invoke(receipt,"ConfirmStopped");
            await (Task)Invoke(typeof(DownloadService),"DrainStageCleanupAsync",TimeSpan.FromSeconds(3))!;
            if(Directory.Exists(stage))await (Task)Invoke(typeof(DownloadService),"CleanupStageAsync",stage)!;
        }
    }

    private static async Task FailedStageRecoveryAsync()
    {
        var stage=(string)Invoke(typeof(DownloadService),"CreateStageDirectory")!;
        var partial=Path.Combine(stage,"video.part");File.WriteAllText(partial,"synthetic locked partial download");
        var receipt=new ProcessStopReceipt();Invoke(receipt,"MarkStarted");Invoke(receipt,"ConfirmStopped");
        try
        {
            using(var held=new FileStream(partial,FileMode.Open,FileAccess.Read,FileShare.None))
            {
                Invoke(typeof(DownloadService),"QueueStageCleanup",stage,receipt);
                Exception? failure=null;
                try{await (Task)Invoke(typeof(DownloadService),"DrainStageCleanupAsync",TimeSpan.FromSeconds(3))!;}
                catch(Exception error){failure=error;}
                Check(failure is IOException,"A failed stage deletion was silently reported as a drained cleanup.");
                Check(Directory.Exists(stage),"A failed cleanup lost its retained stage.");
            }
            Invoke(typeof(DownloadService),"QueueStageCleanup",stage,receipt);
            await (Task)Invoke(typeof(DownloadService),"DrainStageCleanupAsync",TimeSpan.FromSeconds(3))!;
            Check(!Directory.Exists(stage),"The exact stop-proven fixture could not be retried after its lock was released.");
        }
        finally{if(Directory.Exists(stage))await (Task)Invoke(typeof(DownloadService),"CleanupStageAsync",stage)!;}
    }

    private static object? Invoke(object instance,string name,params object?[] args)
    {
        var type=instance as Type??instance.GetType();
        var method=type.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static)
            .SingleOrDefault(member=>member.Name==name && member.GetParameters().Length==args.Length &&
                member.GetParameters().Select((parameter,index)=>args[index] is null || parameter.ParameterType.IsInstanceOfType(args[index])).All(value=>value))
            ??throw new MissingMethodException(type.FullName,name);
        try{return method.Invoke(instance is Type?null:instance,args);}
        catch(TargetInvocationException error)when(error.InnerException is not null){ExceptionDispatchInfo.Capture(error.InnerException).Throw();throw;}
    }
    private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
