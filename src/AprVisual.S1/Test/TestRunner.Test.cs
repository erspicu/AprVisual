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
        private static int RunOneTest(string path, int maxWait, string region, bool benchmark)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            var rom = NesRom.LoadFromFile(path);
            if (rom is null) { Console.WriteLine($"FAIL(load) | {Path.GetFileName(path)}"); return 2; }

            // Test mode emulates the conventional console power-up state (palette residue + P=$34;
            // documented shim — see WireCore.ApplyPowerUpState). Benchmarks never set this.
            WireCore.PowerUpStateShim = true;
            WireCore.RegisterRawIdAliases = true;   // for the DMC latch shim's unnamed nodes
            WireCore.EnableJoypadHandler = true;    // behavioral controller (input injection + faithful bus traffic)

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
                WireCore.EnableDmcLatchShim();   // DMC pcm_latch edge-capture (documented analog-race shim)
                if (!_noAluShim) WireCore.EnableAluLatchShim();   // ALU input-latch hold (documented analog-race shim)
                WireCore.EnableLxaMagicShim();   // LXA $AB magic=$FF (documented analog bus-fight shim)
                WireCore.EnableFrameIrqShim();   // frame-IRQ flag hold (documented intra-settle-transient shim)
                WireCore.EnablePpuWriteDelay(_ppuWriteDelayHc);   // $2001 write-effect delay (even_odd campaign; 0=off)
                if (!_noDbl2007Shim) WireCore.EnableDbl2007Shim();   // $2007 double-read merge (documented analog-propagation shim; zero-footprint)
                var vram = (_expectedCrcs != null || _screenVerdict) ? WireCore.ResolveMemory("u4.ram") : null;

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

                for (frames = 1; frames <= _testMaxFrames; frames++)
                {
                    foreach (var (btn, pf, rf) in inputEvents)
                    {
                        if (frames == pf) { WireCore.PadSetButton(0, btn, true);  Console.Error.WriteLine($"# [pad] frame {frames}: press {ButtonName(btn)}"); }
                        if (frames == rf) { WireCore.PadSetButton(0, btn, false); Console.Error.WriteLine($"# [pad] frame {frames}: release {ButtonName(btn)}"); }
                    }
                    WireCore.RunFrame();

                    // open-bus decay shim (see above): value-change resets the clock; same value for
                    // ~600 ms of simulated time ⇒ the charge leaks away on real silicon.
                    if (ioDbN.Length == 8)
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
                if (frames > _testMaxFrames) frames = _testMaxFrames;   // loop exits at budget+1

                if (status == "timeout")
                {
                    var rr = WireCore.CheckUnitTest();
                    if (resultText.Length == 0)
                        resultText = rr.Found ? $"budget exhausted, $6000=0x{rr.Code:X2}" : "budget exhausted, no $6000 signature";
                    resultCode = 3;
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
