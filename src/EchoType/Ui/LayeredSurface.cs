using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using EchoType.Native;

namespace EchoType.Ui;

/// <summary>
/// Pushes a 32-bit ARGB bitmap onto a WS_EX_LAYERED form so the HUD can have
/// true per-pixel alpha (rounded corners, soft shadow) without a color-key halo.
/// </summary>
internal static class LayeredSurface {

    public static void Present(Form form, Bitmap bitmap, byte opacity) {
        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        IntPtr memDc = NativeMethods.CreateCompatibleDC(screenDc);
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;
        try {
            hBitmap = CreatePremultipliedDib(bitmap);
            oldBitmap = NativeMethods.SelectObject(memDc, hBitmap);
            var size = new NativeMethods.SIZE { cx = bitmap.Width, cy = bitmap.Height };
            var source = new NativeMethods.POINT { x = 0, y = 0 };
            var dest = new NativeMethods.POINT { x = form.Left, y = form.Top };
            var blend = new NativeMethods.BLENDFUNCTION {
                BlendOp = NativeMethods.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = opacity,
                AlphaFormat = NativeMethods.AC_SRC_ALPHA,
            };
            NativeMethods.UpdateLayeredWindow(
                form.Handle, screenDc, ref dest, ref size, memDc, ref source,
                0, ref blend, NativeMethods.ULW_ALPHA);
        } finally {
            if (oldBitmap != IntPtr.Zero) {
                NativeMethods.SelectObject(memDc, oldBitmap);
            }
            if (hBitmap != IntPtr.Zero) {
                NativeMethods.DeleteObject(hBitmap);
            }
            NativeMethods.DeleteDC(memDc);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static IntPtr CreatePremultipliedDib(Bitmap bitmap) {
        var info = new NativeMethods.BITMAPINFO {
            bmiHeader = new NativeMethods.BITMAPINFOHEADER {
                biSize = Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = bitmap.Width,
                biHeight = -bitmap.Height, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = NativeMethods.BI_RGB,
            },
        };
        IntPtr dib = NativeMethods.CreateDIBSection(
            IntPtr.Zero, ref info, NativeMethods.DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero || bits == IntPtr.Zero) {
            throw new InvalidOperationException("Could not create layered bitmap.");
        }

        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try {
            CopyPremultiplied(data.Scan0, bits, data.Stride, bitmap.Width, bitmap.Height);
        } finally {
            bitmap.UnlockBits(data);
        }
        return dib;
    }

    /// <summary>
    /// GDI+ ARGB is not premultiplied; UpdateLayeredWindow with AC_SRC_ALPHA expects
    /// premultiplied BGRA. Convert while copying so edges stay anti-aliased.
    /// </summary>
    private static void CopyPremultiplied(IntPtr source, IntPtr dest, int stride, int width, int height) {
        int destStride = width * 4;
        byte[] row = new byte[Math.Max(Math.Abs(stride), destStride)];
        for (int y = 0; y < height; y++) {
            Marshal.Copy(source + y * stride, row, 0, destStride);
            for (int x = 0; x < width; x++) {
                int i = x * 4;
                byte a = row[i + 3];
                if (a == 0) {
                    row[i] = 0;
                    row[i + 1] = 0;
                    row[i + 2] = 0;
                    continue;
                }
                if (a < 255) {
                    row[i] = (byte)(row[i] * a / 255);
                    row[i + 1] = (byte)(row[i + 1] * a / 255);
                    row[i + 2] = (byte)(row[i + 2] * a / 255);
                }
            }
            Marshal.Copy(row, 0, dest + y * destStride, destStride);
        }
    }
}
