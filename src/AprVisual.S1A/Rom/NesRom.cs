using System;
using System.IO;

namespace AprVisual.Rom
{
    /// <summary>
    /// Minimal iNES (.nes) ROM loader. S1 scope: NROM (mapper 0) + CNROM (mapper 3, behavioral
    /// CHR bank latch in WireCore.Handlers.cs); other mappers are rejected by LoadSystem. Modeled on ref/AprNes/NesCore/nesrom.cpp
    /// (and ref/metalnes-main/source/metalnes/nesrom.cpp).
    /// </summary>
    internal sealed class NesRom
    {
        public string Path = "";
        public string Name = "";

        public int PrgCount16K;     // header[4] — PRG ROM size in 16 KB units
        public int ChrCount8K;      // header[5] — CHR ROM size in 8 KB units (0 = CHR RAM)
        public byte Flags6;         // header[6]
        public byte Flags7;         // header[7]

        public byte[] PrgRom = Array.Empty<byte>();
        public byte[] ChrRom = Array.Empty<byte>();   // empty => cartridge uses CHR RAM

        public int Mapper => (Flags6 >> 4) | (Flags7 & 0xF0);
        public bool HorizontalMirroring => (Flags6 & 1) == 0;
        public bool HasTrainer => (Flags6 & 4) != 0;

        public static NesRom? LoadFromFile(string path)
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch { return null; }
            if (bytes.Length < 16) return null;
            if (bytes[0] != 'N' || bytes[1] != 'E' || bytes[2] != 'S' || bytes[3] != 0x1A) return null;

            var rom = new NesRom
            {
                Path = path,
                Name = System.IO.Path.GetFileName(path),
                PrgCount16K = bytes[4],
                ChrCount8K = bytes[5],
                Flags6 = bytes[6],
                Flags7 = bytes[7],
            };

            int offset = 16;
            if (rom.HasTrainer) offset += 512;          // 512-byte trainer, ignored

            int prgSize = rom.PrgCount16K * 16 * 1024;
            int chrSize = rom.ChrCount8K * 8 * 1024;
            if (offset + prgSize > bytes.Length) return null;

            rom.PrgRom = new byte[prgSize];
            Array.Copy(bytes, offset, rom.PrgRom, 0, prgSize);
            offset += prgSize;

            if (chrSize > 0 && offset + chrSize <= bytes.Length)
            {
                rom.ChrRom = new byte[chrSize];
                Array.Copy(bytes, offset, rom.ChrRom, 0, chrSize);
            }

            return rom;
        }
    }
}
