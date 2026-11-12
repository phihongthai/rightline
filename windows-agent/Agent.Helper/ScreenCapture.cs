using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KidSafe.Agent.Helper;

/// <summary>Captures the whole virtual desktop (all monitors), scales to max 1280px wide and encodes JPEG, targeting &lt; 80 KB.</summary>
internal static class ScreenCapture
{
    public const int MaxWidth = 1280;
    public const int TargetBytes = 80_000;

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);

    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    private const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;

    /// <returns>JPEG bytes, or null if the screen can't be read (secure desktop, locked session...).</returns>
    public static byte[]? CaptureJpeg()
    {
        int x = GetSystemMetrics(SM_XVIRTUALSCREEN), y = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int w = GetSystemMetrics(SM_CXVIRTUALSCREEN), h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (w <= 0 || h <= 0) return null;

        IntPtr screen = GetDC(IntPtr.Zero), mem = IntPtr.Zero, bmp = IntPtr.Zero;
        if (screen == IntPtr.Zero) return null;
        try
        {
            mem = CreateCompatibleDC(screen);
            bmp = CreateCompatibleBitmap(screen, w, h);
            var old = SelectObject(mem, bmp);
            var ok = BitBlt(mem, 0, 0, w, h, screen, x, y, SRCCOPY | CAPTUREBLT);
            SelectObject(mem, old);
            if (!ok) return null;

            BitmapSource src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            if (w > MaxWidth) src = new TransformedBitmap(src, new ScaleTransform((double)MaxWidth / w, (double)MaxWidth / w));
            src.Freeze();
            return EncodeUnderLimit(src);
        }
        finally
        {
            if (bmp != IntPtr.Zero) DeleteObject(bmp);
            if (mem != IntPtr.Zero) DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static byte[] EncodeUnderLimit(BitmapSource src)
    {
        byte[] last = [];
        foreach (var quality in new[] { 60, 50, 40, 30 })
        {
            var enc = new JpegBitmapEncoder { QualityLevel = quality };
            enc.Frames.Add(BitmapFrame.Create(src));
            using var ms = new MemoryStream();
            enc.Save(ms);
            last = ms.ToArray();
            if (last.Length <= TargetBytes) break;
        }
        return last;
    }
}
