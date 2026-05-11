using System;
using System.Runtime.InteropServices;

namespace AprVisual.Render
{
    // gdi32 P/Invoke + DIB structs, lifted from ref/AprNes/tool/NativeAPIShare.cs (GDI part).
    internal static class NativeApi
    {
        [DllImport("gdi32.dll", EntryPoint = "SelectObject")]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

        [DllImport("gdi32.dll", EntryPoint = "DeleteObject")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteObject(IntPtr ho);

        [DllImport("gdi32.dll")]
        public static extern int SetDIBitsToDevice(
            IntPtr hdc, int xDest, int yDest, uint dwWidth, uint dwHeight,
            int xSrc, int ySrc, uint uStartScan, uint cScanLines,
            IntPtr lpvBits, ref BITMAPINFO lpbmi, uint fuColorUse);

        // For scaled display (Panel larger than 256x240).
        [DllImport("gdi32.dll")]
        public static extern int StretchDIBits(
            IntPtr hdc, int xDest, int yDest, int destWidth, int destHeight,
            int xSrc, int ySrc, int srcWidth, int srcHeight,
            IntPtr lpBits, ref BITMAPINFO lpBmi, uint iUsage, uint rop);

        public const uint DIB_RGB_COLORS = 0;
        public const uint SRCCOPY = 0x00CC0020;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;

        // One dummy RGBQUAD; not used for 32bpp BI_RGB but keeps the struct shape sane.
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1, ArraySubType = UnmanagedType.Struct)]
        public RGBQUAD[] bmiColors;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RGBQUAD
    {
        public byte rgbBlue;
        public byte rgbGreen;
        public byte rgbRed;
        public byte rgbReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;       // negative => top-down DIB (first pixel = top-left)
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression; // BI_RGB = 0
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;

        public const uint BI_RGB = 0;
    }
}
