using System;
using System.IO;
using System.IO.Compression;

namespace AprVisual.Render
{
    // Minimal RGB PNG encoder using only the .NET BCL (System.IO.Compression.ZLibStream).
    // Replaces the old System.Drawing.Bitmap save path so the S1 fork can be a pure,
    // portable console app with no Windows-desktop / GDI dependency.
    //
    // Input is the unmanaged ARGB framebuffer (0x00RRGGBB per pixel); we emit colour
    // type 2 (truecolour RGB, 8-bit), filter type 0 (none) on every scanline.
    internal static unsafe class PngWriter
    {
        public static void Write(string path, uint* argb, int width, int height)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);

            // ── signature ──
            fs.Write(stackalloc byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

            // ── IHDR ──
            Span<byte> ihdr = stackalloc byte[13];
            WriteBE(ihdr, 0, (uint)width);
            WriteBE(ihdr, 4, (uint)height);
            ihdr[8]  = 8;   // bit depth
            ihdr[9]  = 2;   // colour type 2 = truecolour RGB
            ihdr[10] = 0;   // compression (deflate)
            ihdr[11] = 0;   // filter method
            ihdr[12] = 0;   // interlace none
            WriteChunk(fs, "IHDR", ihdr);

            // ── raw scanlines: each row = 1 filter byte (0) + width*3 RGB bytes ──
            int rowBytes = width * 3 + 1;
            var raw = new byte[rowBytes * height];
            for (int y = 0; y < height; y++)
            {
                int o = y * rowBytes;
                raw[o++] = 0;   // filter: none
                uint* row = argb + (long)y * width;
                for (int x = 0; x < width; x++)
                {
                    uint p = row[x];
                    raw[o++] = (byte)((p >> 16) & 0xFF);   // R
                    raw[o++] = (byte)((p >> 8)  & 0xFF);   // G
                    raw[o++] = (byte)( p        & 0xFF);   // B
                }
            }

            // ── IDAT: zlib-compress the raw scanlines ──
            byte[] idat;
            using (var ms = new MemoryStream())
            {
                using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                    z.Write(raw, 0, raw.Length);
                idat = ms.ToArray();
            }
            WriteChunk(fs, "IDAT", idat);

            // ── IEND ──
            WriteChunk(fs, "IEND", ReadOnlySpan<byte>.Empty);
        }

        private static void WriteBE(Span<byte> buf, int off, uint v)
        {
            buf[off]     = (byte)(v >> 24);
            buf[off + 1] = (byte)(v >> 16);
            buf[off + 2] = (byte)(v >> 8);
            buf[off + 3] = (byte)v;
        }

        private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
        {
            Span<byte> len = stackalloc byte[4];
            WriteBE(len, 0, (uint)data.Length);
            s.Write(len);

            Span<byte> typeBytes = stackalloc byte[4];
            for (int i = 0; i < 4; i++) typeBytes[i] = (byte)type[i];
            s.Write(typeBytes);
            if (data.Length > 0) s.Write(data);

            uint crc = Crc32(typeBytes, data);
            Span<byte> crcBytes = stackalloc byte[4];
            WriteBE(crcBytes, 0, crc);
            s.Write(crcBytes);
        }

        // PNG CRC-32 over (type || data).
        private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
        {
            uint c = 0xFFFFFFFFu;
            foreach (byte b in type) c = Crc32Table[(c ^ b) & 0xFF] ^ (c >> 8);
            foreach (byte b in data) c = Crc32Table[(c ^ b) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }

        private static readonly uint[] Crc32Table = BuildCrc32Table();
        private static uint[] BuildCrc32Table()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }
    }
}
