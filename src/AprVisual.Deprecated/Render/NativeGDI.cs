using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace AprVisual.Render
{
    /// <summary>
    /// Direct GDI blit of an unmanaged ARGB framebuffer onto a control's HDC, via
    /// SetDIBitsToDevice / StretchDIBits. Lifted from ref/AprNes/tool/NativeRendering.cs.
    ///
    /// No PictureBox, no Bitmap.Image, no Invalidate/Paint — fastest GDI path.
    /// The source buffer is unmanaged (NativeMemory) so no pinning / GC involvement.
    ///
    /// Usage:
    ///   NativeGDI.Init(panel.CreateGraphics(), 256, 240, fbPtr, scale);
    ///   ... each frame: NativeGDI.Present();
    ///   ... on resize/close: NativeGDI.Free();
    /// </summary>
    internal static unsafe class NativeGDI
    {
        private static Graphics? _grDest;
        private static IntPtr _hdcDest = IntPtr.Zero;
        private static IntPtr _dataPtr = IntPtr.Zero;
        private static BITMAPINFO _info;

        private static int _srcW, _srcH;
        private static int _dstW, _dstH;
        private static int _locX, _locY;
        private static bool _stretch;

        /// <param name="grDest">Graphics from the target control (e.g. panel.CreateGraphics()).</param>
        /// <param name="width">Source framebuffer width  (256 for NES).</param>
        /// <param name="height">Source framebuffer height (240 for NES).</param>
        /// <param name="data">Pointer to width*height uint ARGB pixels (0x00RRGGBB; alpha ignored by GDI).</param>
        /// <param name="scale">Integer display scale; 1 = SetDIBitsToDevice, &gt;1 = StretchDIBits.</param>
        /// <param name="dx">Destination X on the control.</param>
        /// <param name="dy">Destination Y on the control.</param>
        public static void Init(Graphics grDest, int width, int height, uint* data, int scale = 1, int dx = 0, int dy = 0)
        {
            Free();

            _grDest = grDest;
            _hdcDest = grDest.GetHdc();

            _srcW = width;
            _srcH = height;
            scale = scale < 1 ? 1 : scale;
            _dstW = width * scale;
            _dstH = height * scale;
            _stretch = scale != 1;
            _locX = dx;
            _locY = dy;
            _dataPtr = (IntPtr)data;

            _info = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height,             // top-down DIB: first pixel = top-left
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BITMAPINFOHEADER.BI_RGB,
                    biSizeImage = (uint)(width * height * 4),
                },
                bmiColors = new RGBQUAD[1],
            };
        }

        /// <summary>Swap in a new source pointer (e.g. double-buffering) without re-init.</summary>
        public static void UpdateDataPtr(uint* newPtr) => _dataPtr = (IntPtr)newPtr;

        /// <summary>Blit the current framebuffer to the control's HDC. Call from the UI thread.</summary>
        public static void Present()
        {
            if (_hdcDest == IntPtr.Zero || _dataPtr == IntPtr.Zero) return;

            if (_stretch)
            {
                NativeApi.StretchDIBits(
                    _hdcDest, _locX, _locY, _dstW, _dstH,
                    0, 0, _srcW, _srcH,
                    _dataPtr, ref _info, NativeApi.DIB_RGB_COLORS, NativeApi.SRCCOPY);
            }
            else
            {
                NativeApi.SetDIBitsToDevice(
                    _hdcDest, _locX, _locY, (uint)_srcW, (uint)_srcH,
                    0, 0, 0, (uint)_srcH,
                    _dataPtr, ref _info, NativeApi.DIB_RGB_COLORS);
            }
        }

        public static void Free()
        {
            if (_hdcDest != IntPtr.Zero)
            {
                try { _grDest?.ReleaseHdc(_hdcDest); } catch { /* ignore */ }
                _hdcDest = IntPtr.Zero;
            }
            _grDest = null;
            _dataPtr = IntPtr.Zero;
        }
    }
}
