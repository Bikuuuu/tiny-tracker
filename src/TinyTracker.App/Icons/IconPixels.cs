using System.Runtime.InteropServices;
using TinyTracker.App.Interop;
using TinyTracker.Presentation.Icons;

namespace TinyTracker.App.Icons;

// Reads one icon out of an .exe, .dll or .ico file as premultiplied BGRA pixels, in memory.
internal static class IconPixels
{
    public static byte[]? Extract(string file, int index, int size)
    {
        if (NativeMethods.PrivateExtractIconsW(file, index, size, size, out var icon, out _, 1, 0) != 1 || icon == 0) return null;
        try
        {
            if (!NativeMethods.GetIconInfo(icon, out var info)) return null;
            try
            {
                // Monochrome icons have no color bitmap; they get a letter tile instead.
                if (info.hbmColor == 0) return null;
                var pixels = Read(info.hbmColor, size);
                if (pixels is null) return null;
                IconSource.Premultiply(pixels, Read(info.hbmMask, size) ?? []);
                return pixels;
            }
            finally
            {
                if (info.hbmColor != 0) NativeMethods.DeleteObject(info.hbmColor);
                if (info.hbmMask != 0) NativeMethods.DeleteObject(info.hbmMask);
            }
        }
        finally
        {
            NativeMethods.DestroyIcon(icon);
        }
    }

    // Top-down 32-bit rows, whatever the bitmap's own format.
    private static byte[]? Read(nint bitmap, int size)
    {
        var header = new NativeMethods.BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
            biWidth = size,
            biHeight = -size,
            biPlanes = 1,
            biBitCount = 32,
        };
        var pixels = new byte[size * size * 4];
        var dc = NativeMethods.GetDC(0);
        try
        {
            return NativeMethods.GetDIBits(dc, bitmap, 0, (uint)size, pixels, ref header, 0) == size ? pixels : null;
        }
        finally
        {
            NativeMethods.ReleaseDC(0, dc);
        }
    }
}
