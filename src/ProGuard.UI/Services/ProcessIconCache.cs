using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
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

    // Extracting an icon touches the disk and the shell, so deferred requests are served one at a time by a
    // dedicated STA thread (the shell wants COM initialised) instead of the caller's thread.
    private static readonly BlockingCollection<string> _pending = new();
    private static readonly ConcurrentDictionary<string, byte> _queued = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _workerLock = new();
    private static Thread? _worker;

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

    /// <summary>
    /// Never blocks on the shell: returns the icon for <paramref name="filePath"/> when it has been extracted,
    /// otherwise queues the extraction and returns the generic .exe icon for now. Ask again later (the process
    /// list does so on every refresh) to pick up the real one.
    /// </summary>
    public static ImageSource? GetIconDeferred(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return GetDefaultExeIcon();
        }

        if (_cache.TryGetValue(filePath, out var cached))
        {
            return cached;
        }

        if (_queued.TryAdd(filePath, 0))
        {
            EnsureWorker();
            _pending.Add(filePath);
        }

        return GetDefaultExeIcon();
    }

    /// <summary>True once the icon for this path has been extracted (or found to be unavailable).</summary>
    public static bool IsLoaded(string filePath) => _cache.ContainsKey(filePath);

    private static void EnsureWorker()
    {
        if (_worker != null) return;

        lock (_workerLock)
        {
            if (_worker != null) return;

            _worker = new Thread(LoadQueuedIcons)
            {
                Name = "ProGuard icon loader",
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal
            };
            _worker.SetApartmentState(ApartmentState.STA);
            _worker.Start();
        }
    }

    private static void LoadQueuedIcons()
    {
        foreach (var path in _pending.GetConsumingEnumerable())
        {
            var icon = File.Exists(path) ? ExtractIconFromFile(path) : null;
            _cache[path] = icon ?? GetDefaultExeIcon();
            _queued.TryRemove(path, out _);
        }
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
