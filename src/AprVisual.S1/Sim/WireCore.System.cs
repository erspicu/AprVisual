using System;
using System.Collections.Generic;
using AprVisual.Rom;

namespace AprVisual.Sim
{
    internal static unsafe partial class WireCore
    {
        // ── Top-level system assembly + reset + frame stepping — port of ref/metalnes-main:
        //      system.cpp (system_state::Create / setupRom) + handler_nes_system.h (reset / onFrameEnd).
        //    See MD/note/03_系統整合與週期推進.md §3.
        //
        //    Decisions (S1): only NROM (mapper 0); real reset (assert /res, run 192 half-cycles, deassert) —
        //    NOT the "pretend the bus reads NOP" shortcut (MD/struct/01 §11.3).

        public static string SystemDefDir = "data/system-def";   // where the .js module files live

        // ── cached nodes / registers / memory, resolved by ResolveCachedNodes() after the netlist is built ──
        public static int N_Res = EmptyNode;          // the board "res" line (→ CIC → cpu.res / ppu.res)
        public static int N_PpuInVblank = EmptyNode;  // rising edge = frame boundary
        public static int N_CpuSync = EmptyNode;      // high during opcode-fetch cycle
        public static int[] R_CpuA = [], R_CpuX = [], R_CpuY = [], R_CpuP = [], R_CpuS = [], R_CpuIr = [];
        public static int[] R_CpuPcl = [], R_CpuPch = [], R_CpuAb = [], R_CpuDb = [];
        public static Memory? M_EramRam;              // cart.eram.ram — the $6000 work RAM used by blargg test ROMs

        private static NesRom? _rom;

        // ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Build the global netlist for an NES board: load nes-001 + cart-mmu0 (+ the runtime PRG / CHR /
        /// extra-RAM cartridge sub-modules) and instantiate them. Port of the netlist-composition part of
        /// system_state::Create. Does NOT allocate hot arrays, attach handlers, or copy ROM bytes.
        /// </summary>
        public static void ComposeSystem(bool chrIsRam, bool isTestRom)
        {
            ResetBuild();   // clears all build-time state, re-registers vcc/vss

            if (EnableJoypadHandler) PreloadModuleAs(SystemDefDir, "nes-pad-behavioral", "nes-pad");
            var nes001 = LoadModuleDef(SystemDefDir, "nes-001");
            var cartMmu0 = LoadModuleDef(SystemDefDir, "cart-mmu0");

            // Append the runtime cartridge sub-modules to cart-mmu0 (prefix "" → instantiated under "cart").
            LoadModuleDef(SystemDefDir, "cart-mmu0-prgrom");
            cartMmu0.SubModules.Add(new SubModuleRef { Prefix = "", Type = "cart-mmu0-prgrom" });

            string chrType = chrIsRam ? "cart-mmu0-chrram" : "cart-mmu0-chrrom";
            LoadModuleDef(SystemDefDir, chrType);
            cartMmu0.SubModules.Add(new SubModuleRef { Prefix = "", Type = chrType });

            if (isTestRom)
            {
                LoadModuleDef(SystemDefDir, "cart-extraram");
                cartMmu0.SubModules.Add(new SubModuleRef { Prefix = "", Type = "cart-extraram" });
            }

            AddInstance(nes001, "");
            AddInstance(cartMmu0, "cart");

            // S1.5: collapse always-on shorts + drop dead transistors + compact ids (behaviour-preserving).
            // Kept as a real S1 win: +~3.7% (interleaved-paired vs --no-lower) and it defines the golden node
            // numbering (checksum 0x794A43ABDF169ADA / the .aprsnap snapshots). --no-lower is a diagnostic A/B
            // toggle only (measures −3.7%). (Originally also framed as S2's canonical netlist; S2 is now a
            // separate concluded fork — lowering stays here purely on its S1 perf merit.)
            if (EnableLowering) LowerNetlist();
            else LastLowerStats = "(lowering disabled — --no-lower)";
        }

        // ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Build + power on the full NES system for <paramref name="rom"/>: compose the netlist, attach the
        /// behavioral handlers (clock / RAM / ROM / video), copy the ROM bytes into the memory regions,
        /// resolve the cached probe nodes, then do a power-on reset. Port of system_state::Create.
        /// </summary>
        // When true, always compose cart-extraram ($6000 work RAM) regardless of the ROM path
        // heuristic below. Lets a benchmark deterministically match the Rust snapshot (which was
        // exported with extraram present) even when the ROM isn't under a "nes-test-roms" path.
        // Set by the --extra-ram CLI flag.
        public static bool ForceExtraRam = false;

        // Capture-pass tuning (see LoadSystem pass 1 / WarmupCaptureFirstTouch): warm-up past the
        // reset transient, then record the true first-touch order over the capture span. The capture
        // runs on the FULL-SPEED pruned engine, so 32K hc ≈ 0.3 s of load time.
        private const int CaptureWarmupHc = 1024;
        private const int FirstTouchCaptureHc = 32768;

        public static void LoadSystem(NesRom rom)
        {
            _rom = rom;
            bool chrIsRam = rom.ChrRom.Length == 0;
            bool isTestRom = ForceExtraRam
                          || rom.Path.Contains("nes-test-roms", StringComparison.OrdinalIgnoreCase)
                          || rom.Path.Contains("nes_test", StringComparison.OrdinalIgnoreCase);

            // Cartridge scope: NROM (0) natively; CNROM (3) via the behavioral CHR bank latch
            // (WireCore.Handlers.cs SetupCnrom/AttachCnromLatch — the ROM handlers are behavioral
            // already, so a behavioral mapper is the same abstraction level; the 2A03/2C02 netlists
            // are untouched). Anything else would silently misbehave — fail loudly instead.
            bool isCnrom = rom.Mapper == 3;
            if (rom.Mapper != 0 && rom.Mapper != 3)
                throw new NotSupportedException($"mapper {rom.Mapper} not supported (S1 scope: NROM + CNROM)");

            // [auto-renumber] two-phase load: pass 1 composes + attaches + Reset()s with IDENTITY ids —
            // the classifiers (end of Reset, before ANY settle) produce the final PruneMask — captures
            // each node's prune class, then loops back: pass 2 re-composes (ResetBuild clears all of
            // pass 1) and ApplyRenumber sorts ids class-major, making the prune facts contiguous id
            // RANGES (the range-prune compare path), with the SELF-CAPTURED first-touch locality key.
            // Bit-exact: power-on order + checksum go through the permutation in original order.
            for (int pass = 0; ; pass++)
            {
                ComposeSystem(chrIsRam, isTestRom);   // WireCore.System.cs (ResetBuild + load defs + AddInstance)

                ApplyRenumber();   // class-major permutation (no-op without captured bits; post-lowering, pre-handlers)

                if (isCnrom) SetupCnrom(rom.ChrRom.Length);   // enlarge CHR Memory + alloc the bank register (before CopyRomBytes)
                CopyRomBytes(rom);

                // Handlers add fake nodes/transistors via AddCallback — MUST run before Reset() (which sizes the hot arrays).
                AttachClockHandler();     // WireCore.Handlers.cs — toggles "clk" each half-cycle
                AttachMemoryHandlers();   // RAM (u1, u4) + ROM (cart.prg, cart.chr) handlers
                if (isCnrom) AttachCnromLatch(Math.Max(1, rom.ChrRom.Length / 8192));   // CNROM: CPU write to $8000+ latches the CHR bank
                if (EnableJoypadHandler) AttachJoypadHandler();   // behavioral controller (test mode)
                AttachVideoHandler();     // pclk1 rising-edge pixel write to FrameBuffer

                ResolveCachedNodes();   // per-pass: the capture pass's ResetNes needs N_Res (idempotent name lookups)

                if (pass == 0)
                {
                    // PASS 0 — classify only (no settle): capture each node's prune class under
                    // identity ids. Feeds pass 1's class-major build (temporary blind-BFS locality).
                    Reset();
                    CapturePruneClasses();   // → PendingClassBits (+ StashedClassBits for pass 2)
                    continue;
                }
                if (pass == 1)
                {
                    // PASS 1 — capture: this build has VERIFIED ranges, so the prunes are ON and the
                    // settle is the PRODUCTION cascade. Warm past the reset transient, then record the
                    // TRUE first-touch order through the cold instrumented settle copy (the hot
                    // ProcessQueue is untouched). Translated back to identity ids; everything else is
                    // torn down by the final rebuild. (An unpruned warm-up was measured −1% vs this —
                    // the locality key's value is the pruned cascade's order, not line density alone.)
                    ResetNes(full: true);
                    Step(CaptureWarmupHc);                     // past the post-reset transient
                    WarmupCaptureFirstTouch(FirstTouchCaptureHc); // → PendingLocalityOrder (identity ids)
                    PendingClassBits = StashedClassBits;          // re-arm the class bits for the final pass
                    StashedClassBits = null;
                    continue;
                }
                break;
            }

            ResetNes(full: true);     // clear RAMs + Reset() + alloc FrameBuffer + assert/run/deassert res

            // The hot path reads only the unmanaged arrays from this point. Drop the build-time
            // managed data (Node.Gates / Node.C1c2s lists, _transistors list, _transistorSet,
            // _forceComputeList, LoadedDefs JSON parse) — typically ~25-50 MB freed.
            ClearPostLoadBuildState();
        }

        private static void CopyRomBytes(NesRom rom)
        {
            // prg/chr.Data are unmanaged (byte*) — copy via Span instead of Array.Copy.
            var prg = ResolveMemory("cart.prg.rom");
            if (prg != null && rom.PrgRom.Length > 0)
            {
                var prgDst = new Span<byte>(prg.Data, prg.Length);
                if (rom.PrgRom.Length == 16 * 1024 && prg.Length >= 32 * 1024)
                {
                    rom.PrgRom.AsSpan(0, 16 * 1024).CopyTo(prgDst);
                    rom.PrgRom.AsSpan(0, 16 * 1024).CopyTo(prgDst.Slice(16 * 1024));   // NROM-128: mirror the 16 KB bank
                }
                else
                {
                    int n = Math.Min(rom.PrgRom.Length, prg.Length);
                    rom.PrgRom.AsSpan(0, n).CopyTo(prgDst);
                }
            }
            var chr = ResolveMemory("cart.chr.rom");
            if (chr != null && rom.ChrRom.Length > 0)
            {
                int n = Math.Min(rom.ChrRom.Length, chr.Length);
                rom.ChrRom.AsSpan(0, n).CopyTo(new Span<byte>(chr.Data, chr.Length));
            }
        }

        private static void ResolveCachedNodes()
        {
            ClockNode      = LookupNode("clk");          // WireCore.Recalc.cs (reference only — toggled by the clock handler)
            N_Res          = LookupNode("res");
            N_PpuInVblank  = LookupNode("ppu.in_vblank");
            N_CpuSync      = LookupNode("cpu.sync");
            R_CpuA   = ResolveQuiet("cpu.a[7:0]");
            R_CpuX   = ResolveQuiet("cpu.x[7:0]");
            R_CpuY   = ResolveQuiet("cpu.y[7:0]");
            R_CpuP   = ResolveQuiet("cpu.p[7:0]");
            R_CpuS   = ResolveQuiet("cpu.s[7:0]");
            R_CpuIr  = ResolveQuiet("cpu.ir[7:0]");
            R_CpuPcl = ResolveQuiet("cpu.pcl[7:0]");
            R_CpuPch = ResolveQuiet("cpu.pch[7:0]");
            R_CpuAb  = ResolveQuiet("cpu.ab[15:0]");
            R_CpuDb  = ResolveQuiet("cpu.db[7:0]");
            M_EramRam = ResolveMemory("cart.eram.ram");
        }

        private static int[] ResolveQuiet(string expr)
        {
            var l = new List<int>();
            ResolveNodes(expr, l, quiet: true);
            return l.ToArray();
        }

        // ── power-up state shim (test mode only; see MD/testrom/2026-07-03-fail-analysis §A/§3.2) ──
        // The netlist's artificial power-on (discharge → pull-ups → settle) is not any real console's
        // power-on. Test mode injects the CONVENTIONAL power-up state after the raw settle, using the
        // drive → settle → RELEASE (SetFloat) pattern so every touched cell float-holds the injected
        // value and remains fully writable afterwards:
        //   1. the 2C02 palette cells get the blargg-console table from power_up_palette's own source
        //      (which became the emulator-consensus power-up palette), and
        //   2. the 2A03 Z-flag latch (cpu.p1) is cleared — the netlist settles to P=$36, real consoles
        //      (and cpu_reset/registers' expectation) power on with P=$34; only the Z bit differs.
        // Benchmarks never set this flag — the golden-checksum path is byte-for-byte untouched.
        public static bool PowerUpStateShim = false;

        // ── clock-phase experiment (--reset-hold-extra K): extra half-cycles of /res assertion. ──
        // Probes whether the CPU ÷12 / PPU ÷4 divider alignment depends on the reset-release moment
        // (if the dividers free-run from power-on, a global extra step shifts both equally and the
        // RELATIVE phase is unchanged — the experiment's first question). See MD/testrom fail analysis §B.
        public static int ResetHoldExtraHc = 0;
        private static readonly byte[] PowerUpPalette = {
            0x09, 0x01, 0x00, 0x01, 0x00, 0x02, 0x02, 0x0D, 0x08, 0x10, 0x08, 0x24, 0x00, 0x00, 0x04, 0x2C,
            0x09, 0x01, 0x34, 0x03, 0x00, 0x04, 0x00, 0x14, 0x08, 0x3A, 0x00, 0x02, 0x00, 0x20, 0x2C, 0x08,
        };
        private static void ApplyPowerUpState()
        {
            // Each palette bit is a cross-coupled latch CELL with two named nodes: pal_ram_XX_bN (the
            // value side — what the video handler reads) and pal_ram_XX_aN (the complement side).
            // Driving only one side doesn't stick: on release the undriven side snaps the latch back.
            // Drive BOTH sides complementarily, settle, then release both — the cell then holds.
            var bitsB = new List<int>();
            var bitsA = new List<int>();
            var driven = new List<int>(32 * 6 * 2 + 1);
            for (int slot = 0; slot < 32; slot++)
            {
                bitsB.Clear(); bitsA.Clear();
                ResolveNodes($"ppu.pal_ram_{slot:X2}_b[5:0]", bitsB, quiet: true);
                ResolveNodes($"ppu.pal_ram_{slot:X2}_a[5:0]", bitsA, quiet: true);
                if (bitsB.Count != 6 || bitsA.Count != 6)
                { Console.Error.WriteLine($"power-up state shim: palette slot {slot:X2} unresolved — skipped"); continue; }
                int v = PowerUpPalette[slot];
                for (int b = 0; b < 6; b++)
                {
                    bool one = ((v >> b) & 1) != 0;
                    if (one) SetHighQueued(bitsB[b]); else SetLowQueued(bitsB[b]);
                    if (one) SetLowQueued(bitsA[b]);  else SetHighQueued(bitsA[b]);
                    driven.Add(bitsB[b]);
                    driven.Add(bitsA[b]);
                }
            }

            ProcessQueue();                                    // latch cells settle to the driven values
            foreach (int n in driven) SetFloatQueued(n);       // release: cells float-hold, stay writable
            ProcessQueue();
        }

        // 2A03 Z flag (cpu.p1 storage node): the netlist's artificial power-on settles Z=1 (P=$36);
        // real consoles power on with Z=0 (P=$34). Injected AFTER the /res pulse (the flag logic is
        // active during the held-reset cycles, which would regenerate the settle value). Prints a
        // read-back so a failed injection is visible in the test log.
        private static void ApplyPowerUpZFlag()
        {
            int zNode = LookupNode("cpu.p1");
            if (zNode == EmptyNode)
            { Console.Error.WriteLine("power-up state shim: cpu.p1 (Z flag) unresolved — skipped"); return; }
            SetLow(zNode);
            SetFloat(zNode);
            Console.Error.WriteLine($"# [shim] Z flag post-reset inject: cpu.p1={NodeStates[zNode]} P=${ReadReg(R_CpuP):X2}");
        }

        // ── DMC pcm_latch edge-capture shim (test mode only) ────────────────────────────────
        // The DPCM control's pcm_latch pass gate (t14402: gate=apu_clk1, 13907↔13947) has a
        // same-half-cycle race at the clock's falling edge: the latch input (pcm_ff, set by the
        // DMA-fetch PCM strobe during phi2) falls in the SAME half-cycle apu_clk1 closes. Real
        // NMOS silicon resolves the race "data wins" via analog clock-decay overlap (blargg
        // 7-dmc_basics #19 reads $80 on hardware); any quiescent-settle binary model resolves
        // "gate closed first" and the DMC IRQ flag lands one full ACLK late (emu-russia's
        // APUSim quantizes it away identically — verified 2026-07-04, see
        // MD/testrom/2026-07-04-dmc19-aclk-pipeline-analysis.md). This shim implements the
        // latch's intended edge semantic explicitly: at apu_clk1's falling edge, capture the
        // post-settle value of the latch input. Inert everywhere else: after any transparent
        // phase the two sides are already equal, so the copy fires only in the race case.
        public static bool DmcLatchShim = false;
        private static int _dmcShimClk1 = EmptyNode, _dmcShimFf = EmptyNode, _dmcShimLatch = EmptyNode;
        private static int _dmcShimPrevClk1;

        /// <summary>Resolve the shim's nodes and arm it. Call after LoadSystem, with
        /// RegisterRawIdAliases enabled before it (the latch nodes are unnamed raw ids).</summary>
        public static void EnableDmcLatchShim()
        {
            _dmcShimClk1  = LookupNode("cpu.apu_clk1");
            _dmcShimFf    = LookupNode("cpu.#13907");
            _dmcShimLatch = LookupNode("cpu.#13947");
            if (_dmcShimClk1 == EmptyNode || _dmcShimFf == EmptyNode || _dmcShimLatch == EmptyNode)
            { Console.Error.WriteLine("# [shim] DMC latch shim: nodes unresolved — disabled"); DmcLatchShim = false; return; }
            _dmcShimPrevClk1 = NodeStates[_dmcShimClk1];
            DmcLatchShim = true;
        }

        internal static void DmcLatchShimStep()
        {
            int cur = NodeStates[_dmcShimClk1];
            if (_dmcShimPrevClk1 == 1 && cur == 0)
            {
                int v = NodeStates[_dmcShimFf];
                if (NodeStates[_dmcShimLatch] != v)
                {
                    if (v == 1) SetHigh(_dmcShimLatch); else SetLow(_dmcShimLatch);
                    SetFloat(_dmcShimLatch);
                }
            }
            _dmcShimPrevClk1 = cur;
            if (AluLatchShim) AluLatchShimStep();
        }

        // ── ALU input-latch hold shim (test mode only) ───────────────────────────────────────
        // The complement of the DMC race, with the OPPOSITE physical polarity: on unofficial
        // "combined" immediate ops (ANC/ALR/ARR/LXA) the execute-phase phi1->phi2 boundary
        // collapses the SB/DB buses in the same half-cycle the ALU input-latch select lines
        // (SBADD/DBADD) close. Real silicon closes the gate BEFORE the collapse propagates
        // (hold time met), so alua/alub keep their phi1 values; a quiescent settle lets the
        // collapse ripple THROUGH the closing gates and the ALU latches a self-consistent
        // garbage fixed point. Shim: snapshot alua/alub each half-cycle; when the select line
        // falls, restore any bit the same step corrupted (= the latch's intended hold semantic).
        public static bool AluLatchShim = false;
        private static int _aluShimSbadd = EmptyNode, _aluShimDbadd = EmptyNode;
        private static int[] _aluShimA = Array.Empty<int>(), _aluShimB = Array.Empty<int>();
        private static readonly byte[] _aluShimPrevA = new byte[8], _aluShimPrevB = new byte[8];
        private static int _aluShimPrevSbadd, _aluShimPrevDbadd;

        public static void EnableAluLatchShim()
        {
            _aluShimSbadd = LookupNode("cpu.dpc11_SBADD");
            _aluShimDbadd = LookupNode("cpu.dpc9_DBADD");
            var la = new List<int>(); ResolveNodes("cpu.alua[7:0]", la, quiet: true);
            var lb = new List<int>(); ResolveNodes("cpu.alub[7:0]", lb, quiet: true);
            if (_aluShimSbadd == EmptyNode || _aluShimDbadd == EmptyNode || la.Count != 8 || lb.Count != 8)
            { Console.Error.WriteLine("# [shim] ALU latch shim: nodes unresolved — disabled"); AluLatchShim = false; return; }
            _aluShimA = la.ToArray(); _aluShimB = lb.ToArray();
            for (int i = 0; i < 8; i++) { _aluShimPrevA[i] = NodeStates[_aluShimA[i]]; _aluShimPrevB[i] = NodeStates[_aluShimB[i]]; }
            _aluShimPrevSbadd = NodeStates[_aluShimSbadd];
            _aluShimPrevDbadd = NodeStates[_aluShimDbadd];
            AluLatchShim = true;
        }

        private static void AluLatchShimStep()
        {
            int sa = NodeStates[_aluShimSbadd], db = NodeStates[_aluShimDbadd];
            if (_aluShimPrevSbadd == 1 && sa == 0)
                for (int i = 0; i < 8; i++)
                {
                    int n = _aluShimA[i];
                    if (NodeStates[n] != _aluShimPrevA[i])
                    { if (_aluShimPrevA[i] == 1) SetHigh(n); else SetLow(n); SetFloat(n); }
                }
            if (_aluShimPrevDbadd == 1 && db == 0)
                for (int i = 0; i < 8; i++)
                {
                    int n = _aluShimB[i];
                    if (NodeStates[n] != _aluShimPrevB[i])
                    { if (_aluShimPrevB[i] == 1) SetHigh(n); else SetLow(n); SetFloat(n); }
                }
            _aluShimPrevSbadd = sa; _aluShimPrevDbadd = db;
            for (int i = 0; i < 8; i++) { _aluShimPrevA[i] = NodeStates[_aluShimA[i]]; _aluShimPrevB[i] = NodeStates[_aluShimB[i]]; }
            if (LxaMagicShim) LxaMagicShimStep();
        }

        // ── LXA ($AB) magic-constant shim (test mode only) ───────────────────────────────────
        // LXA/ATX: A = X = (A | MAGIC) & imm. The MAGIC constant is a ratioed analog bus fight
        // (AC pulls the merged SB/IDB line against the data-latch driver during a short window);
        // real chips yield $EE/$FF and it is documented as chip- and temperature-dependent
        // (NESdev unofficial opcodes; TriCNES source: "can supposedly be different depending on
        // the CPU's temperature"). Our binary GND-wins resolution quantizes the fight to
        // MAGIC=$00. Both blargg's console (instr_test checksums) and AccuracyCoin's author's
        // console behave as MAGIC=$FF, the NTSC G-revision consensus — this shim applies that:
        // when opcode $AB completes, force A = X = imm and N/Z accordingly. Behavioral patch at
        // the same honesty level as the CNROM mapper: the abstraction cannot express the analog
        // strength contest, so the behavioral layer supplies the reference machine's outcome.
        public static bool LxaMagicShim = false;
        private static int _lxaPhi2 = EmptyNode, _lxaSync = EmptyNode;
        private static int _lxaP1 = EmptyNode, _lxaP7 = EmptyNode, _lxaZLoop = EmptyNode, _lxaNotN = EmptyNode;
        private static int[] _lxaDb = Array.Empty<int>(), _lxaA = Array.Empty<int>(), _lxaX = Array.Empty<int>();
        private static int _lxaPrevPhi2, _lxaArm, _lxaImm;
        private static bool _lxaPrevSync;

        public static void EnableLxaMagicShim()
        {
            _lxaPhi2 = LookupNode("cpu.phi2");
            _lxaSync = LookupNode("cpu.sync");
            _lxaP1 = LookupNode("cpu.p1");
            _lxaP7 = LookupNode("cpu.p7");
            _lxaZLoop = LookupNode("cpu.#566");
            _lxaNotN  = LookupNode("cpu.#1045");
            var db = new List<int>(); ResolveNodes("cpu.db[7:0]", db, quiet: true);
            var a  = new List<int>(); ResolveNodes("cpu.a[7:0]",  a,  quiet: true);
            var x  = new List<int>(); ResolveNodes("cpu.x[7:0]",  x,  quiet: true);
            if (_lxaPhi2 == EmptyNode || _lxaSync == EmptyNode || _lxaP1 == EmptyNode || _lxaP7 == EmptyNode
                || db.Count != 8 || a.Count != 8 || x.Count != 8)
            { Console.Error.WriteLine("# [shim] LXA magic shim: nodes unresolved — disabled"); LxaMagicShim = false; return; }
            _lxaDb = db.ToArray(); _lxaA = a.ToArray(); _lxaX = x.ToArray();
            _lxaPrevPhi2 = NodeStates[_lxaPhi2];
            _lxaArm = 0;
            LxaMagicShim = true;
        }

        private static void LxaMagicShimStep()
        {
            int ph = NodeStates[_lxaPhi2];
            if (_lxaPrevPhi2 == 1 && ph == 0)   // phi2 falling = end of a CPU cycle
            {
                int dbv = ReadBits(_lxaDb);
                // In this netlist's phi2-fall sampling, cpu.sync leads by one cycle: sync==1 at
                // the fall of cycle N flags cycle N+1 as the opcode fetch. Use the PREVIOUS
                // fall's sync to classify the current cycle.
                bool fetchNow = _lxaPrevSync;
                _lxaPrevSync = NodeStates[_lxaSync] == 1;
                void Force(int node, int bit) { if (node == EmptyNode) return; if (bit == 1) SetHigh(node); else SetLow(node); SetFloat(node); }
                if (_lxaArm >= 3 && _lxaArm < 6) _lxaArm++;
                if (_lxaArm == 6)
                {
                    // Release the sustained flag drive (held across PHP-style immediate readers)
                    SetFloat(_lxaP7); SetFloat(_lxaNotN); SetFloat(_lxaP1); SetFloat(_lxaZLoop);
                    _lxaArm = 0;
                }
                else if (_lxaArm == 2 && fetchNow)
                {
                    int imm = _lxaImm;
                    for (int i = 0; i < 8; i++) { int b = (imm >> i) & 1; Force(_lxaA[i], b); Force(_lxaX[i], b); }
                    // Flags: both latches are actively-refreshed loops that revert a one-shot
                    // force; hold a sustained drive on the pair for 3 cycles instead, then float.
                    int n = (imm >> 7) & 1, z = imm == 0 ? 1 : 0;
                    void Drive(int node, int bit) { if (node == EmptyNode) return; if (bit == 1) SetHigh(node); else SetLow(node); }
                    Drive(_lxaP7, n); Drive(_lxaNotN, n ^ 1);
                    Drive(_lxaP1, z); Drive(_lxaZLoop, z);
                    _lxaArm = 3;
                }
                if (fetchNow && dbv == 0xAB) _lxaArm = 1;
                else if (_lxaArm == 1) { _lxaImm = dbv; _lxaArm = 2; }
            }
            _lxaPrevPhi2 = ph;
            if (FrameIrqShim) FrameIrqShimStep();
        }

        // ── Frame-IRQ flag hold shim (test mode only) ────────────────────────────────────────
        // The frame-IRQ RS pair (frame_irq / /frame_irq) loses its state to an intra-settle
        // transient when a $4017 write wave coincides with the apu_clk1 falling edge (blargg
        // apu_test 3-irq_flag #6: writing $00/$80 must NOT clear the flag). The netlist's clear
        // terms match APUSim exactly ({read-$4015 (node 13170), frm_intmode level, _res}) and
        // ALL are inactive post-settle at the false clear — the same same-half-cycle race family
        // as the DMC latch. Shim: if the flag fell in a step with no legitimate clear term
        // active, restore the pair (dual-side, cross-coupled).
        public static bool FrameIrqShim = false;
        private static int _fiFlag = EmptyNode, _fiNFlag = EmptyNode, _fiRdClr = EmptyNode, _fiInh = EmptyNode, _fiRes = EmptyNode;
        private static byte _fiPrev;

        public static void EnableFrameIrqShim()
        {
            _fiFlag  = LookupNode("cpu.frame_irq");
            _fiNFlag = LookupNode("cpu./frame_irq");
            _fiRdClr = LookupNode("cpu.#13170");
            _fiInh   = LookupNode("cpu.frm_intmode");
            _fiRes   = LookupNode("cpu._res");
            if (_fiFlag == EmptyNode || _fiNFlag == EmptyNode || _fiRdClr == EmptyNode || _fiInh == EmptyNode || _fiRes == EmptyNode)
            { Console.Error.WriteLine("# [shim] frame-IRQ shim: nodes unresolved — disabled"); FrameIrqShim = false; return; }
            _fiPrev = NodeStates[_fiFlag];
            FrameIrqShim = true;
        }

        private static void FrameIrqShimStep()
        {
            byte now = NodeStates[_fiFlag];
            if (_fiPrev == 1 && now == 0
                && NodeStates[_fiRdClr] == 0 && NodeStates[_fiInh] == 0 && NodeStates[_fiRes] == 0)
            {
                SetHigh(_fiFlag); SetLow(_fiNFlag);
                SetFloat(_fiFlag); SetFloat(_fiNFlag);
                now = NodeStates[_fiFlag];
            }
            _fiPrev = now;
            if (Dbl2007Shim) Dbl2007ShimStep();
        }

        // ── $2001 write-effect delay shim (test mode only, opt-in) ────────────────────────────
        // The CPU->PPU register-write transport delay is idealized to 0 in our two-netlist
        // board integration. The only proven casualty so far is the pre-render dot-339
        // even/odd skip race in 10-even_odd_timing. A broad "delay every PPUMASK transition"
        // experiment made the ROM lose its verdict path because the test deliberately schedules
        // several $2001 writes with cycle precision. Keep this instrument scoped to the late
        // side of the skip decision window: the A=4 enable case lands at dot 337 and is already
        // correct, while the failing A=5 enable case is one dot later. Disable is the mirror:
        // the failing late-disable edge needs to hold the old enabled value through dot 339.
        // Reads ($2002/VBL) and setup/console PPUMASK writes stay untouched.
        public static bool PpuWriteDelay = false;
        public static int PpuWriteDelayHc = 0;
        private static int _pwdBkg = EmptyNode, _pwdSpr = EmptyNode, _pwdNBkg = EmptyNode, _pwdNSpr = EmptyNode;
        private static int[] _pwdHp = [], _pwdVp = [];
        private static int _pwdBkgPrev, _pwdSprPrev, _pwdBkgHold, _pwdSprHold;
        private static int _pwdBkgClamp = EmptyNode, _pwdSprClamp = EmptyNode;
        private static long _pwdBkgRel = -1, _pwdSprRel = -1;
        private static bool _pwdDebug;

        public static void EnablePpuWriteDelay(int hc)
        {
            if (hc <= 0) { PpuWriteDelay = false; return; }
            _pwdBkg = LookupNode("ppu.bkg_enable");
            _pwdSpr = LookupNode("ppu.spr_enable");
            _pwdNBkg = LookupNode("ppu./bkg_enable");
            _pwdNSpr = LookupNode("ppu./spr_enable");
            var hp = new List<int>(); ResolveNodes("ppu.hpos[8:0]", hp, quiet: true);
            var vp = new List<int>(); ResolveNodes("ppu.vpos[8:0]", vp, quiet: true);
            _pwdHp = hp.Count == 9 ? hp.ToArray() : Array.Empty<int>();
            _pwdVp = vp.Count == 9 ? vp.ToArray() : Array.Empty<int>();
            if (_pwdBkg == EmptyNode || _pwdSpr == EmptyNode || _pwdNBkg == EmptyNode || _pwdNSpr == EmptyNode || _pwdHp.Length != 9 || _pwdVp.Length != 9)
            { Console.Error.WriteLine("# [shim] ppu-write-delay: nodes unresolved — disabled"); PpuWriteDelay = false; return; }
            _pwdBkgPrev = NodeStates[_pwdBkg]; _pwdSprPrev = NodeStates[_pwdSpr];
            _pwdBkgRel = _pwdSprRel = -1;
            _pwdBkgClamp = _pwdSprClamp = EmptyNode;
            PpuWriteDelayHc = hc;
            _pwdDebug = Environment.GetEnvironmentVariable("PWD_DEBUG") == "1";
            PpuWriteDelay = true;
        }

        private static bool PpuWriteDelayArmedWindow(int oldValue, int newValue)
        {
            int v = ReadBits(_pwdVp);
            int h = ReadBits(_pwdHp);
            if (v != 261 || h > 339) return false;
            if (oldValue == 0 && newValue != 0) return h >= 338;  // late enable
            if (oldValue != 0 && newValue == 0) return h >= 338;  // late disable
            return false;
        }

        private static void PpuWriteDelayDebug(string nodeName, int oldValue, int newValue, bool armed)
        {
            if (!_pwdDebug) return;
            int v = ReadBits(_pwdVp);
            int h = ReadBits(_pwdHp);
            if (v == 261 && h >= 330 && h <= 340)
                Console.Error.WriteLine($"# [pwd] t={Time} v={v} h={h} {nodeName} {oldValue}->{newValue} armed={armed}");
        }

        private static void PpuWriteDelayStep()
        {
            // bkg_enable: hold skip-window transitions for N hc (clamp old value, then release)
            if (_pwdBkgRel >= 0)
            {
                if (Time >= _pwdBkgRel)
                {
                    InstRelease(_pwdBkgClamp);
                    _pwdBkgClamp = EmptyNode;
                    _pwdBkgRel = -1;
                    _pwdBkgPrev = NodeStates[_pwdBkg];
                }
            }
            else
            {
                int now = NodeStates[_pwdBkg];
                if (now != _pwdBkgPrev)
                {
                    bool armed = PpuWriteDelayArmedWindow(_pwdBkgPrev, now);
                    PpuWriteDelayDebug("bkg", _pwdBkgPrev, now, armed);
                    if (armed)
                    {
                        _pwdBkgHold = _pwdBkgPrev;
                        _pwdBkgClamp = _pwdBkgHold == 0 ? _pwdBkg : _pwdNBkg;
                        InstClampLow(_pwdBkgClamp);
                        _pwdBkgRel = Time + PpuWriteDelayHc;
                    }
                    else _pwdBkgPrev = now;
                }
            }

            // spr_enable
            if (_pwdSprRel >= 0)
            {
                if (Time >= _pwdSprRel)
                {
                    InstRelease(_pwdSprClamp);
                    _pwdSprClamp = EmptyNode;
                    _pwdSprRel = -1;
                    _pwdSprPrev = NodeStates[_pwdSpr];
                }
            }
            else
            {
                int now = NodeStates[_pwdSpr];
                if (now != _pwdSprPrev)
                {
                    bool armed = PpuWriteDelayArmedWindow(_pwdSprPrev, now);
                    PpuWriteDelayDebug("spr", _pwdSprPrev, now, armed);
                    if (armed)
                    {
                        _pwdSprHold = _pwdSprPrev;
                        _pwdSprClamp = _pwdSprHold == 0 ? _pwdSpr : _pwdNSpr;
                        InstClampLow(_pwdSprClamp);
                        _pwdSprRel = Time + PpuWriteDelayHc;
                    }
                    else _pwdSprPrev = now;
                }
            }
        }

        // ── $2007 double-read merge shim (test mode only, global) ────────────────────────────
        // Back-to-back $2007 reads (LDA abs,X page-cross dummy + real read) merge into ONE
        // buffer advance in the netlist — matching real hardware — but the reload's
        // staging→inbuf→io→db propagation AND the CPU's A load complete inside the same
        // final-phi2 settle wave (op-probe verified), so the CPU read-through gets the NEW
        // buffer value where all four blargg-documented real patterns keep old/transitional
        // values (double_2007_read). Behavioral references model exactly this at the memory
        // layer: a read that lands mid-reload returns the OLD buffer (AprNes ppu_r_2007 state
        // machine, TriCNES read-latch pipeline). The shim is the logical stand-in for the
        // missing physical propagation delay — hence GLOBAL in test mode, not per-test.
        // ZERO-FOOTPRINT (Gemini consult 2026-07-05): no fake nodes/transistors — a load-time
        // attach renumbers the netlist and re-rolls alignment-lottery races in unrelated
        // tests (measured: dma_2007_read@K=1 flipped to a non-real pattern with zero shim
        // firings). Detection polls in the existing test-mode shim chain; the force uses the
        // instrument-grade InstClampLow (Gnd-class, wins the whole wave — measured: every
        // Set-flag force at every discrete boundary loses, the A load consumes the mid-wave
        // transient). Armed only when the reload lands inside a GENUINE CPU $2007 sample:
        // address bus on a $2007 mirror, read mode, RDY high (a DMC-DMA stall parks ab on
        // $2007 through zombie cycles the CPU never consumes — the real sample comes cycles
        // later with full propagation time), phi2 HIGH (a reload landing in phi1 precedes
        // the next sample by over half a cycle — real HW propagates it; measured on
        // dma_2007_read, where clamping such a reload zeroed the X readout), and not the
        // palette bypass. Clamps only the bits that ROSE (presented value = old∧new, the same
        // transitional class as the documented real patterns); the buffer itself keeps the
        // netlist's merged single advance. Measured race spans: fall 1 hc (micro) to 7 hc
        // (blargg @K=1) after the reload — i.e. the window is "same active phi2 phase".
        public static bool Dbl2007Shim = false;
        private static readonly bool _d27Debug = Environment.GetEnvironmentVariable("PB_DEBUG") != null;
        private static int _d27Pal = EmptyNode, _d27Rw = EmptyNode, _d27Rdy = EmptyNode, _d27Phi2 = EmptyNode, _d27Nr2007 = EmptyNode;
        private static int[] _d27Inbuf = Array.Empty<int>(), _d27Ab = Array.Empty<int>(), _d27Db = Array.Empty<int>();
        private static int _d27Prev, _d27Clamped, _d27Phi2Prev;
        private static long _d27T0;

        public static void EnableDbl2007Shim()
        {
            _d27Pal  = LookupNode("ppu.read_2007_output_palette");
            _d27Nr2007 = LookupNode("ppu./r2007");
            _d27Rw   = LookupNode("cpu.rw");
            _d27Rdy  = LookupNode("cpu.rdy");
            _d27Phi2 = LookupNode("cpu.phi2");
            var ib = new List<int>(); ResolveNodes("ppu.inbuf[7:0]", ib, quiet: true);
            var ab = new List<int>(); ResolveNodes("cpu.ab[15:0]", ab, quiet: true);
            var db = new List<int>(); ResolveNodes("cpu.db[7:0]", db, quiet: true);
            if (_d27Pal == EmptyNode || _d27Rw == EmptyNode || _d27Rdy == EmptyNode || _d27Phi2 == EmptyNode
                || _d27Nr2007 == EmptyNode || ib.Count != 8 || ab.Count != 16 || db.Count != 8)
            { Console.Error.WriteLine("# [shim] dbl2007 shim: nodes unresolved — disabled"); Dbl2007Shim = false; return; }
            _d27Inbuf = ib.ToArray(); _d27Ab = ab.ToArray(); _d27Db = db.ToArray();
            _d27Prev = ReadBits(_d27Inbuf);
            _d27Phi2Prev = NodeStates[_d27Phi2];
            _d27Clamped = 0;
            Dbl2007Shim = true;
        }

        private static void Dbl2007ShimStep()
        {
            int now = ReadBits(_d27Inbuf);
            if (now != _d27Prev)
            {
                if (_d27Clamped == 0
                    && NodeStates[_d27Nr2007] == 0
                    && NodeStates[_d27Phi2] != 0
                    && NodeStates[_d27Pal] == 0
                    && (ReadBits(_d27Ab) & 0xE007) == 0x2007
                    && NodeStates[_d27Rw] != 0 && NodeStates[_d27Rdy] != 0)
                {
                    // Reload landed inside a genuine CPU $2007 sample: /r2007 pulse ACTIVE and
                    // phi2 HIGH — the data phase whose imminent fall the reload races. Guards
                    // measured against dma_2007_read: ab parks on $2007 between pulses during
                    // a DMC-DMA stall (nr guard), and a reload landing in phi1 (phi2 guard)
                    // precedes the next sample by half a cycle — real HW propagates both, so
                    // clamping there zeroed the X readout (44&~44=00). Clamp the risen bits
                    // low as a boundary condition of the next wave (phi2 fall + phi1 + A load);
                    // if the fall does not arrive within the analog window (2 hc), the release
                    // path below undoes the clamp BEFORE the sample.
                    int rose = now & ~_d27Prev & 0xFF;
                    if (_d27Debug) Console.Error.WriteLine($"# [d27] t={Time} CLAMP {_d27Prev:X2}->{now:X2} rose={rose:X2} ab={ReadBits(_d27Ab):X4} nr={NodeStates[_d27Nr2007]} rdy={NodeStates[_d27Rdy]} phi2={NodeStates[_d27Phi2]}");
                    for (int i = 0; i < 8; i++)
                        if (((rose >> i) & 1) != 0) InstClampLow(_d27Db[i]);
                    _d27Clamped = rose;
                    _d27T0 = Time;
                }
                _d27Prev = now;
            }
            int phi2 = NodeStates[_d27Phi2];
            if (_d27Clamped != 0
                && ((_d27Phi2Prev == 1 && phi2 == 0)      // CPU sample edge passed (post-settle)
                    || Time - _d27T0 > 24))               // safety: never hold past one CPU cycle
            {
                if (_d27Debug) Console.Error.WriteLine($"# [d27] t={Time} REL dt={Time - _d27T0} why={(_d27Phi2Prev == 1 && phi2 == 0 ? "fall" : "safety")}");
                for (int i = 0; i < 8; i++)
                    if (((_d27Clamped >> i) & 1) != 0) InstRelease(_d27Db[i]);
                _d27Clamped = 0;
                _d27Prev = ReadBits(_d27Inbuf);
            }
            _d27Phi2Prev = phi2;
        }



        // ── Controller input injection (test mode) ──────────────────────────────────────────
        // The controllers are BEHAVIORAL (same abstraction level as the cartridge): the
        // gate-level nes-pad (CD4021 from pslatch modules) cannot express a released button
        // under this engine's group semantics (the latch pass-gates backdrive the buttons
        // nodes; in-group GND beats any external drive). EnableJoypadHandler swaps in the
        // connector-only nes-pad-behavioral def and attaches a Joypad callback that implements
        // the 4021 latch/shift protocol, driving portN.d0 (the LS368 then inverts onto the
        // data bus, so a pressed button reads 1 — real polarity). Button state is a plain
        // byte per pad, bit0=A..bit7=Right (AprNes convention).
        public static bool EnableJoypadHandler = false;   // set BEFORE LoadSystem (test mode only)

        public static bool PadInit() => _joyArmed;

        public static void PadSetButton(int pad, int osIdx, bool pressed)
        {
            if (pad < 0 || pad > 1 || osIdx < 0 || osIdx > 7) return;
            if (pressed) JoyButtons[pad] |= (byte)(1 << osIdx);
            else         JoyButtons[pad] &= (byte)~(1 << osIdx);
        }

        /// <summary>
        /// Reset the NES. <paramref name="full"/> = power-on (clear RAMs, re-power node state, re-alloc the
        /// framebuffer); otherwise a soft reset. Then assert /res for 192 half-cycles and deassert.
        /// Port of handler_nes_system::reset() / softReset().
        /// </summary>
        public static void ResetNes(bool full)
        {
            if (full)
            {
                ResolveMemory("u1.ram")?.Clear();
                ResolveMemory("u4.ram")?.Clear();
                ResolveMemory("cart.chrram.ram")?.Clear();
                Reset();                                  // re-power node state + rebuild hot arrays (frees FrameBuffer too)
                FrameBuffer = AllocArray<uint>(ScreenW * ScreenH);
                RecomputeAllNodes();                      // settle the raw power-on state — MetalNES's resetState() does this
                if (PowerUpStateShim) ApplyPowerUpState();
            }
            if (N_Res != EmptyNode)
            {
                SetHigh(N_Res);
                Step(12 * 8 * 2 + ResetHoldExtraHc);      // = 192 half-cycles with reset asserted (+ phase-experiment extra)
                SetLow(N_Res);
            }
            else
            {
                Console.Error.WriteLine("ResetNes: no 'res' node — sim may not start");
            }
            if (full && PowerUpStateShim) ApplyPowerUpZFlag();   // after /res deassert (see the method comment)
        }

        public static void SoftReset() => ResetNes(full: false);

        // ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Step the simulation until the PPU's in-vblank flag rises (one "frame" boundary), or until
        /// <paramref name="maxHalfCycles"/> have run. Returns the number of half-cycles actually stepped.
        /// Port of the frame-boundary behaviour of handler_nes_system (add_edge_callback on ppu.in_vblank).
        /// </summary>
        public static long RunFrame(long maxHalfCycles = 1_200_000)
        {
            long start = Time;
            if (N_PpuInVblank == EmptyNode)
            {
                // no vblank node available — just step a fixed amount (~one NES frame of half-cycles)
                Step((int)Math.Min(maxHalfCycles, 714_736));
                return Time - start;
            }
            bool prev = NodeStates[N_PpuInVblank] != 0;
            for (long i = 0; i < maxHalfCycles; i++)
            {
                Step(1);
                bool now = NodeStates[N_PpuInVblank] != 0;
                if (!prev && now) break;                  // rising edge → frame boundary
                prev = now;
            }
            return Time - start;
        }
    }
}
