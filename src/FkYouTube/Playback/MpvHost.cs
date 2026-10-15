using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace FkYouTube.Playback;

/// <summary>Dedicated native video rectangle. WPF controls belong outside its airspace.</summary>
public sealed class MpvHost : HwndHost
{
    private nint _window;
    public new nint Handle => _window;
    public bool InputDisabled => _window != 0 && !IsWindowEnabled(_window);

    public MpvHost()
    {
        Focusable = false;
        // Player input belongs to the enabled WPF parent surface and toolbar.
        // Keep the WPF state disabled too, because HwndHost mirrors IsEnabled to Win32.
        IsEnabled = false;
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        // STATIC + SS_BLACKRECT gives a black placeholder before mpv creates its child.
        _window = CreateWindowExW(0, "STATIC", "", 0x40000000 | 0x10000000 | 0x02000000 | 0x04000000 | 0x08000000 | 4,
            0, 0, 1, 1, hwndParent.Handle, 0, 0, 0);
        if (_window == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        // A disabled parent prevents the foreign mpv child from taking click/key focus;
        // Win32 visibility and GPU rendering remain independent of input enablement.
        EnableWindow(_window, false);
        return new HandleRef(this, _window);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (hwnd.Handle != 0) DestroyWindow(hwnd.Handle);
        _window = 0;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint extendedStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnableWindow(nint window, [MarshalAs(UnmanagedType.Bool)] bool enabled);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowEnabled(nint window);
}
