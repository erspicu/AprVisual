using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AprVisual.Sim
{
    // ── State snapshot / restore ─────────────────────────────────────────────────────────────
    //
    // Purpose: a long unattended run (AccuracyCoin ≈ 5000 frames ≈ 7 h) must be resumable from
    // any 10-frame checkpoint, so chasing a bug at frame 4800 costs minutes, not hours.
    //
    // What a snapshot contains — the COMPLETE mutable state of the engine (audited 2026-07-12):
    //   NODE  NodeStates[NodeCount]                  per-node 0/1
    //   FLAG  NodeInfos[i].Flags                     drive bits (SetHigh/SetLow), instrument clamps
    //                                                (InstClampLow/High set Gnd/Pwr here), State bit.
    //                                                The rest of NodeInfo is build-time topology.
    //   MEMS  every behavioral Memory buffer         RAM *and* ROM (ROM never mutates, but saving it
    //                                                makes the file self-verifying; ~44 KB)
    //   VIDC  per-callback VidPrev                   the video handler's pclk edge tracker
    //   BANK  _cnromChrBank value                    CNROM only; absent for mapper 0
    //   SHIM  every shim's live state                prev-values / arm registers / clamp node +
    //                                                release deadline / the ODMA queues. Node-id
    //                                                caches are NOT saved — they are deterministic
    //                                                per identical load and re-resolved by Enable*().
    //   RUNR  opaque runner blob                     TestRunner's loop state (io_db decay tracker …)
    //   FRMB  FrameBuffer                            256x240 ARGB (makes byte-compare self-tests
    //                                                also validate DoVideo continuity)
    //
    // Deliberately EXCLUDED (documented, so the next auditor doesn't wonder):
    //   - build-time topology (TransistorList*, NodeTlist*, FlagsToState, _callbackByNode, node-id
    //     caches inside shims): identical by construction for an identical (ROM, system-def, config)
    //     load — the header records that config and LoadState refuses on mismatch.
    //   - transient settle state (RecalcList/Hash, _pendingCallbacks, _invoking): a snapshot is only
    //     legal at quiescence; SaveState verifies emptiness and LoadState re-zeros them.
    //   - diagnostics (SettleCalls, BfsWalks, GuardBlocked*, trace hooks): no simulation effect.
    //
    // A snapshot is only taken at a frame boundary (after RunFrame + the runner's shims), where the
    // netlist is quiescent. Restore flow: build the system EXACTLY as the original run did
    // (LoadSystem + the same Enable* calls — same graph, same node ids), then LoadState overwrites
    // all dynamic state. Bit-exactness is provable: snapshot(resumed run @ F) must equal
    // snapshot(original run @ F) byte for byte — files carry no timestamps.
    internal static unsafe partial class WireCore
    {
        // v2 (2026-07-13): appends the LAE ($BB) shim's live state to the SHIM section. The TSX-wait
        // window spans ~2 frames, so a frame-boundary snapshot CAN land inside it — without these
        // fields a resumed run would miss the deferred S fix and diverge. v1 files (the run-4 lineage)
        // load fine with all-zero defaults: they were taken by a pre-LAE-shim engine.
        private const uint SnapVersion = 2;
        private static readonly byte[] SnapMagic = Encoding.ASCII.GetBytes("APRSNAP1");

        // ── public API ──────────────────────────────────────────────────────────────────────

        /// <summary>Write the complete engine state to <paramref name="path"/> (atomic: tmp+move).
        /// Throws if the engine is not quiescent — a snapshot mid-settle would be unrestorable.</summary>
        public static void SaveState(string path, int frame, byte[] runnerBlob)
        {
            if (RecalcListCount != 0 || RecalcListNextCount != 0)
                throw new InvalidOperationException($"SaveState: recalc queue not empty ({RecalcListCount}/{RecalcListNextCount}) — not at quiescence");
            if (_pendingCallbacks.Count != 0)
                throw new InvalidOperationException($"SaveState: {_pendingCallbacks.Count} pending callbacks — not at quiescence");
            if (_invoking)
                throw new InvalidOperationException("SaveState: called from inside InvokeCallbacks");
            foreach (var cb in _callbacks)
                if (cb.Enqueued) throw new InvalidOperationException($"SaveState: callback '{cb.Name}' still enqueued");

            string tmp = path + ".tmp";
            using (var ms = new MemoryStream())
            {
                using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
                {
                    w.Write(SnapMagic);
                    w.Write(SnapVersion);

                    // ── header: identity + config fingerprint (LoadState refuses on any mismatch) ──
                    w.Write(frame);
                    w.Write(Time);
                    w.Write(NodeCount);
                    w.Write(TransistorCount);
                    w.Write(RomFingerprint());
                    w.Write(PowerUpStateShim);
                    w.Write(LxaMagicShim); w.Write(FrameIrqShim); w.Write(Dbl2007Shim);
                    w.Write(OamDmaPpuBusShim); w.Write(PpuAleReadFeedbackShim);
                    w.Write(ForceExtraRam); w.Write(EnableJoypadHandler);
                    w.Write(ResetHoldExtraHc);

                    // ── NODE ──
                    WriteTag(w, "NODE", NodeCount);
                    w.Write(new ReadOnlySpan<byte>(NodeStates, NodeCount));

                    // ── FLAG (NodeInfo offset 0, stride sizeof(NodeInfo)) ──
                    WriteTag(w, "FLAG", NodeCount);
                    for (int i = 0; i < NodeCount; i++) w.Write((byte)NodeInfos[i].Flags);

                    // ── MEMS (sorted by name for deterministic bytes) ──
                    var memNames = _memories.Keys.OrderBy(n => n, StringComparer.Ordinal).ToArray();
                    using (var sec = BeginSection())
                    {
                        var sw = sec.Writer;
                        sw.Write(memNames.Length);
                        foreach (var name in memNames)
                        {
                            var m = _memories[name];
                            sw.Write(name);
                            sw.Write(m.Length);
                            sw.Write(new ReadOnlySpan<byte>(m.Data, m.Length));
                        }
                        EndSection(w, "MEMS", sec);
                    }

                    // ── VIDC ──
                    using (var sec = BeginSection())
                    {
                        sec.Writer.Write(_callbacks.Count);
                        foreach (var cb in _callbacks) sec.Writer.Write(cb.VidPrev);
                        EndSection(w, "VIDC", sec);
                    }

                    // ── BANK ──
                    using (var sec = BeginSection())
                    {
                        sec.Writer.Write(_cnromChrBank != null);
                        if (_cnromChrBank != null) sec.Writer.Write(*_cnromChrBank);
                        EndSection(w, "BANK", sec);
                    }

                    // ── SHIM (fixed order; values written unconditionally — enable flags live in the header) ──
                    using (var sec = BeginSection())
                    {
                        var sw = sec.Writer;
                        sw.Write(_lxaPrevPhi2); sw.Write(_lxaArm); sw.Write(_lxaImm);      // LXA magic
                        sw.Write(_lxaPrevSync);
                        sw.Write(_fiPrev);                                                 // frame IRQ
                        sw.Write(_d27Prev); sw.Write(_d27Clamped); sw.Write(_d27Phi2Prev); // dbl-$2007
                        sw.Write(_d27T0);
                        Span<byte> emptyOdmaQueue = stackalloc byte[OamDmaQueueCapacity];
                        emptyOdmaQueue.Clear();
                        sw.Write(_odmaValueQ != null ? new ReadOnlySpan<byte>(_odmaValueQ, OamDmaQueueCapacity) : emptyOdmaQueue);
                        sw.Write(_odmaAddrQ != null ? new ReadOnlySpan<byte>(_odmaAddrQ, OamDmaQueueCapacity) : emptyOdmaQueue); // OAM-DMA PPU-bus
                        for (int i = 0; i < 16; i++) sw.Write(_odmaDriven != null ? _odmaDriven[i] : 0);
                        sw.Write(_odmaPrevPhi2); sw.Write(_odmaPrevNWe); sw.Write(_odmaPendingPpuGet);
                        sw.Write(_odmaQHead); sw.Write(_odmaQCount); sw.Write(_odmaDrivenCount);
                        sw.Write(_odmaLastActivity);
                        sw.Write(PpuAleReadFeedbackHoldCount);                             // ALE/read feedback (log gating)
                        sw.Write(_ppuAleReadFeedbackLastLogTime);
                        sw.Write(_joyArmed);                                               // behavioral joypad
                        sw.Write(_laeRecent); sw.Write(_laeSbsSeen);                       // v2: LAE $BB shim live state
                        sw.Write(_laeVal); sw.Write(_laeOldS); sw.Write(_laeWait);
                        sw.Write(_laePrevSbs); sw.Write(_laePrevAcs); sw.Write(_laeDbPrevFall);
                        sw.Write(LaeRecording); sw.Write(LaeReadCount);
                        for (int i = 0; i < 16; i++)
                        {
                            sw.Write(LaeReadAddr != null ? LaeReadAddr[i] : 0);
                            sw.Write(LaeReadVal != null ? LaeReadVal[i] : 0);
                        }
                        EndSection(w, "SHIM", sec);
                    }

                    // ── RUNR (opaque) ──
                    WriteTag(w, "RUNR", runnerBlob.Length);
                    w.Write(runnerBlob);

                    // ── FRMB ──
                    int fbLen = FrameBuffer != null ? ScreenW * ScreenH * 4 : 0;
                    WriteTag(w, "FRMB", fbLen);
                    if (fbLen > 0) w.Write(new ReadOnlySpan<byte>((byte*)FrameBuffer, fbLen));

                    w.Write(Encoding.ASCII.GetBytes("END!"));
                }
                // trailer CRC over everything so far — a torn/corrupt file can never restore silently
                byte[] payload = ms.ToArray();
                using var f = new FileStream(tmp, FileMode.Create, FileAccess.Write);
                f.Write(payload, 0, payload.Length);
                Span<byte> crc = stackalloc byte[4];
                BitConverter.TryWriteBytes(crc, Crc32(payload));
                f.Write(crc);
            }
            File.Move(tmp, path, overwrite: true);
        }

        /// <summary>Restore state saved by <see cref="SaveState"/>. The system must already be built
        /// with the IDENTICAL configuration (same ROM, system-def, shim enables) — the header is
        /// verified and any mismatch throws. Returns the snapshot's frame number and runner blob.</summary>
        public static byte[] LoadState(string path, out int frame)
        {
            byte[] file = File.ReadAllBytes(path);
            if (file.Length < 16) throw new InvalidDataException($"LoadState: {path} too short");
            uint want = BitConverter.ToUInt32(file, file.Length - 4);
            uint got = Crc32(file.AsSpan(0, file.Length - 4));
            if (want != got) throw new InvalidDataException($"LoadState: CRC mismatch (file 0x{want:X8}, computed 0x{got:X8}) — torn or corrupt snapshot");

            using var r = new BinaryReader(new MemoryStream(file, 0, file.Length - 4), Encoding.UTF8);
            if (!r.ReadBytes(8).AsSpan().SequenceEqual(SnapMagic)) throw new InvalidDataException("LoadState: bad magic");
            uint ver = r.ReadUInt32();
            if (ver != 1 && ver != SnapVersion) throw new InvalidDataException($"LoadState: version {ver}, expected 1..{SnapVersion}");

            frame = r.ReadInt32();
            long time = r.ReadInt64();
            Expect(r.ReadInt32(), NodeCount, "NodeCount");
            Expect(r.ReadInt32(), TransistorCount, "TransistorCount");
            Expect((int)r.ReadUInt32(), (int)RomFingerprint(), "ROM fingerprint");
            ExpectB(r.ReadBoolean(), PowerUpStateShim, nameof(PowerUpStateShim));
            ExpectB(r.ReadBoolean(), LxaMagicShim, nameof(LxaMagicShim));
            ExpectB(r.ReadBoolean(), FrameIrqShim, nameof(FrameIrqShim));
            ExpectB(r.ReadBoolean(), Dbl2007Shim, nameof(Dbl2007Shim));
            ExpectB(r.ReadBoolean(), OamDmaPpuBusShim, nameof(OamDmaPpuBusShim));
            ExpectB(r.ReadBoolean(), PpuAleReadFeedbackShim, nameof(PpuAleReadFeedbackShim));
            ExpectB(r.ReadBoolean(), ForceExtraRam, nameof(ForceExtraRam));
            ExpectB(r.ReadBoolean(), EnableJoypadHandler, nameof(EnableJoypadHandler));
            Expect(r.ReadInt32(), ResetHoldExtraHc, nameof(ResetHoldExtraHc));

            // ── NODE ──
            ReadTag(r, "NODE", NodeCount);
            r.BaseStream.ReadExactly(new Span<byte>(NodeStates, NodeCount));

            // ── FLAG ──
            ReadTag(r, "FLAG", NodeCount);
            for (int i = 0; i < NodeCount; i++) NodeInfos[i].Flags = (NodeFlags)r.ReadByte();

            // ── MEMS ──
            ReadTag(r, "MEMS", -1);
            int memCount = r.ReadInt32();
            var memNames = _memories.Keys.OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Expect(memCount, memNames.Length, "memory count");
            for (int i = 0; i < memCount; i++)
            {
                string name = r.ReadString();
                if (name != memNames[i]) throw new InvalidDataException($"LoadState: memory #{i} is '{name}', expected '{memNames[i]}'");
                var m = _memories[name];
                Expect(r.ReadInt32(), m.Length, $"memory '{name}' length");
                r.BaseStream.ReadExactly(new Span<byte>(m.Data, m.Length));
            }

            // ── VIDC ──
            ReadTag(r, "VIDC", -1);
            Expect(r.ReadInt32(), _callbacks.Count, "callback count");
            foreach (var cb in _callbacks) { cb.VidPrev = r.ReadBoolean(); cb.Enqueued = false; }

            // ── BANK ──
            ReadTag(r, "BANK", -1);
            bool hasBank = r.ReadBoolean();
            ExpectB(hasBank, _cnromChrBank != null, "CNROM bank presence");
            if (hasBank) *_cnromChrBank = r.ReadInt32();

            // ── SHIM ──
            ReadTag(r, "SHIM", -1);
            _lxaPrevPhi2 = r.ReadInt32(); _lxaArm = r.ReadInt32(); _lxaImm = r.ReadInt32();
            _lxaPrevSync = r.ReadBoolean();
            _fiPrev = r.ReadByte();
            _d27Prev = r.ReadInt32(); _d27Clamped = r.ReadInt32(); _d27Phi2Prev = r.ReadInt32();
            _d27T0 = r.ReadInt64();
            Span<byte> emptyOdmaQueue = stackalloc byte[OamDmaQueueCapacity];
            r.BaseStream.ReadExactly(_odmaValueQ != null ? new Span<byte>(_odmaValueQ, OamDmaQueueCapacity) : emptyOdmaQueue);
            r.BaseStream.ReadExactly(_odmaAddrQ != null ? new Span<byte>(_odmaAddrQ, OamDmaQueueCapacity) : emptyOdmaQueue);
            for (int i = 0; i < 16; i++)
            {
                int driven = r.ReadInt32();
                if (_odmaDriven != null) _odmaDriven[i] = driven;
            }
            _odmaPrevPhi2 = r.ReadInt32(); _odmaPrevNWe = r.ReadInt32(); _odmaPendingPpuGet = r.ReadInt32();
            _odmaQHead = r.ReadInt32(); _odmaQCount = r.ReadInt32(); _odmaDrivenCount = r.ReadInt32();
            _odmaLastActivity = r.ReadInt64();
            PpuAleReadFeedbackHoldCount = r.ReadInt64();
            _ppuAleReadFeedbackLastLogTime = r.ReadInt64();
            _joyArmed = r.ReadBoolean();
            if (ver >= 2)
            {
                _laeRecent = r.ReadInt32(); _laeSbsSeen = r.ReadBoolean();
                _laeVal = r.ReadInt32(); _laeOldS = r.ReadInt32(); _laeWait = r.ReadInt32();
                _laePrevSbs = r.ReadInt32(); _laePrevAcs = r.ReadInt32(); _laeDbPrevFall = r.ReadInt32();
                LaeRecording = r.ReadBoolean(); LaeReadCount = r.ReadInt32();
                for (int i = 0; i < 16; i++)
                {
                    int addr = r.ReadInt32();
                    int val = r.ReadInt32();
                    if (LaeReadAddr != null) LaeReadAddr[i] = addr;
                    if (LaeReadVal != null) LaeReadVal[i] = val;
                }
            }
            else
            {   // v1: pre-LAE-shim engine — quiescent defaults are faithful
                _laeRecent = 0; _laeSbsSeen = false; _laeVal = -1; _laeOldS = -1; _laeWait = 0;
                _laePrevSbs = 0; _laePrevAcs = 0; _laeDbPrevFall = -1;
                LaeRecording = false; LaeReadCount = 0;
            }

            // ── RUNR ──
            int runrLen = ReadTag(r, "RUNR", -1);
            byte[] runnerBlob = r.ReadBytes(runrLen);

            // ── FRMB ──
            int fbLen = ReadTag(r, "FRMB", -1);
            if (fbLen > 0)
            {
                Expect(fbLen, ScreenW * ScreenH * 4, "framebuffer length");
                if (FrameBuffer != null) r.BaseStream.ReadExactly(new Span<byte>((byte*)FrameBuffer, fbLen));
                else r.BaseStream.Seek(fbLen, SeekOrigin.Current);
            }

            if (!r.ReadBytes(4).AsSpan().SequenceEqual(Encoding.ASCII.GetBytes("END!")))
                throw new InvalidDataException("LoadState: missing END marker");

            // transient state: guaranteed-clean start
            RecalcListCount = 0; RecalcListNextCount = 0;
            new Span<byte>(RecalcHash, NodeCount).Clear();
            new Span<byte>(RecalcHashNext, NodeCount).Clear();
            _pendingCallbacks.Clear(); _processingCallbacks.Clear();
            _invoking = false;
            Time = time;
            return runnerBlob;
        }

        // ── helpers ─────────────────────────────────────────────────────────────────────────

        // PRG+CHR content fingerprint — ties a snapshot to the exact ROM it was taken under.
        private static uint RomFingerprint()
        {
            if (_rom == null) return 0;
            uint c = Crc32(_rom.PrgRom);
            // combine (not concat) so PRG/CHR boundaries can't alias
            return Crc32(_rom.ChrRom) ^ (c * 31) ^ (uint)_rom.Mapper;
        }

        private static void WriteTag(BinaryWriter w, string tag, int len)
        { w.Write(Encoding.ASCII.GetBytes(tag)); w.Write(len); }

        private static int ReadTag(BinaryReader r, string tag, int expectLen)
        {
            var got = Encoding.ASCII.GetString(r.ReadBytes(4));
            if (got != tag) throw new InvalidDataException($"LoadState: section '{got}', expected '{tag}'");
            int len = r.ReadInt32();
            if (expectLen >= 0 && len != expectLen)
                throw new InvalidDataException($"LoadState: section {tag} length {len}, expected {expectLen}");
            return len;
        }

        private sealed class Section : IDisposable
        {
            public readonly MemoryStream Stream = new();
            public readonly BinaryWriter Writer;
            public Section() { Writer = new BinaryWriter(Stream, Encoding.UTF8, leaveOpen: true); }
            public void Dispose() { Writer.Dispose(); Stream.Dispose(); }
        }
        private static Section BeginSection() => new();
        private static void EndSection(BinaryWriter w, string tag, Section s)
        {
            s.Writer.Flush();
            WriteTag(w, tag, (int)s.Stream.Length);
            w.Write(s.Stream.GetBuffer(), 0, (int)s.Stream.Length);
        }

        private static void Expect(int got, int want, string what)
        { if (got != want) throw new InvalidDataException($"LoadState: {what} is {got}, current build has {want} — snapshot taken under a different configuration"); }
        private static void ExpectB(bool got, bool want, string what)
        { if (got != want) throw new InvalidDataException($"LoadState: {what} was {got}, current run has {want} — config mismatch re-rolls the netlist; refusing"); }

        // CRC-32 (IEEE 802.3), table-based; used for the file trailer and the ROM fingerprint.
        private static uint[]? _crcTable;
        private static uint Crc32(ReadOnlySpan<byte> data)
        {
            if (_crcTable == null)
            {
                _crcTable = new uint[256];
                for (uint i = 0; i < 256; i++)
                {
                    uint c = i;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    _crcTable[i] = c;
                }
            }
            uint crc = 0xFFFFFFFFu;
            foreach (byte b in data) crc = _crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }
    }
}
