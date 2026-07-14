using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AprVisual.Rom;
using AprVisual.Sim;

namespace AprVisual.Test
{
    internal static partial class TestRunner
    {
        // ── Test mode (--test / --test-dir): frame-based validation, verdict detection, input scripting, result JSON. ──
        // ── --test / --test-dir: frame-based test-ROM validation. Detection (per simulated frame):
        //      1. blargg $6000 protocol (signature DE B0 61, status < 0x80 = done, 0 = pass)
        //      2. $6000 == 0x81 → auto soft reset (wait 6 frames, WireCore.SoftReset(), max 10× — apu_reset/cpu_reset)
        //      3. --expected-crc: scan nametable 0 (u4.ram, tile == ASCII) for an isolated 8-hex CRC,
        //         require 2 consecutive identical frames, compare against the accept set (dmc_dma visual tests)
        //    Budget: --max-frames (simulation frames, primary); --max-wait (wall seconds, safety, 0 = off).
        //    Outputs: console PASS/FAIL line (+ --test-json structured record, --test-screenshot final PNG).
        // AccuracyCoin unattended completion block, in the NES internal CPU RAM (see the fork's README):
        //   $07F0-$07F2 magic "DE B0 61" | $07F3 passed | $07F4 total | $07F5 skipped
        private const int AcMagic0 = 0x7F0;
        private const int AcDebugEc = 0x0EC;   // the ROM's Debug_EC menu-init progress byte
        private const int AcStormConfirmFrames = 120;   // ~2 s of console time parked in $06xx = the BRK storm, beyond doubt
        private const int AcDiagnosticStage = AcDebugEc;
        private const int AcCurrentPage = 0x014;   // menuTabXPos: zero-based AccuracyCoin suite page
        private const int AcCurrentItem = 0x016;   // menuCursorYPos: item selected by the all-tests loop
        private const int AcDiagnosticIteration = 0x0ED;   // Copy_X2: caller X saved by RunTest

        private static (int Filled, int LastAddress, int LastValue) ReadAcResultProgress(WireCore.Memory? acRam)
        {
            if (acRam == null) return (0, -1, -1);
            int filled = 0, lastAddress = -1, lastValue = -1;
            for (int address = 0x400; address < 0x500; address++)
            {
                int value = acRam.Read(address);
                if (value == 0) continue;
                filled++;
                lastAddress = address;
                lastValue = value;
            }
            return (filled, lastAddress, lastValue);
        }

        // Full result-table dump at the end of every AC run, whatever the exit path. Exists because
        // of two burns in one day: a "final" tally extrapolated from a mid-run diff (5 late failures
        // missed), and per-test values misread from the last 10-frame snapshot (the run ended between
        // snapshots). The table at verdict time is the only authoritative source -- so print it.
        private static void DumpAcResultTable(WireCore.Memory acRam)
        {
            int passed = 0, failed = 0;
            var sb = new StringBuilder();
            for (int a = 0x400; a < 0x500; a++)
            {
                int v = acRam.Read(a);
                if (v == 0) continue;
                bool pass = (v & 1) != 0;
                if (pass) passed++; else failed++;
                if (!pass || (v != 1))   // failures and non-default pass variants are the informative ones
                    sb.Append($"#   ${a:X3}=${v:X2} {(pass ? $"PASS variant {v >> 2}" : $"FAIL err {v >> 2}")}\n");
            }
            Console.Error.WriteLine($"# [ac] result table at exit: {passed} pass / {failed} fail / {passed + failed} filled");
            if (sb.Length > 0) Console.Error.Write(sb.ToString());
        }

        // Runner-side loop state carried inside a snapshot (opaque to WireCore). v1: the _io_db
        // decay tracker (semantically live — it decides WHEN the open-bus decay fires) and the
        // Debug_EC edge tracker (log cosmetics, but keeps resumed output identical).
        private static byte[] BuildRunnerBlob(int ioPrev, int ioStable, int acEcPrev)
        {
            var b = new byte[13];
            b[0] = 1;   // blob version
            BitConverter.TryWriteBytes(b.AsSpan(1), ioPrev);
            BitConverter.TryWriteBytes(b.AsSpan(5), ioStable);
            BitConverter.TryWriteBytes(b.AsSpan(9), acEcPrev);
            return b;
        }

        private static void ParseRunnerBlob(byte[] b, ref int ioPrev, ref int ioStable, ref int acEcPrev)
        {
            if (b.Length < 13 || b[0] != 1)
            { Console.Error.WriteLine("# [snap] WARNING: unknown runner blob; io_db decay tracker starts fresh"); return; }
            ioPrev   = BitConverter.ToInt32(b, 1);
            ioStable = BitConverter.ToInt32(b, 5);
            acEcPrev = BitConverter.ToInt32(b, 9);
        }

        // One checkpoint of a long run: the current screen, plus a status line appended to progress.jsonl.
        // "latest.png" is an overwritten copy so a watcher always has one stable path to attach.
        private static void WriteProgress(string dir, int frames, long hc, double wall, int ec, WireCore.Memory? acRam)
        {
            try
            {
                unsafe
                {
                    if (WireCore.FrameBuffer != null)
                    {
                        string shot = Path.Combine(dir, $"f{frames:D6}.png");
                        AprVisual.Render.PngWriter.Write(shot, WireCore.FrameBuffer, WireCore.ScreenW, WireCore.ScreenH);
                        File.Copy(shot, Path.Combine(dir, "latest.png"), true);
                    }
                }
                var resultProgress = ReadAcResultProgress(acRam);
                int acStage = acRam?.Read(AcDiagnosticStage) ?? -1;
                int cpuPc = WireCore.ReadReg(WireCore.R_CpuPcl) | (WireCore.ReadReg(WireCore.R_CpuPch) << 8);
                int cpuAb = WireCore.ReadReg(WireCore.R_CpuAb);
                int cpuDb = WireCore.ReadReg(WireCore.R_CpuDb);
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                string line = string.Format(ci,
                    "{{\"frame\":{0},\"simSec\":{1:F2},\"hc\":{2},\"wallSec\":{3:F1},\"secPerFrame\":{4:F2},\"debugEc\":{5},\"acStage\":{6},\"acPage\":{7},\"acItem\":{8},\"acIteration\":{9},\"cpuPc\":{10},\"cpuAb\":{11},\"cpuDb\":{12},\"resultsFilled\":{13},\"lastResultAddress\":{14},\"lastResultValue\":{15},\"utc\":\"{16:s}Z\"}}",
                    frames, frames / 60.0988, hc, wall, wall / frames, ec, acStage,
                    acRam?.Read(AcCurrentPage) ?? -1, acRam?.Read(AcCurrentItem) ?? -1,
                    acRam?.Read(AcDiagnosticIteration) ?? -1, cpuPc, cpuAb, cpuDb,
                    resultProgress.Filled, resultProgress.LastAddress, resultProgress.LastValue, DateTime.UtcNow);
                File.AppendAllText(Path.Combine(dir, "progress.jsonl"), line + "\n");
            }
            catch (Exception ex) { Console.Error.WriteLine($"# (progress checkpoint failed: {ex.Message})"); }
        }

        private static int RunOneTest(string path, int maxWait, string region, bool benchmark)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            var rom = NesRom.LoadFromFile(path);
            if (rom is null) { Console.WriteLine($"FAIL(load) | {Path.GetFileName(path)}"); return 2; }

            // Test mode emulates the conventional console power-up state (palette residue + P=$34;
            // documented shim — see WireCore.ApplyPowerUpState). Benchmarks never set this.
            WireCore.PowerUpStateShim = true;
            WireCore.RegisterRawIdAliases = true;   // for the DMC latch shim's unnamed nodes
            WireCore.EnableJoypadHandler = _joypad;   // per-test (--joypad): behavioral controller + tie-rewire. OFF by default:
                                                      // the module swap + 6 tie rewires are a LOAD-TIME graph change that re-rolls the
                                                      // alignment lottery (regressed ppu_vbl_nmi when it was global). See campaign notes.
            // Must be selected before LoadSystem: the CHR handler needs ALE and /RD in its callback
            // trigger so it refreshes immediately after the analog-feedback window closes.
            WireCore.PpuAleReadFeedbackShim = _acVerdict && !_noShims && !_noPpuAleReadFeedbackShim;
            // Test ROMs speak the blargg $6000 protocol, which lives in cart-extraram. Never infer this from
            // the ROM's path: relocating the ROMs under tools/testrom/roms missed LoadSystem's "nes-test-roms"
            // path heuristic, silently dropped the $6000 RAM, and made every class-A test report
            // detection=none. See MD/ISSUE/2026-07-09-*.
            // AccuracyCoin is the one exception: it never speaks $6000, and a number of its tests MEASURE the
            // open bus at $6000-$7FFF. Mapping RAM there would answer those reads with RAM instead of open bus
            // and silently corrupt the tests, so --ac-verdict leaves the region unmapped.
            WireCore.ForceExtraRam = !_acVerdict;

            const int ResetDelayFrames = 6, MaxAutoResets = 10;
            string status = "timeout", detection = "none", resultText = "";
            int resultCode = -1, frames = 0, resetCount = 0;
            long hcRun = 0;
            double wallSecs = 0, loadSecs = 0;

            try
            {
                var swLoad = System.Diagnostics.Stopwatch.StartNew();
                WireCore.LoadSystem(rom);
                loadSecs = swLoad.Elapsed.TotalSeconds;
                if (WireCore.PpuAleReadFeedbackShim)
                    Console.Error.WriteLine("# [shim] PPU ALE/read feedback armed for cart.chr ROM");
                if (!_noShims)
                {
                    WireCore.EnableDmcLatchShim();   // DMC pcm_latch edge-capture (documented analog-race shim)
                    if (!_noAluShim) WireCore.EnableAluLatchShim();   // ALU input-latch hold (documented analog-race shim)
                    WireCore.EnableLxaMagicShim();   // LXA $AB magic=$FF (documented analog bus-fight shim)
                    WireCore.EnableFrameIrqShim();   // frame-IRQ flag hold (documented intra-settle-transient shim)
                    WireCore.EnablePpuWriteDelay(_ppuWriteDelayHc);   // $2001 write-effect delay (even_odd; GLOBAL, default 16, narrow window vpos261/hpos338-339)
                    if (!_noDbl2007Shim) WireCore.EnableDbl2007Shim();   // $2007 double-read merge (documented analog-propagation shim; zero-footprint)
                    if (_oamDmaPpuBusShim) WireCore.EnableOamDmaPpuBusShim();   // $4014 from PPU I/O bus: hold $2004 write data through OAM /WE (GLOBAL, default on)
                    if (Environment.GetEnvironmentVariable("NO_OB_SHIM") == null) WireCore.EnableOpenBusShim();   // open bus = last transferred byte (see System.cs)
                    if (Environment.GetEnvironmentVariable("NO_DL_SHIM") == null) WireCore.EnableDlShim();   // DL phi2 transparency at $4016/$4017 (see System.cs) -- must follow EnableOpenBusShim
                    if (Environment.GetEnvironmentVariable("NO_ABORT_SHIM") == null) WireCore.EnableDmc4015AbortShim();   // deferred $4015 disable aborts in-flight DMC DMA (see System.cs)
                    if (Environment.GetEnvironmentVariable("NO_R4015_SHIM") == null) WireCore.EnableR4015A1Shim();   // missing a1 input on the R4015 read-decode PLA term (upstream netlist defect; see System.cs)
                }
                var vram = (_expectedCrcs != null || _screenVerdict) ? WireCore.ResolveMemory("u4.ram") : null;

                // --ac-verdict: the NES internal 2K CPU RAM (nes-001 instantiates it as "u1" = SRAM2K).
                var acRam = _acVerdict ? WireCore.ResolveMemory("u1.ram") : null;
                if (_acVerdict && acRam == null) Console.Error.WriteLine("# [ac] WARNING: u1.ram not found -- --ac-verdict is inert");
                int acEcPrev = acRam?.Read(AcDebugEc) ?? 0;
                int acStormFrames = 0;   // consecutive frames with PC parked in the $06xx IRQ-routine page

                if (_progressFrames > 0 && _progressDir != null)
                {
                    Directory.CreateDirectory(_progressDir);   // WriteProgress swallows its exceptions; don't let a missing dir look like "no progress yet"

                    // Start the log clean. WriteProgress APPENDS, so re-running into a directory that still
                    // holds an earlier run would splice the two: frame jumps backwards, wallSec restarts, and
                    // the differenced throughput series reads negative time. A run's data must describe ONE run.
                    string jsonl = Path.Combine(_progressDir, "progress.jsonl");
                    int stalePngs = 0;
                    try
                    {
                        if (File.Exists(jsonl)) File.Delete(jsonl);
                        foreach (string old in Directory.EnumerateFiles(_progressDir, "f??????.png")) { File.Delete(old); stalePngs++; }
                        string latest = Path.Combine(_progressDir, "latest.png");
                        if (File.Exists(latest)) File.Delete(latest);
                    }
                    catch (Exception ex) { Console.Error.WriteLine($"# [progress] WARNING: could not clear stale checkpoints ({ex.Message}) -- data may be mixed"); }

                    Console.Error.WriteLine($"# [progress] checkpoint every {_progressFrames} frames -> {_progressDir}"
                                          + (stalePngs > 0 ? $"  (cleared {stalePngs} stale checkpoints from a previous run)" : ""));
                }

                // Same hazard, worse consequence: a leftover result JSON / screenshot from an earlier run is
                // this run's ONLY completion signal (ac_watch.py polls for the JSON and mails the verdict the
                // moment it appears). Leave one lying around and a fresh run reports a stale result instantly.
                // Delete them up front so their existence always means "THIS run produced them".
                try
                {
                    if (_testJsonPath != null && File.Exists(_testJsonPath)) File.Delete(_testJsonPath);
                    if (_testShotPath != null && File.Exists(_testShotPath)) File.Delete(_testShotPath);
                }
                catch (Exception ex) { Console.Error.WriteLine($"# WARNING: could not clear a stale result file ({ex.Message})"); }

                // PPU open-bus decay shim (test mode only). The real 2C02's io-bus latch (the "decay
                // register") leaks to 0 in ~600 ms when not refreshed (ppu_open_bus readme: "some decay
                // sooner, depending on the NES and temperature"); floating netlist nodes hold forever.
                // Approximation: if the latch value is nonzero and unchanged for ~36 frames (~600 ms),
                // force it to 0 (drive low, settle, release — the dynamic node then float-holds 0).
                var ioDb = new List<int>();
                WireCore.ResolveNodes("ppu._io_db[7:0]", ioDb, quiet: true);   // _io_db = the io data-bus LATCH side (io_db is the live internal bus)
                int[] ioDbN = ioDb.Count == 8 ? ioDb.ToArray() : Array.Empty<int>();
                int ioPrev = -1, ioStable = 0;
                const int IoDecayFrames = 36;
                Console.Error.WriteLine($"# [shim] _io_db decay armed: resolved {ioDb.Count}/8 nodes");

                long t0 = WireCore.Time;
                int resetAtFrame = -1;
                bool waitRestart = false;      // after a soft reset, ignore the stale 0x81 until the ROM writes something else
                string? prevCrc = null;
                string? prevMarker = null;     // B-class: terminal marker seen last frame (2-frame confirm)
                var sw = System.Diagnostics.Stopwatch.StartNew();

                // Scripted controller input (--input): (osButtonIdx, pressFrame, releaseFrame),
                // AprNes-compatible spec "Button:sec[:holdSec]", 60.0988 fps, default hold 10 frames.
                var inputEvents = ParseInputSpec(_inputSpec);
                if (inputEvents.Count > 0 && !WireCore.PadInit()) inputEvents.Clear();

                // --watch in test mode: read-only per-frame node prints. Pairs with --resume for
                // level-triggered hunts: an unacknowledged IRQ line stays asserted, so even coarse
                // per-frame sampling names the culprit.
                var watchNodes = new List<(string Name, int Id)>();
                if (_watchSpec != null)
                    foreach (var w in _watchSpec.Split(','))
                    {
                        int wid = WireCore.LookupNode(w.Trim());
                        if (wid == WireCore.EmptyNode) Console.Error.WriteLine($"# [watch] no node named '{w.Trim()}'");
                        else watchNodes.Add((w.Trim(), wid));
                    }

                // --resume: overwrite the freshly-built system's dynamic state with a snapshot. The
                // build above MUST have used the identical config (same ROM / shims / flags) — the
                // snapshot header records it and LoadState refuses on any mismatch, because a config
                // change re-rolls node ids and the restored state would be garbage on a wrong graph.
                int startFrame = 1;
                if (_resumePath != null)
                {
                    byte[] blob = WireCore.LoadState(_resumePath, out int snapFrame);
                    ParseRunnerBlob(blob, ref ioPrev, ref ioStable, ref acEcPrev);
                    startFrame = snapFrame + 1;
                    t0 = WireCore.Time;   // hc/wall accounting covers only the resumed portion
                    Console.Error.WriteLine($"# [snap] resumed at frame {snapFrame} (t={WireCore.Time:N0}) from {Path.GetFileName(_resumePath)}");
                }

                for (frames = startFrame; frames <= _testMaxFrames; frames++)
                {
                    foreach (var (btn, pf, rf) in inputEvents)
                    {
                        if (frames == pf) { WireCore.PadSetButton(0, btn, true);  Console.Error.WriteLine($"# [pad] frame {frames}: press {ButtonName(btn)}"); }
                        if (frames == rf) { WireCore.PadSetButton(0, btn, false); Console.Error.WriteLine($"# [pad] frame {frames}: release {ButtonName(btn)}"); }
                    }
                    WireCore.RunFrame();

                    // open-bus decay shim (see above): value-change resets the clock; same value for
                    // ~600 ms of simulated time ⇒ the charge leaks away on real silicon.
                    if (!_noShims && ioDbN.Length == 8)
                    {
                        int v = WireCore.ReadBits(ioDbN);
                        if (v != ioPrev) { ioPrev = v; ioStable = 0; }
                        else if (v != 0 && ++ioStable >= IoDecayFrames)
                        {
                            foreach (int n in ioDbN) WireCore.SetLow(n);
                            foreach (int n in ioDbN) WireCore.SetFloat(n);
                            int after = WireCore.ReadBits(ioDbN);
                            Console.Error.WriteLine($"# [shim] _io_db decay fired at frame {frames}: {v:X2} -> {after:X2}{(after != 0 ? "  (DID NOT STICK)" : "")}");
                            ioPrev = after; ioStable = 0;
                        }
                    }

                    // --progress-frames: a long unattended run is otherwise a black box for hours. Checkpoint
                    // the screen and a status line so an outside watcher can report progress and, if the run
                    // goes wrong, show WHERE. Read-only w.r.t. the simulation; off unless asked for.
                    if (watchNodes.Count > 0)
                    {
                        var wsb = new StringBuilder($"# [watch] frame {frames}:");
                        unsafe { foreach (var (wn, wid) in watchNodes) wsb.Append($" {wn}={WireCore.NodeStates[wid]}"); }
                        Console.Error.WriteLine(wsb.ToString());
                    }

                    if (_progressFrames > 0 && _progressDir != null && frames % _progressFrames == 0)
                        WriteProgress(_progressDir, frames, WireCore.Time - t0, sw.Elapsed.TotalSeconds,
                            acRam?.Read(AcDebugEc) ?? -1, acRam);

                    // --snapshot-frames: full engine-state snapshot (see WireCore.Snapshot.cs). Taken here —
                    // after RunFrame and the runner-level shims, at quiescence — so a --resume from this
                    // file continues at frames+1 with bit-identical state. ~380 KB each; a full AccuracyCoin
                    // run at every 10 frames is ~490 files / ~190 MB, and buys minute-scale access to ANY
                    // point of a 7-hour run.
                    if (_snapFrames > 0 && _snapDir != null && frames % _snapFrames == 0)
                    {
                        try
                        {
                            Directory.CreateDirectory(_snapDir);
                            WireCore.SaveState(Path.Combine(_snapDir, $"state_f{frames:D6}.sav"),
                                               frames, BuildRunnerBlob(ioPrev, ioStable, acEcPrev));
                        }
                        catch (Exception ex) { Console.Error.WriteLine($"# [snap] snapshot at frame {frames} FAILED: {ex.Message}"); }
                    }

                    // AccuracyCoin (unattended fork). It speaks neither of the other two protocols: no $6000
                    // handshake, and its CHR font is not ASCII-mapped so the nametable scan cannot read it.
                    // Instead it writes a completion block to CPU RAM, disables NMI and halts with the results
                    // table on screen. See AprAccuracyCoinUnattended/README.md.
                    if (acRam != null)
                    {
                        // Debug_EC ($00EC) is the ROM's own menu-init progress counter; the unattended hook
                        // fires at $0A and parks it at $FF. Tracing it turns "did the auto-run even start?"
                        // into a question answerable in minutes rather than after the full multi-hour sweep.
                        int ec = acRam.Read(AcDebugEc);
                        if (ec != acEcPrev)
                        {
                            Console.Error.WriteLine($"# [ac] frame {frames}: Debug_EC ${acEcPrev:X2} -> ${ec:X2}{(ec == 0xFF ? "  (unattended auto-run entered)" : "")}");
                            acEcPrev = ec;
                        }

                        if (acRam.Read(AcMagic0) == 0xDE && acRam.Read(AcMagic0 + 1) == 0xB0 && acRam.Read(AcMagic0 + 2) == 0x61)
                        {
                            int passed = acRam.Read(AcMagic0 + 3), total = acRam.Read(AcMagic0 + 4), skipped = acRam.Read(AcMagic0 + 5);
                            resultCode = passed == total ? 0 : 1;
                            resultText = $"AccuracyCoin: {passed}/{total} passed, {skipped} skipped";
                            detection  = "ac";
                            status     = resultCode == 0 ? "pass" : "fail";
                            break;
                        }

                        // Epilogue-storm verdict. The ROM's RAM IRQ routine at $0600 is deliberately
                        // overwritten by the $2007 Stress test's data; if a late frame-IRQ fires (S1
                        // loses the ROM's protection-vs-flag cycle race -- see MD/toDoNext3
                        // 2026-07-13 brkstorm postmortem), the CPU BRK-storms in $0600-$0602 forever
                        // and the completion block can never be written. The per-test results ARE
                        // final at that point, so judge from the results table, clearly marked.
                        // Normal code never dwells in the $06xx page for seconds at a time.
                        int pcNow = WireCore.ReadReg(WireCore.R_CpuPcl) | (WireCore.ReadReg(WireCore.R_CpuPch) << 8);
                        if (pcNow >= 0x0600 && pcNow <= 0x06FF) acStormFrames++;
                        else acStormFrames = 0;
                        if (acStormFrames >= AcStormConfirmFrames)
                        {
                            int passed = 0, total = 0;
                            for (int a = 0x400; a < 0x500; a++)
                            {
                                int v = acRam.Read(a);
                                if (v == 0) continue;
                                total++;
                                if ((v & 1) != 0) passed++;   // odd = pass (variant<<2|1); even = (error<<2)|2
                            }
                            resultCode = passed == total ? 0 : 1;
                            resultText = $"AccuracyCoin results table complete: {passed}/{total} passed "
                                       + $"({total - passed} deviations); completion block unwritten -- epilogue frame-IRQ storm (documented)";
                            detection  = "ac-storm";
                            status     = resultCode == 0 ? "pass" : "fail";
                            break;
                        }
                    }

                    var r = WireCore.CheckUnitTest();
                    if (r.Found)
                    {
                        if (r.Complete)   // status < 0x80 → final result
                        {
                            resultCode = r.Code;
                            resultText = r.Text.Trim();
                            detection  = resetCount > 0 ? "6000+reset" : "6000";
                            status     = r.Code == 0 ? "pass" : "fail";
                            break;
                        }
                        // status ≥ 0x80: 0x80 running, 0x81 requests a soft reset (blargg reset protocol)
                        if (r.Code == 0x81 && resetAtFrame < 0 && !waitRestart && resetCount < MaxAutoResets)
                            resetAtFrame = frames;
                        if (waitRestart && r.Code != 0x81)
                            waitRestart = false;
                        if (resetAtFrame >= 0 && frames >= resetAtFrame + ResetDelayFrames)
                        {
                            Console.WriteLine($"# [test] auto soft reset #{resetCount + 1} at frame {frames} ($6000=$81 at frame {resetAtFrame})");
                            WireCore.SoftReset();
                            resetAtFrame = -1;
                            waitRestart = true;
                            resetCount++;
                        }
                    }
                    else if (vram != null)
                    {
                        // No $6000 protocol — judge from the screen (nametable 0, tile == ASCII).
                        string nt = DecodeNametable(vram);

                        if (_expectedCrcs != null)
                        {
                            // C-class: terminal on-screen CRC vs the accept set.
                            string? crc = FindNametableCrc(nt);
                            if (crc != null && crc == prevCrc)   // same isolated 8-hex on 2 consecutive frames
                            {
                                bool ok    = _expectedCrcs.Contains(crc);
                                resultCode = ok ? 0 : 1;
                                resultText = $"screen CRC {crc} " + (ok ? "(matched)" : "(NOT in expected set)");
                                detection  = "crc";
                                status     = ok ? "pass" : "fail";
                                break;
                            }
                            prevCrc = crc;
                        }

                        if (_screenVerdict)
                        {
                            // B-class: terminal Passed/Failed text or old-blargg "$0X" code. Terminal =
                            // printed once and the ROM halts (calibration found no ROM that shows a
                            // marker while still drawing) — 2-frame confirm guards mid-draw snapshots.
                            int vc = FindNametableVerdict(nt, out string? marker);
                            if (vc >= 0 && marker != null && marker == prevMarker)
                            {
                                resultCode = vc;
                                resultText = $"(screen: {marker} = {(vc == 0 ? "passed" : "failed")})";
                                detection  = "screen";
                                status     = vc == 0 ? "pass" : "fail";
                                break;
                            }
                            prevMarker = vc >= 0 ? marker : null;
                        }
                    }

                    if (maxWait > 0 && sw.Elapsed.TotalSeconds > maxWait)
                    {
                        resultText = $"wall-clock safety cap ({maxWait}s) hit at frame {frames}";
                        break;
                    }
                }
                sw.Stop();
                wallSecs = sw.Elapsed.TotalSeconds;
                hcRun = WireCore.Time - t0;
                if (acRam != null) DumpAcResultTable(acRam);   // single exit point: covers ac, ac-storm and timeout alike
                if (frames > _testMaxFrames) frames = _testMaxFrames;   // loop exits at budget+1

                if (status == "timeout")
                {
                    var rr = WireCore.CheckUnitTest();
                    if (resultText.Length == 0)
                        resultText = rr.Found ? $"budget exhausted, $6000=0x{rr.Code:X2}" : "budget exhausted, no $6000 signature";
                    resultCode = 3;
                    if (acRam != null)
                    {
                        var resultProgress = ReadAcResultProgress(acRam);
                        Console.Error.WriteLine($"# [ac] timeout stage=${acRam.Read(AcDiagnosticStage):X2} " +
                            $"page=${acRam.Read(AcCurrentPage):X2} item=${acRam.Read(AcCurrentItem):X2} " +
                            $"iteration=${acRam.Read(AcDiagnosticIteration):X2} " +
                            $"resultsFilled={resultProgress.Filled} lastResult=" +
                            (resultProgress.LastAddress >= 0
                                ? $"${resultProgress.LastAddress:X4}=${resultProgress.LastValue:X2}"
                                : "none"));
                        Console.Error.WriteLine("# [ac] timeout CPU " + WireCore.DumpCpuState());
                    }
                }

                // --shot-delay: run extra frames after the verdict so the ROM can finish PRESENTING its
                // result (the dmc_dma family keeps rendering disabled while it works the $2007 bus and only
                // enables the screen after publishing the verdict text/CRC to VRAM — which the detection
                // reads directly, so the framebuffer lags). Cosmetic only: frames / wallSeconds /
                // halfCycles in the JSON stay at the verdict point.
                if (_testShotDelay > 0 && _testShotPath != null && status != "timeout")
                    for (int i = 0; i < _testShotDelay; i++) WireCore.RunFrame();

                // final-frame screenshot (for the report page)
                if (_testShotPath != null)
                {
                    try
                    {
                        string? dir = Path.GetDirectoryName(_testShotPath);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        unsafe
                        {
                            if (WireCore.FrameBuffer != null)
                                AprVisual.Render.PngWriter.Write(_testShotPath, WireCore.FrameBuffer, WireCore.ScreenW, WireCore.ScreenH);
                        }
                    }
                    catch (Exception ex) { Console.Error.WriteLine($"# (screenshot failed: {ex.Message})"); }
                }

                string label = status switch { "pass" => "PASS", "fail" => $"FAIL({resultCode})", _ => "TIMEOUT" };
                Console.WriteLine($"{label} | {Path.GetFileName(path)} | {name}");
                if (resultText.Length > 0) Console.WriteLine(resultText);
                Console.WriteLine($"# frames={frames} simSec={frames / 60.0988:F1} wallSec={wallSecs:F0} hc={hcRun:N0} detection={detection} resets={resetCount} load={loadSecs:F0}s");
                if (WireCore.PpuAleReadFeedbackShim)
                    Console.WriteLine($"# [shim] PPU ALE/read feedback holds={WireCore.PpuAleReadFeedbackHoldCount:N0}");
                WireCore.DumpPpuMemoryTrace();
                if (_acDumpWork && acRam != null)
                {
                    for (int row = 0x500; row < 0x700; row += 0x10)
                    {
                        var dump = new StringBuilder($"AC_WORK_{row:X4}:");
                        for (int i = 0; i < 0x10; i++) dump.Append($" {acRam.Read(row + i):X2}");
                        Console.WriteLine(dump);
                    }
                }
#if DEBUG
                // Guard telemetry: how much settle↔callback recursion the re-entrancy guard absorbed. In the
                // no-guard build this depth WAS the stack — ~24021 on AccuracyCoin. (DEBUG builds only.)
                Console.WriteLine($"# [guard] nested entries absorbed: total={WireCore.GuardBlockedTotal:N0}  max-per-drain={WireCore.GuardBlockedMax:N0} (= recursion depth avoided)");
#endif

                if (_testJsonPath != null)
                    WriteTestJson(path, status, resultCode, detection, resultText, frames, wallSecs, loadSecs, hcRun, resetCount);

                return status switch { "pass" => 0, "fail" => resultCode == 0 ? 1 : Math.Min(resultCode, 125), _ => 3 };
            }
            finally { WireCore.Shutdown(); }
        }

        // Nametable 0 (u4.ram bytes 0..0x3BF) decoded to ASCII — blargg's CHR maps tile == ASCII.
        private static string DecodeNametable(WireCore.Memory vram)
        {
            int n = Math.Min(0x3C0, vram.Length);
            var s = new char[n];
            for (int i = 0; i < n; i++)
            {
                byte t = vram.Read(i);
                s[i] = t is >= 0x20 and < 0x7F ? (char)t : ' ';
            }
            return new string(s);
        }

        private static bool IsHexChar(char c) => c is (>= '0' and <= '9') or (>= 'A' and <= 'F') or (>= 'a' and <= 'f');

        // Isolated 8-hex-digit string (a CRC32 print). Neighbours must be non-hex so a longer dump can't alias.
        private static string? FindNametableCrc(string s)
        {
            for (int i = 0; i + 8 <= s.Length; i++)
            {
                bool all = true;
                for (int k = 0; k < 8; k++) if (!IsHexChar(s[i + k])) { all = false; break; }
                if (!all) continue;
                if (i > 0 && IsHexChar(s[i - 1])) continue;
                if (i + 8 < s.Length && IsHexChar(s[i + 8])) continue;
                return s.Substring(i, 8).ToUpperInvariant();
            }
            return null;
        }

        // B-class terminal markers. Returns the result code (0 = pass, 1..15 = fail) and the marker
        // label, or -1 when nothing is visible. Two forms exist in the 46-ROM set (per the AprNesRef
        // calibration sweep): "Passed"/"Failed" text (30) and the old-blargg "$0X" code (16;
        // $01 = pass, $02-$0F = fail code).
        private static readonly string[] _padNames = { "A", "B", "Select", "Start", "Up", "Down", "Left", "Right" };
        private static string ButtonName(int i) => i >= 0 && i < 8 ? _padNames[i] : i.ToString();

        private static List<(int Btn, int Press, int Release)> ParseInputSpec(string? spec)
        {
            var events = new List<(int, int, int)>();
            if (string.IsNullOrEmpty(spec)) return events;
            const double Fps = 60.0988;
            foreach (string entry in spec.Split(','))
            {
                var parts = entry.Trim().Split(':');
                if (parts.Length < 2) continue;
                int btn = Array.FindIndex(_padNames, n => n.Equals(parts[0].Trim(), StringComparison.OrdinalIgnoreCase));
                if (btn < 0 || !double.TryParse(parts[1], out double sec)) continue;
                int hold = 10;
                if (parts.Length >= 3 && double.TryParse(parts[2], out double holdSec)) hold = Math.Max(1, (int)(holdSec * Fps));
                int pf = Math.Max(1, (int)(sec * Fps));
                events.Add((btn, pf, pf + hold));
            }
            return events;
        }

        private static int FindNametableVerdict(string s, out string? marker)
        {
            if (s.Contains("Passed") || s.Contains("PASSED")) { marker = "Passed"; return 0; }
            if (s.Contains("Failed") || s.Contains("FAILED")) { marker = "Failed"; return 1; }
            if (_passMarker != null && s.Contains(_passMarker)) { marker = _passMarker; return 0; }
            for (int i = 0; i + 2 < s.Length; i++)
            {
                if (s[i] != '$' || s[i + 1] != '0') continue;
                char c = s[i + 2];
                if (c == '1') { marker = "$01"; return 0; }
                if (c is >= '2' and <= '9') { marker = "$0" + c; return c - '0'; }
                if (c is >= 'A' and <= 'F') { marker = "$0" + c; return 10 + (c - 'A'); }
                if (c is >= 'a' and <= 'f') { marker = "$0" + char.ToUpperInvariant(c); return 10 + (c - 'a'); }
            }
            marker = null;
            return -1;
        }

        // Structured per-test record — consumed by tools/testrom/build_report.py (schema aprvisual-testrom/1).
        private static void WriteTestJson(string romPath, string status, int resultCode, string detection,
                                          string resultText, int frames, double wallSecs, double loadSecs,
                                          long hcRun, int resetCount)
        {
            try
            {
                string? dir = Path.GetDirectoryName(_testJsonPath!);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var (bv, bd) = BuildVersion();
                var sb = new StringBuilder();
                sb.Append("{\n");
                sb.Append("  \"schema\": \"aprvisual-testrom/1\",\n");
                sb.Append($"  \"rom\": \"{Esc(Path.GetFileName(romPath))}\",\n");
                sb.Append($"  \"status\": \"{status}\",\n");
                sb.Append($"  \"resultCode\": {resultCode},\n");
                sb.Append($"  \"detection\": \"{detection}\",\n");
                sb.Append($"  \"resetCount\": {resetCount},\n");
                sb.Append($"  \"frames\": {frames},\n");
                sb.Append($"  \"maxFrames\": {_testMaxFrames},\n");
                sb.Append($"  \"simSeconds\": {frames / 60.0988:F2},\n");
                sb.Append($"  \"wallSeconds\": {wallSecs:F1},\n");
                sb.Append($"  \"loadSeconds\": {loadSecs:F1},\n");
                sb.Append($"  \"halfCycles\": {hcRun},\n");
                sb.Append($"  \"engineVersion\": \"{Esc(bv)}\",\n");
                sb.Append($"  \"commitDate\": \"{Esc(bd)}\",\n");
                sb.Append($"  \"pinned\": {(_pinned ? "true" : "false")},\n");
                sb.Append($"  \"timestampUtc\": \"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss}Z\",\n");
                sb.Append($"  \"screenshot\": \"{Esc(_testShotPath ?? "")}\",\n");
                sb.Append($"  \"resultText\": \"{Esc(resultText)}\"\n");
                sb.Append("}\n");
                File.WriteAllText(_testJsonPath!, sb.ToString());
            }
            catch (Exception ex) { Console.Error.WriteLine($"# (test json write failed: {ex.Message})"); }
        }
    }
}
