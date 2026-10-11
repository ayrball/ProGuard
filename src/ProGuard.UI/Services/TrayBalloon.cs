using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ProGuard.UI.Services;

/// <summary>
/// Shows a tray balloon (a toast on Windows 10 and 11). WPF-UI's tray icon has no balloon support, so this adds a
/// short-lived icon of its own through Shell_NotifyIcon and removes it once the balloon has had time to show.
/// Must be used on the UI thread.
/// </summary>
internal sealed class TrayBalloon : IDisposable
{
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_INFO = 0x10;
    private const uint NIIF_INFO = 0x01, NIIF_WARNING = 0x02;
    private const uint IconId = 0x5047;
    // .NET SDK builds put <ApplicationIcon> in the exe as icon resource 32512.
    private static readonly IntPtr AppIconResource = new(32512);
    private static readonly IntPtr IDI_APPLICATION = new(32512);
    private static readonly TimeSpan Linger = TimeSpan.FromSeconds(15);

    private HwndSource? _window;
    private DispatcherTimer? _removeTimer;
    private bool _added;

    public void Show(string title, string text, bool warning = false)
    {
        // Invisible owner window: Shell_NotifyIcon needs one, though nothing here listens for its messages.
        _window ??= new HwndSource(new HwndSourceParameters("ProGuard notifications") { Width = 0, Height = 0, WindowStyle = 0 });

        var data = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _window.Handle,
            uID = IconId,
            uFlags = NIF_ICON | NIF_TIP | NIF_INFO,
            hIcon = LoadAppIcon(),
            szTip = "ProGuard",
            szInfoTitle = Truncate(title, 63),
            szInfo = Truncate(string.IsNullOrEmpty(text) ? " " : text, 255),
            dwInfoFlags = warning ? NIIF_WARNING : NIIF_INFO
        };

        if (_added) _added = Shell_NotifyIcon(NIM_MODIFY, ref data);
        if (!_added) _added = Shell_NotifyIcon(NIM_ADD, ref data);

        _removeTimer ??= CreateRemoveTimer();
        _removeTimer.Stop();
        _removeTimer.Start();
    }

    private DispatcherTimer CreateRemoveTimer()
    {
        var timer = new DispatcherTimer { Interval = Linger };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            RemoveIcon();
        };
        return timer;
    }

    private void RemoveIcon()
    {
        if (!_added || _window == null) return;
        var data = new NOTIFYICONDATA { cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = _window.Handle, uID = IconId };
        Shell_NotifyIcon(NIM_DELETE, ref data);
        _added = false;
    }

    private static IntPtr LoadAppIcon()
    {
        var icon = LoadIcon(GetModuleHandle(null), AppIconResource);
        return icon != IntPtr.Zero ? icon : LoadIcon(IntPtr.Zero, IDI_APPLICATION);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";

    public void Dispose()
    {
        _removeTimer?.Stop();
        RemoveIcon();
        _window?.Dispose();
        _window = null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
