using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using FkYouTube;
using FkYouTube.Services;
using FkYouTube.ProcessSupport;

internal static class SessionReviewR6Regression
{
    private const string Cookies="# Netscape HTTP Cookie File\n.youtube.com\tTRUE\t/\tTRUE\t0\tSAPISID\tsynthetic-r6\n";
    public static async Task RunAsync(string workspace)
    {
        var parent=Path.Combine(workspace,"artifacts","session-review-r6");
        var root=Path.Combine(parent,"run-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var failures=new List<Exception>();
        async Task Run(string name,Func<Task> test)
        {
            try{await test();Console.WriteLine("SESSION R6 PASS "+name);}
            catch(Exception ex){failures.Add(new InvalidOperationException(name,ex));Console.WriteLine("SESSION R6 FAIL "+name+": "+ex);}
        }
        try
        {
            await Run("import refuses a concurrently writable source and keeps it unchanged",()=>ImportWriterAsync(root));
            await Run("bounded cookie read rejects oversize and preserves UTF8 BOM compatibility",()=>BoundedReadAsync(root));
            await Run("cookie lease waits for every consumer and reclaims after late confirmation",()=>DeferredLeaseAsync(root));
            await Run("failed bounded process stop retains an owner until later stop proof",()=>DeferredProcessAsync(workspace,root));
            await Run("feed cleanup returns its error without deleting or awaiting an unconfirmed lease",()=>DeferredFeedAsync(workspace,root));
            if(failures.Count>0)throw new AggregateException(failures);
        }
        finally
        {
            Check(Path.GetFullPath(root).StartsWith(Path.GetFullPath(parent)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),
                "Fixture cleanup escaped its owned workspace directory.");
            Directory.Delete(root,true);
        }
    }
    private static Task ImportWriterAsync(string root)
    {
        var paths=new RuntimePaths(Path.Combine(root,"source-writer"));var session=new SessionService(paths);
        var source=Path.Combine(paths.Root,"source.txt");File.WriteAllText(source,Cookies);
        using(var held=new FileStream(source,FileMode.Open,FileAccess.Write,FileShare.ReadWrite))
        {
            Exception? failure=null;try{session.ImportFile(source);}catch(Exception ex){failure=ex;}
            Check(failure is IOException,"A writable source was accepted despite read stability being unprovable.");
            Check(!session.IsConnected,"Rejected import changed encrypted session state.");
        }
        Check(File.ReadAllText(source)==Cookies,"Import modified its original source.");
        session.ImportFile(source);
        using(var lease=session.Acquire())Check(File.ReadAllText(lease.Path!).Contains("synthetic-r6"),"Normal import no longer works.");
        return Task.CompletedTask;
    }
    private static Task BoundedReadAsync(string root)
    {
        var file=Path.Combine(root,"bounded.txt");File.WriteAllText(file,Cookies,new System.Text.UTF8Encoding(true));
        Check((string)Invoke(typeof(SessionService),"ReadCookieText",file)! ==Cookies,"BOM cookie input changed.");
        using(var oversized=new FileStream(file,FileMode.Create,FileAccess.Write,FileShare.None))oversized.SetLength(8*1024*1024+1);
        Exception? failure=null;try{Invoke(typeof(SessionService),"ReadCookieText",file);}catch(Exception ex){failure=ex;}
        Check(failure is InvalidDataException,"Oversize cookie output was accepted.");
        return Task.CompletedTask;
    }
    private static async Task DeferredLeaseAsync(string root)
    {
        var paths=new RuntimePaths(Path.Combine(root,"deferred-lease"));var session=new SessionService(paths);
        var source=Path.Combine(paths.Root,"source.txt");File.WriteAllText(source,Cookies);session.ImportFile(source);
        var lease=session.Acquire();var first=new ProcessStopReceipt();var second=new ProcessStopReceipt();
        Invoke(first,"MarkStarted");Invoke(second,"MarkStarted");
        Invoke(lease,"AttachConsumer",first);Invoke(lease,"AttachConsumer",second);
        lease.Dispose();
        Check(File.Exists(lease.Path),"Lease deleted before either consumer stopped.");
        Invoke(first,"ConfirmStopped");await Task.Delay(30);
        Check(File.Exists(lease.Path),"Lease deleted while its second consumer remained live.");
        var asyncDispose=lease.DisposeAsync().AsTask();Check(!asyncDispose.IsCompleted,"Async disposal bypassed stop proof.");
        var timer=System.Diagnostics.Stopwatch.StartNew();Exception? pendingFailure=null;
        try{await session.DrainCookieCleanupAsync();}catch(Exception ex){pendingFailure=ex;}
        Check(pendingFailure is IOException && timer.Elapsed<TimeSpan.FromSeconds(8),"Unknown stop proof did not produce a bounded cleanup failure.");
        Check(File.Exists(lease.Path),"Bounded drain failure deleted an unconfirmed lease.");
        Invoke(second,"ConfirmStopped");await asyncDispose.WaitAsync(TimeSpan.FromSeconds(3));
        await session.DrainCookieCleanupAsync();
        Check(!File.Exists(lease.Path),"Confirmed consumers left an unreclaimed lease.");
    }
    private static async Task DeferredProcessAsync(string workspace,string root)
    {
        var paths=new RuntimePaths(Path.Combine(root,"deferred-process"));var session=new SessionService(paths);
        var source=Path.Combine(paths.Root,"source.txt");File.WriteAllText(source,Cookies);session.ImportFile(source);
        var lease=session.Acquire();var receipt=new ProcessStopReceipt();Invoke(lease,"AttachConsumer",receipt);
        var python=Path.Combine(workspace,"runtime","python","python.exe");
        var process=OwnedProcess.Start(python,["-B","-c","import time; time.sleep(60)"],workspace);
        // Recovery disposes its Process after root/job proof. Retain a separate
        // native witness so that disposal cannot remove this test's exit event.
        using var witness=Process.GetProcessById(process.Id);_=witness.SafeHandle;
        var exited=witness.WaitForExitAsync();
        Invoke(receipt,"MarkStarted");
        var allowStop=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recoveryEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts=0;var original=new IOException("synthetic stop proof unavailable");
        Func<Task> stop=async()=>
        {
            if(Interlocked.Increment(ref attempts)==1)throw original;
            recoveryEntered.TrySetResult();
            await allowStop.Task;
            await (Task)Invoke(typeof(ProcessRunner),"StopAndDrainAsync",process,Array.Empty<Task>())!;
        };
        try
        {
            Exception? failure=null;
            try{await (Task)Invoke(typeof(ProcessRunner),"CompleteOwnedProcessAsync",process,Array.Empty<Task>(),receipt,stop)!;}catch(Exception ex){failure=ex;}
            Check(ReferenceEquals(failure,original),"Deferred ownership changed the original stop error.");
            lease.Dispose();
            Check(!receipt.ConsumersStopped && !process.HasExited && File.Exists(lease.Path),"Unconfirmed child lost its job/lease ownership.");
            await recoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Exception? pendingRecovery=null;
            try{await (Task)Invoke(typeof(ProcessRunner),"DrainRecoveryWithinAsync",TimeSpan.FromMilliseconds(80))!;}
            catch(Exception ex){pendingRecovery=ex;}
            Check(pendingRecovery is IOException && !receipt.ConsumersStopped && File.Exists(lease.Path),
                "A bounded recovery drain abandoned or prematurely confirmed its retained owner.");
            allowStop.SetResult();
            await (Task)Invoke(typeof(ProcessRunner),"DrainRecoveryAsync")!;
            await session.DrainCookieCleanupAsync();
            Check(receipt.ConsumersStopped && !File.Exists(lease.Path),"Late process exit did not reclaim the lease.");
            Check(!OwnedProcess.GetAllOwnedProcessIds().Contains(process.Id),"Confirmed stop retained the child job.");
        }
        finally
        {
            allowStop.TrySetResult();
            try{process.Kill();await exited.WaitAsync(TimeSpan.FromSeconds(5));
                if(process.GetProcessIds().Count==0)Invoke(receipt,"ConfirmStopped");}catch(ObjectDisposedException){}
            await lease.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
    private static async Task DeferredFeedAsync(string workspace,string root)
    {
        var paths=new RuntimePaths(Path.Combine(root,"deferred-feed"));var session=new SessionService(paths);
        var source=Path.Combine(paths.Root,"source.txt");File.WriteAllText(source,Cookies);session.ImportFile(source);
        var lease=session.Acquire();var process=OwnedProcess.Start(Path.Combine(workspace,"runtime","python","python.exe"),
            ["-B","-c","import time; time.sleep(60)"],workspace);
        using var witness=Process.GetProcessById(process.Id);_=witness.SafeHandle;
        var exited=witness.WaitForExitAsync();var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts=0;
        Func<Task> stop=async()=>
        {
            if(Interlocked.Increment(ref attempts)==1)throw new IOException("synthetic feed stop unconfirmed");
            await release.Task;
            await (Task)Invoke(typeof(ProcessRunner),"StopAndDrainAsync",process,Array.Empty<Task>())!;
        };
        try
        {
            Exception? failure=null;
            try{await ((Task)Invoke(typeof(FeedService),"CleanupAsync",process,lease,null,false,stop)!).WaitAsync(TimeSpan.FromSeconds(3));}
            catch(Exception ex){failure=ex;}
            Check(failure is AggregateException,"Feed failed stop did not return its existing aggregate error promptly: "+failure?.GetType().Name);
            Check(!exited.IsCompleted && File.Exists(lease.Path),"Feed failed stop disposed the owned job or deleted its consumer's lease.");
            release.TrySetResult();await session.DrainCookieCleanupAsync();await exited.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!File.Exists(lease.Path),"Feed late stop proof failed to reclaim its lease.");
        }
        finally
        {
            release.TrySetResult();try{process.Kill();await exited.WaitAsync(TimeSpan.FromSeconds(5));}catch(ObjectDisposedException){}
            // If an old implementation lacks the injectable boundary, the test
            // still owns this process and can dispose it only after actual exit.
            if(attempts==0 && process.GetProcessIds().Count==0)process.Dispose();
            await lease.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
    private static object? Invoke(object instance,string method,params object?[] args)
    {
        var type=instance as Type??instance.GetType();
        var member=type.GetMethod(method,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance)
            ??throw new MissingMethodException(type.FullName,method);
        try{return member.Invoke(instance is Type?null:instance,args);}
        catch(TargetInvocationException ex)when(ex.InnerException is not null){ExceptionDispatchInfo.Capture(ex.InnerException).Throw();throw;}
    }
    private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
