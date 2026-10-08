using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProGuard.Core.Native;

namespace ProGuard.UI.Services;

public static class ProcessIconCache
{
    private static readonly ConcurrentDictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static ImageSource? _defaultExeIcon;
    private static readonly object _initLock = new();

    public static ImageSource? GetIcon(string? filePath, string processName)
    {
        string key = !string.IsNullOrWhiteSpace(filePath) ? filePath : processName;
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        ImageSource? icon = null;

        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
        {
            icon = ExtractIconFromFile(filePath);
        }

        if (icon == null)
        {
            icon = GetDefaultExeIcon();
        }

        _cache[key] = icon;
        return icon;
    }

    private static ImageSource? ExtractIconFromFile(string filePath)
    {
        try
        {
            var shinfo = new SHFILEINFO();
            var res = Shell32.SHGetFileInfo(filePath, 0, ref shinfo, (uint)Marshal.SizeOf(typeof(SHFILEINFO)), Shell32.SHGFI_ICON | Shell32.SHGFI_SMALLICON);
            if (res != IntPtr.Zero && shinfo.hIcon != IntPtr.Zero)
            {
                try
                {
                    var source = Imaging.CreateBitmapSourceFromHIcon(shinfo.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    return source;
                }
                finally
                {
                    Shell32.DestroyIcon(shinfo.hIcon);
                }
            }
        }
        catch { }

        return null;
    }

    private static ImageSource? GetDefaultExeIcon()
    {
        if (_defaultExeIcon != null) return _defaultExeIcon;

        lock (_initLock)
        {
            if (_defaultExeIcon != null) return _defaultExeIcon;

            try
            {
                var shinfo = new SHFILEINFO();
                var res = Shell32.SHGetFileInfo(".exe", Shell32.FILE_ATTRIBUTE_NORMAL, ref shinfo, (uint)Marshal.SizeOf(typeof(SHFILEINFO)), 
                    Shell32.SHGFI_ICON | Shell32.SHGFI_SMALLICON | Shell32.SHGFI_USEFILEATTRIBUTES);

                if (res != IntPtr.Zero && shinfo.hIcon != IntPtr.Zero)
                {
                    try
                    {
                        var source = Imaging.CreateBitmapSourceFromHIcon(shinfo.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        source.Freeze();
                        _defaultExeIcon = source;
                        return _defaultExeIcon;
                    }
                    finally
                    {
                        Shell32.DestroyIcon(shinfo.hIcon);
                    }
                }
            }
            catch { }

            return null;
        }
    }
}
