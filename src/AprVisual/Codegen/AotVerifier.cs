using System;
using AprVisual.Sim;
using AprVisual.Rom;

namespace AprVisual.Codegen
{
    /// <summary>
    /// AOT verification harness: run S1 on a ROM step-by-step; at each half-cycle, after S1
    /// settles, call a hand-coded AOT eval on the same NodeStates snapshot, and compare the
    /// predicted output to S1's actual NodeStates value. Report mismatch rate.
    ///
    /// First MVP: verifies AotBlocks.EvalTileHBitMux_* against S1's ppu.+tile_h_bit_out.
    /// Both combinational (always-recompute) and phi-gated (latch-hold-when-pclk-low) variants
    /// are tested — the right model wins.
    /// </summary>
    public static unsafe class AotVerifier
    {
        /// <summary>Auto-emit AOT code for ir0..7 + notir0..7 via AotEmitter, then verify the
        /// emitter's output matches S1 for hcCount half-cycles. This is the Phase B milestone:
        /// "emitter-generated code = hand-coded code = S1 truth".</summary>
        public static int VerifyEmitterOnIrInverter(string romPath, int hcCount)
        {
            if (hcCount < 1) hcCount = 100_000;
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            Console.WriteLine($"# aot-emit-verify-ir: {System.IO.Path.GetFileName(romPath)} — running {hcCount:N0} half-cycles");
            try
            {
                WireCore.LoadSystem(rom);
                var ids = AotBlocks.ResolveIrInverter();

                // Auto-emit for each of the 8 notir nodes
                Console.WriteLine($"# === auto-emitted AOT for notir[0..7] (from AotEmitter.EmitForNode) ===");
                var emitted = new AotEmitter.EmitResult[8];
                int emittedOk = 0;
                for (int i = 0; i < 8; i++)
                {
                    var er = AotEmitter.EmitForNode(ids.NotIr[i]);
                    emitted[i] = er;
                    Console.WriteLine($"#   notir{i} (id {ids.NotIr[i]}): pattern='{er.Pattern}', inputs=[{string.Join(',', er.InputIds)}], expr = {er.CSharpExpr ?? "(none)"}");
                    if (er.Compiled != null) emittedOk++;
                }
                if (emittedOk != 8)
                {
                    Console.WriteLine($"# emitter only handled {emittedOk}/8 nodes — cannot verify (need 8/8)");
                    return 3;
                }
                // Sanity: each emitter's discovered input ID should equal the corresponding ir[i]
                bool inputMatchesHand = true;
                for (int i = 0; i < 8; i++)
                {
                    if (emitted[i].InputIds.Length != 1 || emitted[i].InputIds[0] != ids.Ir[i])
                    {
                        Console.WriteLine($"#   ! emitter discovered input for notir{i} = {string.Join(',', emitted[i].InputIds)} ; hand-coded says ir{i} = {ids.Ir[i]}");
                        inputMatchesHand = false;
                    }
                }
                Console.WriteLine($"# emitter's discovered inputs MATCH the hand-coded gate IDs: {inputMatchesHand}");

                // Run S1 + emitter side-by-side
                long sampled = 0, mismatchesEmitter = 0, mismatchesHand = 0;
                for (int hc = 0; hc < hcCount; hc++)
                {
                    WireCore.Step(1);
                    byte* ns = WireCore.NodeStates;
                    IntPtr nsPtr = (IntPtr)ns;
                    byte emitterByte = 0;
                    for (int i = 0; i < 8; i++) emitterByte |= (byte)(emitted[i].Compiled!(nsPtr) << i);
                    byte handByte    = AotBlocks.EvalIrInverter(ns, ids);
                    byte actualByte  = AotBlocks.ReadIrInverterActual(ns, ids);
                    sampled++;
                    if (emitterByte != actualByte) mismatchesEmitter++;
                    if (handByte    != actualByte) mismatchesHand++;
                }

                Console.WriteLine($"# samples: {sampled:N0}");
                Console.WriteLine($"# hand-coded eval mismatches : {mismatchesHand:N0} / {sampled:N0}");
                Console.WriteLine($"# auto-emitted eval mismatches: {mismatchesEmitter:N0} / {sampled:N0}");
                if (mismatchesEmitter == 0 && mismatchesHand == 0)
                    Console.WriteLine($"# VERDICT: AUTO-EMITTED AOT IS EQUIVALENT TO HAND-CODED AND TO S1 (0 diff). Phase B milestone achieved.");
                else
                    Console.WriteLine($"# VERDICT: divergence — see counts above");
                return (mismatchesEmitter == 0 && mismatchesHand == 0) ? 0 : 1;
            }
            finally { WireCore.Shutdown(); }
        }

        public static int VerifyIrInverter(string romPath, int hcCount)
        {
            if (hcCount < 1) hcCount = 100_000;
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            Console.WriteLine($"# aot-verify-ir-inverter: {System.IO.Path.GetFileName(romPath)} — running {hcCount:N0} half-cycles");
            try
            {
                WireCore.LoadSystem(rom);
                var ids = AotBlocks.ResolveIrInverter();
                Console.WriteLine($"# resolved: ir = [{string.Join(',', ids.Ir)}], notir = [{string.Join(',', ids.NotIr)}]");

                long sampled = 0, mismatches = 0;
                long irChangeHc = 0;          // half-cycles where any ir bit changed
                byte prevIr = 0xFF;
                int firstMismatchHc = -1;
                byte firstMismatchPredicted = 0, firstMismatchActual = 0, firstMismatchIr = 0;

                for (int hc = 0; hc < hcCount; hc++)
                {
                    WireCore.Step(1);
                    byte* ns = WireCore.NodeStates;
                    byte predicted = AotBlocks.EvalIrInverter(ns, ids);
                    byte actual    = AotBlocks.ReadIrInverterActual(ns, ids);
                    byte ir = 0; for (int i = 0; i < 8; i++) ir |= (byte)(ns[ids.Ir[i]] << i);
                    sampled++;
                    if (ir != prevIr) { irChangeHc++; prevIr = ir; }
                    if (predicted != actual)
                    {
                        mismatches++;
                        if (firstMismatchHc < 0) { firstMismatchHc = hc; firstMismatchPredicted = predicted; firstMismatchActual = actual; firstMismatchIr = ir; }
                    }
                }

                Console.WriteLine($"# samples: {sampled:N0}");
                Console.WriteLine($"# ir-changing half-cycles: {irChangeHc:N0}  ({(double)irChangeHc / sampled:P2})  — proves ir IS being exercised");
                Console.WriteLine($"# mismatches: {mismatches:N0} / {sampled:N0}  ({(double)mismatches / sampled:P2})");
                if (mismatches > 0)
                {
                    Console.WriteLine($"# first mismatch @ hc={firstMismatchHc}: ir=0x{firstMismatchIr:X2}, predicted_notir=0x{firstMismatchPredicted:X2}, actual_notir=0x{firstMismatchActual:X2}");
                    Console.WriteLine($"# VERDICT: AOT inverter eval does NOT match S1 perfectly — likely phi-latched");
                }
                else
                    Console.WriteLine($"# VERDICT: AOT inverter eval IS the right model (zero diff vs S1 over {sampled:N0} hc)");
                return mismatches == 0 ? 0 : 1;
            }
            finally { WireCore.Shutdown(); }
        }

        public static int VerifyTileHBitMux(string romPath, int hcCount)
        {
            if (hcCount < 1) hcCount = 100_000;
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            Console.WriteLine($"# aot-verify-tilemux: {System.IO.Path.GetFileName(romPath)} — running {hcCount:N0} half-cycles");
            try
            {
                WireCore.LoadSystem(rom);
                var ids = AotBlocks.ResolveTileHBitMux();
                Console.WriteLine($"# resolved IDs: finex={ids.FineX0}/{ids.FineX1}/{ids.FineX2}, pclk1_3={ids.Pclk1_3}, tile_h0={ids.TileH0}..{ids.TileH0 + 7}, output={ids.Output}");

                // Counters
                long sampled = 0;
                long mismatchComb = 0;     // combinational variant
                long mismatchPhi  = 0;     // phi-gated variant
                long pclkHighSamples = 0;
                long pclkLowSamples = 0;
                long actualOutputHigh = 0;
                long actualOutputLow = 0;
                // Per-input diagnostics
                var fxToggles = new long[3];          // count of times each fine_x bit changed
                var tileHLow = new long[8];           // count of times each tile_h bit was 0
                byte prevFx0 = 99, prevFx1 = 99, prevFx2 = 99;

                // Sample first few divergences for debugging
                const int maxReports = 5;
                int reportsComb = 0, reportsPhi = 0;

                for (int hc = 0; hc < hcCount; hc++)
                {
                    WireCore.Step(1);   // one master half-cycle
                    byte* ns = WireCore.NodeStates;   // already a pointer, no fixed needed
                    byte predictedComb = AotBlocks.EvalTileHBitMux_Combinational(ns, in ids);
                    byte predictedPhi  = AotBlocks.EvalTileHBitMux_PhiGated(ns, in ids);
                    byte actual = ns[ids.Output];
                    byte pclk = ns[ids.Pclk1_3];

                    sampled++;
                    if (pclk != 0) pclkHighSamples++; else pclkLowSamples++;
                    if (actual != 0) actualOutputHigh++; else actualOutputLow++;
                    // Track input movement
                    if (ns[ids.FineX0] != prevFx0) { if (prevFx0 != 99) fxToggles[0]++; prevFx0 = ns[ids.FineX0]; }
                    if (ns[ids.FineX1] != prevFx1) { if (prevFx1 != 99) fxToggles[1]++; prevFx1 = ns[ids.FineX1]; }
                    if (ns[ids.FineX2] != prevFx2) { if (prevFx2 != 99) fxToggles[2]++; prevFx2 = ns[ids.FineX2]; }
                    for (int b = 0; b < 8; b++) if (ns[ids.TileH0 + b] == 0) tileHLow[b]++;

                    if (predictedComb != actual)
                    {
                        mismatchComb++;
                        if (reportsComb < maxReports)
                        {
                            int idx = (ns[ids.FineX2] << 2) | (ns[ids.FineX1] << 1) | ns[ids.FineX0];
                            Console.WriteLine($"#   COMB miss @ hc={hc}: predicted={predictedComb}, actual={actual}, finex={ns[ids.FineX2]}{ns[ids.FineX1]}{ns[ids.FineX0]} ({idx}), tile_h[{idx}]={ns[ids.TileH0 + idx]}, pclk1_3={pclk}");
                            reportsComb++;
                        }
                    }
                    if (predictedPhi != actual)
                    {
                        mismatchPhi++;
                        if (reportsPhi < maxReports)
                        {
                            int idx = (ns[ids.FineX2] << 2) | (ns[ids.FineX1] << 1) | ns[ids.FineX0];
                            Console.WriteLine($"#   PHI  miss @ hc={hc}: predicted={predictedPhi},  actual={actual}, finex=({idx}), tile_h[{idx}]={ns[ids.TileH0 + idx]}, pclk1_3={pclk}");
                            reportsPhi++;
                        }
                    }
                }

                Console.WriteLine($"# samples: {sampled:N0} (pclk_high={pclkHighSamples:N0}, pclk_low={pclkLowSamples:N0})");
                Console.WriteLine($"# actual output bit: high={actualOutputHigh:N0}, low={actualOutputLow:N0}  (fraction high = {(double)actualOutputHigh / sampled:P2})");
                Console.WriteLine($"# fine_x toggles  : fx0={fxToggles[0]:N0}  fx1={fxToggles[1]:N0}  fx2={fxToggles[2]:N0}");
                Console.WriteLine($"# tile_h low count: t0={tileHLow[0]:N0}  t1={tileHLow[1]:N0}  t2={tileHLow[2]:N0}  t3={tileHLow[3]:N0}  t4={tileHLow[4]:N0}  t5={tileHLow[5]:N0}  t6={tileHLow[6]:N0}  t7={tileHLow[7]:N0}");
                Console.WriteLine($"# combinational variant : {mismatchComb:N0} / {sampled:N0} mismatches  ({(double)mismatchComb / sampled:P2})");
                Console.WriteLine($"# phi-gated variant     : {mismatchPhi:N0} / {sampled:N0} mismatches  ({(double)mismatchPhi / sampled:P2})");
                if (mismatchComb == 0) Console.WriteLine($"# VERDICT: combinational variant IS the right model (zero diff vs S1)");
                else if (mismatchPhi == 0) Console.WriteLine($"# VERDICT: phi-gated variant IS the right model (zero diff vs S1)");
                else Console.WriteLine($"# VERDICT: NEITHER variant matches S1; need to model more carefully");

                return mismatchComb == 0 || mismatchPhi == 0 ? 0 : 1;
            }
            finally { WireCore.Shutdown(); }
        }
    }
}
