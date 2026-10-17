using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FkYouTube.ProcessSupport;

/// <summary>A redirected child created suspended and placed in its own kill-on-close job.</summary>
public sealed class OwnedProcess : IDisposable
{
    // A raw Windows handle can be reused immediately after close. Keep its read,
    // every native operation, and the final close in the same lifetime region.
    private readonly object _jobLifetime = new();
    private nint _job;
    private int _disposed;
    public Process Process { get; }
    public int Id { get; }
    public StreamWriter StandardInput { get; }
    public StreamReader StandardOutput { get; }
    public StreamReader StandardError { get; }
    public bool HasExited => Process.HasExited;
    public int ExitCode => Process.ExitCode;

    private OwnedProcess(Process process, nint job, StreamWriter input, StreamReader output, StreamReader error)
    {
        Process = process;
        Id = process.Id;
        _job = job;
        StandardInput = input;
        StandardOutput = output;
        StandardError = error;
    }

    public static OwnedProcess Start(string exe, IReadOnlyList<string> args, string? workingDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exe);
        ArgumentNullException.ThrowIfNull(args);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("OwnedProcess requires Windows.");
        exe = Path.GetFullPath(exe);
        if (!File.Exists(exe)) throw new FileNotFoundException("Runtime executable is missing.", exe);
        if (workingDirectory is not null) workingDirectory = Path.GetFullPath(workingDirectory);
        nint job = 0, stdinRead = 0, stdinWrite = 0, stdoutRead = 0, stdoutWrite = 0, stderrRead = 0, stderrWrite = 0;
        nint attributeList = 0, handleList = 0, childEnvironment = 0;
        var attributesInitialized = false;
        var pi = new Native.ProcessInformation();
        Process? process = null;
        StreamWriter? input = null;
        StreamReader? output = null, error = null;
        try
        {
            job = Native.CreateJobObjectW(0, null);
            Check(job != 0);
            var limits = new Native.JobExtendedLimitInformation();
            limits.BasicLimitInformation.LimitFlags = Native.JobObjectLimitKillOnJobClose;
            Check(Native.SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<Native.JobExtendedLimitInformation>()));

            var security = new Native.SecurityAttributes { Length = Marshal.SizeOf<Native.SecurityAttributes>(), InheritHandle = 1 };
            Check(Native.CreatePipe(out stdinRead, out stdinWrite, ref security, 0));
            Check(Native.CreatePipe(out stdoutRead, out stdoutWrite, ref security, 0));
            Check(Native.CreatePipe(out stderrRead, out stderrWrite, ref security, 0));
            Check(Native.SetHandleInformation(stdinWrite, 1, 0));
            Check(Native.SetHandleInformation(stdoutRead, 1, 0));
            Check(Native.SetHandleInformation(stderrRead, 1, 0));

            nuint attributeBytes = 0;
            Native.InitializeProcThreadAttributeList(0, 1, 0, ref attributeBytes);
            attributeList = Marshal.AllocHGlobal(checked((int)attributeBytes));
            Check(Native.InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeBytes));
            attributesInitialized = true;
            handleList = Marshal.AllocHGlobal(3 * IntPtr.Size);
            Marshal.WriteIntPtr(handleList, 0, stdinRead);
            Marshal.WriteIntPtr(handleList, IntPtr.Size, stdoutWrite);
            Marshal.WriteIntPtr(handleList, 2 * IntPtr.Size, stderrWrite);
            Check(Native.UpdateProcThreadAttribute(attributeList, 0, (nuint)0x00020002, handleList, (nuint)(3 * IntPtr.Size), 0, 0));

            var startup = new Native.StartupInfoEx();
            startup.StartupInfo.Size = Marshal.SizeOf<Native.StartupInfoEx>();
            startup.StartupInfo.Flags = 0x00000101; // STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW
            startup.StartupInfo.ShowWindow = 0;
            startup.StartupInfo.StandardInput = stdinRead;
            startup.StartupInfo.StandardOutput = stdoutWrite;
            startup.StartupInfo.StandardError = stderrWrite;
            startup.AttributeList = attributeList;
            var commandLine = new StringBuilder(QuoteArgument(exe));
            foreach (var arg in args)
            {
                if (arg is null || arg.Contains('\0')) throw new ArgumentException("Arguments must be non-null and contain no NUL characters.", nameof(args));
                commandLine.Append(' ').Append(QuoteArgument(arg));
            }
            childEnvironment = CreateChildEnvironment();
            // lpApplicationName is explicit; no shell, PATH lookup, or command expansion occurs.
            Check(Native.CreateProcessW(exe, commandLine, 0, 0, true,
                0x08000000 | 0x00000004 | 0x00080000 | 0x00000400, childEnvironment, workingDirectory, ref startup, out pi));
            Check(Native.AssignProcessToJobObject(job, pi.Process));
            process = Process.GetProcessById(checked((int)pi.ProcessId));
            // Retain the Process handle while the original creation handle is still open.
            _ = process.SafeHandle;

            Close(ref stdinRead); Close(ref stdoutWrite); Close(ref stderrWrite);
            var utf8 = new UTF8Encoding(false);
            input = new StreamWriter(TakeStream(ref stdinWrite, FileAccess.Write), utf8) { AutoFlush = true };
            output = new StreamReader(TakeStream(ref stdoutRead, FileAccess.Read), utf8, true);
            error = new StreamReader(TakeStream(ref stderrRead, FileAccess.Read), utf8, true);
            if (Native.ResumeThread(pi.Thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            var owned = new OwnedProcess(process, job, input, output, error);
            job = 0; process = null; input = null; output = null; error = null;
            return owned;
        }
        catch
        {
            if (pi.Process != 0) Native.TerminateProcess(pi.Process, 1);
            input?.Dispose(); output?.Dispose(); error?.Dispose(); process?.Dispose();
            throw;
        }
        finally
        {
            if (attributeList != 0) { if (attributesInitialized) Native.DeleteProcThreadAttributeList(attributeList); Marshal.FreeHGlobal(attributeList); }
            if (handleList != 0) Marshal.FreeHGlobal(handleList);
            if (childEnvironment != 0) Marshal.FreeHGlobal(childEnvironment);
            Close(ref pi.Thread); Close(ref pi.Process);
            Close(ref stdinRead); Close(ref stdinWrite); Close(ref stdoutRead); Close(ref stdoutWrite); Close(ref stderrRead); Close(ref stderrWrite);
            Close(ref job);
        }
    }

    private static nint CreateChildEnvironment()
    {
        // Preserve the native snapshot, including Windows' hidden =C: entries.
        // Rebuilding from managed Environment would discard those drive states.
        var inherited = Native.GetEnvironmentStringsW();
        Check(inherited != 0);
        try
        {
            var entries = new List<string>();
            for (var current = inherited; Marshal.ReadInt16(current) != 0;)
            {
                var entry = Marshal.PtrToStringUni(current)!;
                entries.Add(entry);
                current += checked((entry.Length + 1) * sizeof(char));
            }
            return Marshal.StringToHGlobalUni(BuildChildEnvironmentBlock(entries));
        }
        finally { Native.FreeEnvironmentStringsW(inherited); }
    }

    private static string BuildChildEnvironmentBlock(IEnumerable<string> entries)
    {
        var block = new StringBuilder();
        foreach (var entry in entries)
            if (!entry.StartsWith("SSLKEYLOGFILE=", StringComparison.OrdinalIgnoreCase))
                block.Append(entry).Append('\0');
        if (block.Length == 0) block.Append('\0');
        return block.Append('\0').ToString();
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken = default) => Process.WaitForExitAsync(cancellationToken);

    /// <summary>Current members of this job; does not inspect unrelated processes.</summary>
    public IReadOnlyList<int> GetProcessIds()
    {
        lock (_jobLifetime)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            return GetProcessIdsLocked();
        }
    }

    private IReadOnlyList<int> GetProcessIdsLocked()
    {
        var job = _job;
        for (var capacity = 16; capacity <= 4096; capacity *= 2)
        {
            var size = 8 + capacity * IntPtr.Size;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!Native.QueryInformationJobObject(job, 3, buffer, (uint)size, out _))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error is 122 or 234) continue;
                    throw new Win32Exception(error);
                }
                var count = Marshal.ReadInt32(buffer, 4);
                if (count < 0 || count > capacity) throw new InvalidOperationException("Invalid job process list.");
                var result = new int[count];
                for (var i = 0; i < count; i++)
                    result[i] = checked((int)(IntPtr.Size == 8 ? Marshal.ReadInt64(buffer, 8 + i * 8) : Marshal.ReadInt32(buffer, 8 + i * 4)));
                return result;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new InvalidOperationException("Owned job process list exceeded its query limit.");
    }

    internal bool HasConfirmedTreeExit()
    {
        lock (_jobLifetime)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            return Process.HasExited && GetProcessIdsLocked().Count == 0;
        }
    }

    /// <summary>Terminates only the process tree in this instance's job.</summary>
    public void Kill()
    {
        lock (_jobLifetime)
        {
            if (_disposed != 0) return;
            if (!Native.TerminateJobObject(_job, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public void Dispose()
    {
        lock (_jobLifetime)
        {
            if (_disposed != 0) return;
            Volatile.Write(ref _disposed, 1);
            // Closing the non-inherited last job handle also terminates grandchildren.
            var job = _job; _job = 0;
            if (job != 0) Native.CloseHandle(job);
        }
        DisposeStream(StandardInput);
        DisposeStream(StandardOutput);
        DisposeStream(StandardError);
        Process.Dispose();
    }

    private static void DisposeStream(IDisposable stream)
    {
        try { stream.Dispose(); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private static FileStream TakeStream(ref nint raw, FileAccess access)
    {
        var handle = new SafeFileHandle(raw, true);
        raw = 0;
        try { return new FileStream(handle, access, 4096, isAsync: false); }
        catch { handle.Dispose(); throw; }
    }

    private static void Check(bool success)
    {
        if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static void Close(ref nint handle)
    {
        var current = handle;
        handle = 0;
        if (current != 0) Native.CloseHandle(current);
    }

    // Implements Windows argv quoting, including trailing backslashes and embedded quotes.
    private static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') result.Append('\\', backslashes * 2 + 1).Append('"');
            else result.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }
        return result.Append('\\', backslashes * 2).Append('"').ToString();
    }

    private static class Native
    {
        internal const uint JobObjectLimitKillOnJobClose = 0x00002000;
        [StructLayout(LayoutKind.Sequential)] internal struct SecurityAttributes { public int Length; public nint SecurityDescriptor; public int InheritHandle; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct StartupInfo
        {
            public int Size; public nint Reserved; public nint Desktop; public nint Title;
            public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
            public short ShowWindow, ReservedSize; public nint Reserved2, StandardInput, StandardOutput, StandardError;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct StartupInfoEx { public StartupInfo StartupInfo; public nint AttributeList; }
        [StructLayout(LayoutKind.Sequential)] internal struct ProcessInformation { public nint Process, Thread; public uint ProcessId, ThreadId; }
        [StructLayout(LayoutKind.Sequential)] internal struct JobBasicLimitInformation
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags;
            public nuint MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit;
            public nuint Affinity; public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
        [StructLayout(LayoutKind.Sequential)] internal struct JobExtendedLimitInformation
        {
            public JobBasicLimitInformation BasicLimitInformation; public IoCounters IoInfo;
            public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateJobObjectW(nint attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetInformationJobObject(nint job, int informationClass, ref JobExtendedLimitInformation information, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryInformationJobObject(nint job, int informationClass, nint information, uint length, out uint returnedLength);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AssignProcessToJobObject(nint job, nint process);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateJobObject(nint job, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreatePipe(out nint readPipe, out nint writePipe, ref SecurityAttributes attributes, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetHandleInformation(nint handle, uint mask, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool InitializeProcThreadAttributeList(nint attributes, int count, uint flags, ref nuint bytes);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UpdateProcThreadAttribute(nint attributes, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returnedSize);
        [DllImport("kernel32.dll")] internal static extern void DeleteProcThreadAttributeList(nint attributes);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreateProcessW(string applicationName, StringBuilder commandLine, nint processAttributes, nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, nint environment, string? currentDirectory, ref StartupInfoEx startupInfo, out ProcessInformation processInformation);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern nint GetEnvironmentStringsW();
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool FreeEnvironmentStringsW(nint environment);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(nint thread);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateProcess(nint process, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(nint handle);
    }
}
