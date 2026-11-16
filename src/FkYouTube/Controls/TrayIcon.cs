using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace FkYouTube.Controls;

/// <summary>Owns one Windows notification icon and its separate, hidden message window.</summary>
internal sealed class TrayIcon : IDisposable
{
    internal enum Command : uint { Restore = 1, TogglePlayback, Next, Exit }
    internal readonly record struct MenuItem(Command Command, string Text, bool Enabled = true);

    private const uint NimAdd = 0, NimDelete = 2, NimSetFocus = 3, NimSetVersion = 4;
    private const uint NifMessage = 1, NifIcon = 2, NifTip = 4, NifShowTip = 0x80;
    private const uint IconId = 1;
    private const int CallbackMessage = 0x8001; // WM_APP + 1, on our private HWND
    private const int WmGetIcon = 0x7f, WmContextMenu = 0x7b, WmLeftButtonUp = 0x202, WmRightButtonUp = 0x205;
    private const int NinSelect = 0x400, NinKeySelect = 0x401;
    private const uint MfGrayed = 1, MfSeparator = 0x800;
    private const uint TpmRightButton = 2, TpmNoNotify = 0x80, TpmReturnCmd = 0x100;

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _recoveryTimer;
    private readonly HwndSource _source;
    // HwndSource keeps a weak reference to its hooks.
    private readonly HwndSourceHook _hook;
    private readonly int _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
    private readonly Func<MenuItem[]> _getMenu;
    private readonly Action<Command> _invoke;
    private readonly Action _registrationLost;
    private readonly nint _icon;
    private bool _registered, _version4, _showingMenu, _recoveryQueued, _disposed;

    internal TrayIcon(Dispatcher dispatcher, nint mainHwnd, Func<MenuItem[]> getMenu,
        Action<Command> invoke, Action registrationLost)
    {
        _dispatcher = dispatcher;
        _getMenu = getMenu;
        _invoke = invoke;
        _registrationLost = registrationLost;
        _recoveryTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _recoveryTimer.Tick += OnRecoveryTick;
        // Explorer broadcasts TaskbarCreated to top-level windows. HWND_MESSAGE
        // windows do not receive that broadcast; this HWND is top-level but hidden.
        _source = new HwndSource(new HwndSourceParameters("FK_YT notification icon", 1, 1)
        {
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, without WS_VISIBLE
            ExtendedWindowStyle = 0x80 // WS_EX_TOOLWINDOW
        });
        _hook = HandleMessage;
        _source.AddHook(_hook);
        var windowIcon = SendMessageW(mainHwnd, WmGetIcon, 0, 0); // ICON_SMALL; WPF retains ownership
        if (windowIcon == 0) windowIcon = SendMessageW(mainHwnd, WmGetIcon, 1, 0); // ICON_BIG
        _icon = CopyIcon(windowIcon != 0 ? windowIcon : LoadIconW(0, 32512)); // IDI_APPLICATION fallback
    }

    internal bool TryRegister()
    {
        if (_disposed || _icon == 0 || _source.IsDisposed) return false;
        if (_registered) return true;
        var data = IconData();
        data.Flags = NifMessage | NifIcon | NifTip | NifShowTip;
        data.CallbackMessage = CallbackMessage;
        data.Icon = _icon;
        data.Tip = "FK_YT";
        if (!Shell_NotifyIconW(NimAdd, ref data)) return false;
        _registered = true;
        data = IconData();
        data.Version = 4;
        _version4 = Shell_NotifyIconW(NimSetVersion, ref data);
        return true;
    }

    private NotifyIconData IconData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = _source.Handle, Id = IconId
    };

    private nint HandleMessage(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (_disposed) return 0;
        if (_taskbarCreated != 0 && msg == _taskbarCreated)
        {
            if (!_recoveryQueued)
            {
                _recoveryQueued = true;
                _ = _dispatcher.BeginInvoke(RecreateAfterTaskbarCreated, DispatcherPriority.Normal);
            }
            return 0;
        }
        if (msg != CallbackMessage || !_registered) return 0;
        var dataWord = unchecked((uint)(long)lParam);
        var icon = _version4 ? dataWord >> 16 : unchecked((uint)(long)wParam);
        if (icon != IconId) return 0;
        var notification = _version4 ? (int)(dataWord & 0xffff) : unchecked((int)(long)lParam);
        handled = true;
        if (notification == WmContextMenu || !_version4 && notification == WmRightButtonUp)
        {
            // Run the native menu and its actions after returning from this
            // callback. Exit is allowed to dispose the notification window.
            _ = _dispatcher.BeginInvoke(ShowMenu, DispatcherPriority.Normal);
        }
        else if (notification is NinSelect or NinKeySelect || !_version4 && notification == WmLeftButtonUp)
        {
            _ = _dispatcher.BeginInvoke(() => Invoke(Command.Restore), DispatcherPriority.Normal);
        }
        return 0;
    }

    private void RecreateAfterTaskbarCreated()
    {
        _recoveryQueued = false;
        if (_disposed) return;
        // Windows can also send this on a DPI change while the old icon
        // is still present. Delete by the same owner and ID before adding.
        if (_registered)
        {
            var data = IconData();
            _ = Shell_NotifyIconW(NimDelete, ref data);
        }
        _registered = false;
        _recoveryTimer.Stop();
        if (!TryRegister()) _recoveryTimer.Start();
    }

    private void OnRecoveryTick(object? sender, EventArgs e)
    {
        _recoveryTimer.Stop();
        if (!_disposed && !TryRegister()) _registrationLost();
    }

    private void ShowMenu()
    {
        if (_disposed || !_registered || _showingMenu) return;
        var menu = CreatePopupMenu();
        if (menu == 0) return;
        _showingMenu = true;
        uint selected;
        try
        {
            foreach (var item in _getMenu())
            {
                if (item.Command == Command.Exit && !AppendMenuW(menu, MfSeparator, 0, null)) return;
                if (!AppendMenuW(menu, item.Enabled ? 0u : MfGrayed, (nuint)item.Command, item.Text)) return;
            }
            _ = SetMenuDefaultItem(menu, (uint)Command.Restore, false);
            var id = new NotifyIconIdentifier { Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(), Window = _source.Handle, Id = IconId };
            NativePoint at;
            if (Shell_NotifyIconGetRect(ref id, out var rect) >= 0)
                at = new() { X = rect.Left + (rect.Right - rect.Left) / 2, Y = rect.Top + (rect.Bottom - rect.Top) / 2 };
            else if (!GetCursorPos(out at)) return;
            var hwnd = _source.Handle;
            _ = SetForegroundWindow(hwnd);
            selected = TrackPopupMenu(menu, TpmRightButton | TpmNoNotify | TpmReturnCmd, at.X, at.Y, 0, hwnd, 0);
            _ = PostMessageW(hwnd, 0, 0, 0); // WM_NULL: let subsequent menus dismiss normally.
        }
        finally
        {
            _ = DestroyMenu(menu);
            _showingMenu = false;
        }
        if (_disposed) return;
        if (_registered && selected != (uint)Command.Restore && selected != (uint)Command.Exit)
        {
            var data = IconData();
            _ = Shell_NotifyIconW(NimSetFocus, ref data);
        }
        if (selected is >= (uint)Command.Restore and <= (uint)Command.Exit) Invoke((Command)selected);
    }

    private void Invoke(Command command)
    {
        if (!_disposed) _invoke(command);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _recoveryTimer.Stop();
        _recoveryTimer.Tick -= OnRecoveryTick;
        if (!_source.IsDisposed)
        {
            if (_registered)
            {
                var data = IconData();
                _ = Shell_NotifyIconW(NimDelete, ref data);
            }
            _source.RemoveHook(_hook);
            _source.Dispose();
        }
        if (_icon != 0) _ = DestroyIcon(_icon);
    }

    // Exact NOTIFYICONDATAW layout. HWND/HICON are pointer-sized; the shell's
    // text buffers are inline WCHAR arrays, with native alignment on x86/x64.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        internal uint Size;
        internal nint Window;
        internal uint Id, Flags, CallbackMessage;
        internal nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string? Tip;
        internal uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string? Info;
        internal uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string? InfoTitle;
        internal uint InfoFlags;
        internal Guid ItemGuid;
        internal nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        internal uint Size;
        internal nint Window;
        internal uint Id;
        internal Guid ItemGuid;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }

    [DllImport("shell32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);
    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier icon, out NativeRect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegisterWindowMessageW(string name);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SendMessageW(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint LoadIconW(nint instance, nint name);
    [DllImport("user32.dll")]
    private static extern nint CopyIcon(nint icon);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AppendMenuW(nint menu, uint flags, nuint item, string? text);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetMenuDefaultItem(nint menu, uint item, [MarshalAs(UnmanagedType.Bool)] bool byPosition);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rect);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyMenu(nint menu);
}
