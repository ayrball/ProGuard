using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ProGuard.UI.Services;

/// <summary>
/// Executable icons, recoloured as ink/volt duotones so they stay recognisable without breaking the
/// two-tone palette. Extraction touches the disk and the shell, so it runs on one background STA thread
/// (the shell wants COM); results are cached by path and delivered on the UI thread.
/// </summary>
public sealed class IconCache
{
    private static readonly Color Dark = Color.FromRgb(0x0A, 0x0A, 0x0B);
    private static readonly Color Light = Color.FromRgb(0xDF, 0xFF, 0x20);

    private readonly Dispatcher _dispatcher;
    private readonly ConcurrentDictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<Action<ImageSource?>>> _waiting = new(StringComparer.OrdinalIgnoreCase);
    private readonly BlockingCollection<string> _queue = new();

    public IconCache(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        var worker = new Thread(Work) { IsBackground = true, Name = "ProGuard icons", Priority = ThreadPriority.BelowNormal };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    /// <summary>
    /// Returns the icon at once if it is cached (null = known to have none). Otherwise returns false and
    /// calls <paramref name="loaded"/> on the UI thread once it has been extracted.
    /// </summary>
    public bool TryGet(string? path, Action<ImageSource?> loaded, out ImageSource? icon)
    {
        icon = null;
        if (string.IsNullOrWhiteSpace(path)) return true;
        if (_cache.TryGetValue(path, out icon)) return true;

        bool first = false;
        var list = _waiting.GetOrAdd(path, _ => { first = true; return new List<Action<ImageSource?>>(); });
        lock (list) list.Add(loaded);
        if (first) _queue.Add(path);
        return false;
    }

    private void Work()
    {
        foreach (var path in _queue.GetConsumingEnumerable())
        {
            ImageSource? icon = null;
            try
            {
                if (File.Exists(path)) icon = Extract(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or ArgumentException)
            {
                // No icon for this one; the row shows its monogram instead.
            }

            _cache[path] = icon;
            if (_waiting.TryRemove(path, out var callbacks))
            {
                _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    lock (callbacks)
                    {
                        foreach (var callback in callbacks) callback(icon);
                    }
                });
            }
        }
    }

    private static ImageSource? Extract(string path)
    {
        var info = new SHFILEINFO();
        var result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
            return Duotone(source);
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    /// <summary>Maps each pixel's luminance onto the ink→volt ramp, keeping its alpha.</summary>
    internal static BitmapSource Duotone(BitmapSource source)
    {
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int width = bgra.PixelWidth, height = bgra.PixelHeight, stride = width * 4;
        var pixels = new byte[stride * height];
        bgra.CopyPixels(pixels, stride, 0);

        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte a = pixels[i + 3];
            if (a == 0) continue;

            // Bgra32 is straight (not premultiplied) alpha, so the channels are the true colour.
            double b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
            double t = Math.Clamp((0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0, 0, 1);
            t = Math.Pow(t, 0.8); // lift mid-tones a touch so detail survives on the ink background

            // The output is Pbgra32, so premultiply by alpha on the way out.
            double alpha = a / 255.0;
            pixels[i] = (byte)Math.Round((Dark.B + (Light.B - Dark.B) * t) * alpha);
            pixels[i + 1] = (byte)Math.Round((Dark.G + (Light.G - Dark.G) * t) * alpha);
            pixels[i + 2] = (byte)Math.Round((Dark.R + (Light.R - Dark.R) * t) * alpha);
        }

        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_LARGEICON = 0x0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
