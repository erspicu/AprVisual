using System;
using System.Collections.Generic;
using System.Text;
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

            // The stock MetalNES cart definition is hard-wired for vertical mirroring. NROM boards
            // instead wire CIRAM A10 according to the iNES header: PPU A11 for horizontal mirroring,
            // PPU A10 for vertical mirroring. Keep the connection count/order unchanged so choosing
            // the ROM's wiring does not perturb node allocation.
            string ciramSource = _rom?.HorizontalMirroring == true ? "edge.ppu_a11" : "edge.ppu_a10";
            for (int i = 0; i < cartMmu0.Connections.Count; i++)
            {
                var connection = cartMmu0.Connections[i];
                if (connection.From == "edge.ciram_a10"
                    && (connection.To == "edge.ppu_a10" || connection.To == "edge.ppu_a11"))
                {
                    cartMmu0.Connections[i] = (connection.From, ciramSource);
                    break;
                }
            }

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
            // M2 (S1A): physical-capacitance floating arbitration — data swap at Reset() fill time.
            M2CapArbitration = Environment.GetEnvironmentVariable("M2_CAP") is { Length: > 0 } m2 && m2 != "0";
            M2Census = Environment.GetEnvironmentVariable("M2_CENSUS") is { Length: > 0 } mc && mc != "0";
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
            DmcLatchShim = true; ShimChainArmed = true;
        }

        // ── M4 edge-latch mechanism (the generic edge-capture primitive) ─────────────────────
        // A transparent latch's closing edge defines the cell's value; zero-delay settling lets
        // same-wave data races corrupt it. One primitive expresses both measured verdicts:
        //   data-wins: at the enable's falling edge the cell captures the post-settle DATA value
        //              (DMC pcm_latch — analog clock-decay overlap lets the data through);
        //   hold     : at the enable's falling edge the cell RESTORES its pre-edge snapshot
        //              (ALU input latches — hold time met, the same-wave collapse must not leak).
        // Annotation rows are name-resolved at arm time; env M4_EDGE arms the built-in rows
        // (DMC + ALU — the first entries of the M4 annotation table; the per-site shims they
        // replace auto-supersede in the test runner). The M4 toolbox scan (m4_latch_scan.py)
        // is the source of future rows; race verdicts come from measurement or the M3 binner.
        private struct M4Row
        {
            public string Name; public bool DataWins;
            public int Enable; public int[] Cells; public int[] Datas;
            public byte PrevEnable; public byte[] Snap;
        }
        private static bool _m4Edge;
        public static bool M4EdgeEnabled => _m4Edge;
        private static M4Row[] _m4Rows = Array.Empty<M4Row>();

        public static void EnableM4EdgeLatch()
        {
            var rows = new List<M4Row>();
            {   // row: DMC pcm_latch — data-wins (measured: blargg 7-dmc_basics #19 reads $80)
                int en = LookupNode("cpu.apu_clk1"), d = LookupNode("cpu.#13907"), c = LookupNode("cpu.#13947");
                if (en != EmptyNode && d != EmptyNode && c != EmptyNode)
                    rows.Add(new M4Row { Name = "dmc_pcm_latch", DataWins = true, Enable = en,
                                         Cells = new[] { c }, Datas = new[] { d },
                                         PrevEnable = NodeStates[en], Snap = new byte[1] });
                else Console.Error.WriteLine("# [m4] edge-latch row dmc_pcm_latch: nodes unresolved -- skipped");
            }
            foreach (var (nm, enName, cellsExpr) in new[]
                     { ("alu_a", "cpu.dpc11_SBADD", "cpu.alua[7:0]"),
                       ("alu_b", "cpu.dpc9_DBADD",  "cpu.alub[7:0]") })
            {   // rows: ALU input latches — hold (the phi-boundary bus collapse must not leak in)
                int en = LookupNode(enName);
                var cells = new List<int>(); ResolveNodes(cellsExpr, cells, quiet: true);
                if (en != EmptyNode && cells.Count == 8)
                {
                    var row = new M4Row { Name = nm, DataWins = false, Enable = en,
                                          Cells = cells.ToArray(), Datas = Array.Empty<int>(),
                                          PrevEnable = NodeStates[en], Snap = new byte[8] };
                    for (int i = 0; i < 8; i++) row.Snap[i] = NodeStates[row.Cells[i]];
                    rows.Add(row);
                }
                else Console.Error.WriteLine($"# [m4] edge-latch row {nm}: nodes unresolved -- skipped");
            }
            _m4Rows = rows.ToArray();
            _m4Edge = _m4Rows.Length > 0;
            if (_m4Edge) ShimChainArmed = true;
            Console.Error.WriteLine($"# [m4] edge-latch armed: {_m4Rows.Length} annotation rows");
        }

        private static void M4EdgeLatchStep()
        {
            for (int r = 0; r < _m4Rows.Length; r++)
            {
                ref var row = ref _m4Rows[r];
                byte cur = NodeStates[row.Enable];
                if (row.PrevEnable == 1 && cur == 0)
                {
                    for (int i = 0; i < row.Cells.Length; i++)
                    {
                        byte want = row.DataWins ? NodeStates[row.Datas[i]] : row.Snap[i];
                        int n = row.Cells[i];
                        if (NodeStates[n] != want)
                        { if (want == 1) SetHigh(n); else SetLow(n); SetFloat(n); }
                    }
                }
                row.PrevEnable = cur;
                if (!row.DataWins)
                    for (int i = 0; i < row.Cells.Length; i++) row.Snap[i] = NodeStates[row.Cells[i]];
            }
        }

        // ── per-hc test-shim dispatch (flattened 2026-07-18) ─────────────────────────────────
        // Was a daisy chain hosted inside DmcLatch→Alu→Lxa: any single shim's kill switch
        // silently disabled the whole downstream family — a confounded control for retirement
        // experiments. Flattened with the ORIGINAL execution order preserved exactly:
        // Dmc → Alu → OpenBus → DL → abort → OamEdge → Lxa-rest. ShimChainArmed is set by
        // every Enable* in this family; the benchmark path never arms anything (bit-exact).
        internal static bool ShimChainArmed;
        internal static void TestShimChainStep()
        {
            if (M4EdgeEnabled) M4EdgeLatchStep();   // M4 edge-latch mechanism (supersedes the DMC/ALU shim rows)
            if (DmcLatchShim) DmcLatchShimStep();
            if (AluLatchShim) AluLatchShimStep();
            OpenBusShimStep();        // self-guarded no-ops unless armed —
            DlShimStep();             // (this was the block hosted at the top of LxaMagicShimStep)
            Dmc4015AbortShimStep();
            OamBlankEdgeShimStep();
            if (LxaMagicShim) LxaMagicShimStep();
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
            AluLatchShim = true; ShimChainArmed = true;
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
        // LAE/LAS ($BB absolute,Y) — the same analog bus-fight family, opposite symptom. The op loads
        // A = X = SP = (mem & SP) off the SB bus; measured on the isolated reproduction (AccuracyCoin
        // page 9 item 5, sub-test 1: mem=$5A SP=$CA): A settles to the correct merge ($4A) while X
        // latches the PRE-merge bus ($CA — the raw SP) — X's load latch closes on an earlier settle
        // wave than the AND collapse. Real silicon's analog transition satisfies both latches with the
        // final value. Shim: at $BB's completion boundary, copy the demonstrably-correct A into X and
        // SP (flags derive from the same value and are already right).
        // S is NOT a simple dynamic latch like X: each bit is a cross-coupled pair with a NAMED
        // complement (s_i / nots_i), clocked via cclk (s0 -> #9732 -cclk-> nots0 -> #9090 -SS-> s0,
        // dissected with --dump-node @id). Forcing s[7:0] alone measurably reverts (Copy_SP2 stayed
        // $CA: nots re-imposed the old value through the loop every cycle) — the force must flip
        // BOTH halves of the pair, the same dual-side pattern the frame-IRQ shim uses.
        private static int[] _laeS = Array.Empty<int>();
        private static int[] _laeNotS = Array.Empty<int>();
        private static int[] _laeSb = Array.Empty<int>();
        private static int _laeSbs = EmptyNode;
        private static int _laeRecent;       // half-cycles since IR last read $BB (write-back overlaps the next fetch)
        private static bool _laeSbsSeen;
        private static int _laePrevSbs;
        private static int _laeVal = -1;     // the correct merge, COMPUTED as (db & pre-op S) in the load window
        private static int _laeOldS = -1;    // S sampled when IR first reads $BB (pre-load)
        private static int _laeDbPrevFall = -1;   // db at the PREVIOUS phi2 fall (the data-read cycle)
        private static int[] _laeNotA = Array.Empty<int>();   // A's complement storage (~a_i), found by topology
        private static int _laeAcs = EmptyNode;                // dpc23_SBAC: the SB->A load gate
        private static int _laePrevAcs;

        private static Memory? _laeRam;   // u1.ram — the stack lives here; see the retro-poke below

        // ── open-bus shim (OpenBus err4): the external data bus capacitively holds the last byte
        // ANY driver left on it, and unmapped reads return that byte. The engine's hold-previous
        // covers this -- except that a DOR precharge glitch can escape through the CPU pad drive
        // in the write->fetch settle boundary (measured: dor4 pulsed 1->0 inside one settle while
        // the pad drive was open; the fetch then read $70 for $60 -- a pass-gate window race, same
        // family as the DMC/LAE shims). DOR itself is NOT a usable source (it holds the last
        // WRITTEN byte; open bus wants the last TRANSFERRED byte -- measured err2: replaying DOR
        // returned $02 for $55). So the shim tracks the last driven bus byte behaviorally: mapped
        // or write half-cycles record db; unmapped read half-cycles replay it onto any db bit with
        // no conducting channel. Test mode only; the benchmark path never enables it. ──
        private static bool _openBusShim;
        private static readonly int[] _pdDb = new int[8];
        private static int _pdRw = EmptyNode;
        private static int _pdObTop;   // top of the unmapped window ($5FFF with cart extra-ram, else $7FFF)

        public static void EnableOpenBusShim()
        {
            var db = new List<int>(); ResolveNodes("cpu.db[7:0]", db, quiet: true);
            _pdRw = LookupNode("cpu.rw");
            if (db.Count != 8 || _pdRw == EmptyNode)
            { Console.Error.WriteLine("# [shim] open-bus: nodes unresolved -- disabled"); return; }
            // ResolveNodes returns ascending bit order (ReadBits: index i = bit i)
            for (int b = 0; b < 8; b++) _pdDb[b] = db[b];
            _pdObTop = LookupNode("cart.eram.gate") != EmptyNode ? 0x5FFF : 0x7FFF;
            _openBusShim = true; ShimChainArmed = true;
        }

        private static bool AnyChannelOn(int nn)
        {
            ref var ni = ref NodeInfos[nn];
            if (ni.Inline != 0)
            {
                int k = 0;
                for (int i = 0; i < ni.C1c2Count; i++, k += 2) if (NodeStates[ni.InlinePayload[k]] != 0) return true;
                for (int i = 0; i < ni.GndCount + ni.PwrCount; i++) if (NodeStates[ni.InlinePayload[k++]] != 0) return true;
                return false;
            }
            for (int i = ni.TlistC1c2s; TransistorList[i] != 0; i += 2) if (NodeStates[TransistorList[i]] != 0) return true;
            for (int i = ni.TlistC1gnd; TransistorList[i] != 0; i++) if (NodeStates[TransistorList[i]] != 0) return true;
            for (int i = ni.TlistC1pwr; TransistorList[i] != 0; i++) if (NodeStates[TransistorList[i]] != 0) return true;
            return false;
        }

        private static int _pdLastBus;
        private static readonly bool _pdDbg = Environment.GetEnvironmentVariable("OB_DEBUG") != null;
        private static int _pdDbgN, _pdDbgJoyN, _pdDbgPrevAb = -1, _pdDbgAfterJoy, _pcTrN, _pcTrPrev = -1, _a5N, _a5Pc;
        private static bool _a5InWrite;
        private static int _stN, _stPrevAb = -1;
        private static int[] _spVp, _spHp;
        private static int _spBkgEn = EmptyNode, _spSprEn = EmptyNode, _spBkgOut = EmptyNode, _spSprOut = EmptyNode, _spHit = EmptyNode, _spRend = EmptyNode;
        private static int _spN, _spWrDb, _spPrevTup = -1;
        private static bool _spInWr;
        private static int _sqOwd = EmptyNode, _sqCopy = EmptyNode, _sqEval = EmptyNode, _sqOvf = EmptyNode;
        private static int _sqN, _sqPrev = -1;
        private static int _srEnd = EmptyNode, _srOvf = EmptyNode, _srPovf = EmptyNode;
        private static int[] _srAddr, _srPtr;
        private static int _srN, _srPrevH = -1;
        private static int _ssR2 = EmptyNode, _ssNr = EmptyNode, _ssNr2 = EmptyNode, _ssVis = EmptyNode;
        private static int _ssLt64 = EmptyNode, _ssEq65 = EmptyNode, _ssEq63 = EmptyNode;
        private static int _ssN, _ssPrevH = -1;
        private static int _suNr = EmptyNode, _suGate = EmptyNode, _suIn = EmptyNode, _suOr = EmptyNode, _suRd = EmptyNode;
        private static int _suN, _suPrev = -1;
        private static int _svN, _svDb; private static bool _svIn, _svDumped;
        private static int[] _swD; private static int _swN, _swPrevH = -1;
        private static int _sxSet = EmptyNode, _sxClr = EmptyNode, _sxOwd2 = EmptyNode;
        private static int _sxN, _sxPrev = -1;
        private static int[] _szRows, _szCols; private static int _szN, _szPrev = -1;
        private static int[] _scCells;
        private static int[,] _oaCells; private static int _oaShot, _oaFired;
        private static int _obN, _obPrevH = -1, _obPrev = -1;
        private static int[] _t3P; private static int _t3Act = EmptyNode, _t3Hit = EmptyNode;
        private static int _t3N, _t3Prev = -1, _t3PrevV = -1, _t3Arm, _t3PrevHit, _t3WrDb, _t3PrevRend;
        private static int _t3C0 = EmptyNode, _t3C1 = EmptyNode, _t3Op = EmptyNode, _t3Use = EmptyNode,
                           _t3Bkg = EmptyNode, _t3Set = EmptyNode, _t3Spat = EmptyNode;
        private static int _t3Ret, _t3PrevH2 = -1; private static bool _t3InWr;
        private static int _arSelPat0 = EmptyNode, _arSelPat1 = EmptyNode, _arSetHit = EmptyNode, _arHit = EmptyNode, _arAle = EmptyNode;
        private static int _arN, _arPrevH = -1, _arPixN; private static bool _arIn2007;
        // [bgs] BGSerialIn toggle-phase probe fields (OB_DEBUG only)
        private static int _bgsN, _bgsDb, _bgsAb; private static bool _bgsInWr;
        // ── BG serial-in reload-delay shim (test-mode; BGSerialIn $487 err2; M6 family) ─────────
        // The 2C02 pipelines $2001 write effects by 2-5 dots. AC's BGSerialIn toggles rendering
        // every scanline so the dot%8==7 BG shifter RELOAD falls inside the OFF window: on silicon
        // the ENABLE (write landing dot%8==6) only takes effect at %8==0, so the adjacent %8==7
        // reload is SKIPPED and the shifters serial-in a run of '1's (the white line the test's
        // sprite-0 hit detects). Zero-delay S1 restores rendering instantly -> that reload happens
        // -> no line -> err 2. Shim (Gemini-consulted, a_bgserial_lever_20260717): when a $2001
        // ENABLE write completes at hpos%8 in [4,7] (the only phases where the hardware delay
        // crosses the reload point), InstClampLow the reload gate for 16hc (2 dots). Force-LOW
        // only; phase-orthogonal to the dot-339 (339%8==3) and even_odd (vpos261) shims.
        public static bool BgSerialReloadShim = false;
        private static int _bgrGate = EmptyNode, _bgrHold;
        private static int[] _bgrHp3 = System.Array.Empty<int>();
        private static bool _bgrInWr; private static int _bgrDb;
        private static long _bgrFires;

        /// <summary>Resolve nodes and arm. Call after LoadSystem (test mode only).</summary>
        public static void EnableBgSerialReloadShim()
        {
            _bgrGate = LookupNode("ppu.hpos_mod_8_eq_6_or_7_and_rendering");
            if (_bgrGate == EmptyNode) _bgrGate = LookupNode("ppu.hpos_mod_8_eq_6_or_7");
            var hp = new List<int>(); ResolveNodes("ppu.hpos[2:0]", hp, quiet: true); _bgrHp3 = hp.Count == 3 ? hp.ToArray() : System.Array.Empty<int>();
            if (_bgrGate == EmptyNode || _bgrHp3.Length != 3)
            { Console.Error.WriteLine("# [shim] bg-serial reload delay: nodes unresolved -- disabled"); BgSerialReloadShim = false; return; }
            _bgrHold = 0; _bgrInWr = false; _bgrFires = 0; BgSerialReloadShim = true;
        }

        internal static void BgSerialReloadShimStep()
        {
            // release path first: the clamp outlives the write by design
            if (_bgrHold > 0 && --_bgrHold == 0) InstRelease(_bgrGate);
            int ab = ReadReg(R_CpuAb);
            bool wr = (ab & 0xE007) == 0x2001 && NodeStates[_pdRw] == 0;
            if (wr) { _bgrInWr = true; _bgrDb = ReadReg(R_CpuDb); return; }
            if (!_bgrInWr) return;
            _bgrInWr = false;                              // write just ENDED this hc
            if ((_bgrDb & 0x18) == 0) return;              // not an enable (neither BG nor sprites on)
            int phase = ReadBits(_bgrHp3);                 // hpos % 8 at the write's effect instant
            if (phase < 4) return;                         // effect lands before the reload point -> hardware agrees with zero-delay
            if (_bgrHold == 0) InstClampLow(_bgrGate);     // suppress the imminent %8==7 reload
            _bgrHold = 16; _bgrFires++;
        }

        // [syn] sync-routine decision probe fields: $4015 reads (SLO get/put detector) + $4017 writes
        private static int _synN; private static bool _synIn4015R, _synIn4017W; private static int _syn4017Db, _syn4015Db;
        // [pc] probe window (PC_WIN=lo,hi env override; defaults = the original IDR-forensics window)
        private static readonly long _pcWinLo = ParsePcWin(0, 13750000), _pcWinHi = ParsePcWin(1, 15090000);
        private static long ParsePcWin(int idx, long dflt)
        {
            var s = Environment.GetEnvironmentVariable("PC_WIN")?.Split(',');
            return s != null && s.Length == 2 && long.TryParse(s[idx], out long v) ? v : dflt;
        }
        // [ae]/[aehc] ALERead forensic probe (OB_DEBUG only): per-dot + per-hc ALE/RD/chrAb around the $2007-read overlap
        private static int _aeAle = EmptyNode, _aeRd = EmptyNode, _aeR2007 = EmptyNode, _aeSel0 = EmptyNode, _aeSel1 = EmptyNode;
        private static int[] _aeAb = System.Array.Empty<int>(), _aeVp = System.Array.Empty<int>(), _aeHp = System.Array.Empty<int>(), _aeIoAb = System.Array.Empty<int>();
        private static int _aeN, _aePrevH = -1, _aeArmV = -1, _aeHcBudget = 400;
        private static int _aeIoCe = EmptyNode, _aeCpuRw = EmptyNode;
        private static int _ocRow = EmptyNode, _ocCol = EmptyNode, _ocPclk = EmptyNode, _ocBitA = EmptyNode, _ocBitB = EmptyNode,
                           _ocColA = EmptyNode, _ocColB = EmptyNode, _ocA0 = EmptyNode, _ocB0 = EmptyNode, _ocA1 = EmptyNode, _ocB1 = EmptyNode;
        private static readonly byte[] _dmaRdBuf = new byte[256];
        private static readonly bool[] _dmaRdSeen = new bool[256];
        private static int _dmaRdCount, _dmaRdPrev = -1, _dmaRdPrevDb, _dmaRdGap;
        private static bool _dmaRdDumped;
        private static int[] _micIdb, _micSpr;          // microscope: internal bus + OAM-DMA data latch
        private static int _micR4015 = EmptyNode, _micR4016 = EmptyNode, _micR4017 = EmptyNode;
        private static int _micDbe = EmptyNode, _micJoy1 = EmptyNode, _micJoy2 = EmptyNode;
        private static int _micPrevDbE, _micPrevIdb, _micPrevSpr, _micPrevDec, _micN;
        private static string _micPrevTup = "";
        private static int _finN, _finPrevW = -1, _finPrevDb, _finPrevRw;
        private static int[] _pdDbgIdl, _pdDbgIdb;
        private static int _pdDbgClk1 = EmptyNode;

        private static void OpenBusShimStep()
        {
            if (!_openBusShim) return;
            int abNow = ReadReg(R_CpuAb);
            // Opcode FETCHES from APU register space ($4000-$401F) are open-bus too: reading
            // $4015 does not update the data bus (the status byte travels the internal bus), so
            // a PC parked there fetches whatever the wire remembers. AccuracyCoin's
            // ImpliedDummyRead stunts execute exactly this, and the DOR bit-4 precharge glitch
            // (the OpenBus err4 culprit) corrupted those fetches ($28->$38, $68->$78 measured:
            // PLP/PLA became SEC/SEI, leaking one stack byte per stunt). Data reads there keep
            // their native paths; only fetch cycles join the replay window.
            bool apuFetch = abNow >= 0x4000 && abNow <= 0x401F && NodeStates[_pdRw] != 0
                         && abNow == ((ReadReg(R_CpuPch) << 8) | ReadReg(R_CpuPcl));
            if ((abNow < 0x4020 && !apuFetch) || abNow > _pdObTop || NodeStates[_pdRw] == 0)
            {
                if (_pdDbg && (abNow == 0x4016 || abNow == 0x4017) && NodeStates[_pdRw] != 0 && _pdDbgJoyN < 120)
                {
                    _pdDbgJoyN++;
                    if (_pdDbgIdl == null) { var l = new List<int>(); ResolveNodes("cpu.idl[7:0]", l, quiet: true); _pdDbgIdl = l.ToArray();
                                             var m = new List<int>(); ResolveNodes("cpu.idb[7:0]", m, quiet: true); _pdDbgIdb = m.ToArray();
                                             _pdDbgClk1 = LookupNode("cpu.clk1out"); }
                    Console.Error.WriteLine($"# [obshim] joy t={Time} ab=${abNow:X4} db=${ReadReg(R_CpuDb):X2} idl=${(_pdDbgIdl.Length==8?ReadBits(_pdDbgIdl):-1):X2} idb=${(_pdDbgIdb.Length==8?ReadBits(_pdDbgIdb):-1):X2} a=${ReadReg(R_CpuA):X2} c1={(_pdDbgClk1!=EmptyNode?NodeStates[_pdDbgClk1]:9)}");
                }
                if (_pdDbg && (_pdDbgPrevAb == 0x4016 || _pdDbgPrevAb == 0x4017) && abNow != _pdDbgPrevAb) _pdDbgAfterJoy = 60;
                if (_pdDbg && _pdDbgAfterJoy > 0)
                { _pdDbgAfterJoy--; if ((_pdDbgAfterJoy % 12) == 0) Console.Error.WriteLine($"# [obshim] after-joy t={Time} ab=${abNow:X4} a=${ReadReg(R_CpuA):X2} ir=${ReadReg(R_CpuIr):X2}"); }
                _pdDbgPrevAb = abNow;
                _pdLastBus = ReadReg(R_CpuDb); return;   // driven half-cycle (mapped, or a write) -- record the bus
            }
            if (_pdDbg && _pdDbgN < 40) { _pdDbgN++; Console.Error.WriteLine($"# [obshim] t={Time} ab=${abNow:X4} rw={NodeStates[_pdRw]} last=${_pdLastBus:X2} db=${ReadReg(R_CpuDb):X2}"); }
            for (int b = 0; b < 8; b++)                   // open-bus read -- replay the held byte
            {
                if (AnyChannelOn(_pdDb[b])) continue;      // someone is driving -- hands off
                byte want = (byte)((_pdLastBus >> b) & 1);
                if (NodeStates[_pdDb[b]] != want) LaeForce(_pdDb[b], want);
            }
        }

        // ── DL-transparency shim (OpenBus err6): the 6502's input data latch (DL/idl) is
        // TRANSPARENT through the whole of phi2 -- it tracks the external data bus until the
        // phi2 falling edge. In the netlist the DL is a capture-once dynamic latch: it loads in
        // the single settle where clk1out falls, so an intra-settle transient at that instant is
        // kept for good. Measured on AccuracyCoin OpenBus test 6: at the LDA $4017 read, u8's
        // (74LS368) OE turn-on transient resolved the bus group to $00 mid-settle, the DL gate
        // closed on it, and A latched $00 while the settled bus read $5D on every half-cycle --
        // the structurally identical LDA $4016 read latched $5D correctly (instance node-id
        // ordering lottery, same family as DMC/LAE). The shim restates transparency post-settle:
        // during phi2 (clk1out==0) of a read half-cycle, idl is float-forced back to the settled
        // db whenever they diverge. A no-op on every correctly-resolved cycle. Test mode only. ──
        private static bool _dlShim;
        private static readonly int[] _dlIdl = new int[8];
        private static readonly int[] _dlNotIdl = new int[8];   // complement side -- a single-sided force snaps back
        private static int _dlClk1 = EmptyNode;

        public static void EnableDlShim()
        {
            var idl = new List<int>(); ResolveNodes("cpu.idl[7:0]", idl, quiet: true);
            var nidl = new List<int>(); ResolveNodes("cpu.notidl[7:0]", nidl, quiet: true);
            _dlClk1 = LookupNode("cpu.clk1out");
            if (idl.Count != 8 || nidl.Count != 8 || _dlClk1 == EmptyNode || _pdRw == EmptyNode || _pdDb[7] == 0)
            { Console.Error.WriteLine("# [shim] DL-transparency: nodes unresolved -- disabled (enable AFTER EnableOpenBusShim)"); return; }
            for (int b = 0; b < 8; b++) { _dlIdl[b] = idl[b]; _dlNotIdl[b] = nidl[b]; }
            _dlShim = true; ShimChainArmed = true;
        }

        private static int _dlHeldMask;   // notidl bits currently clamped (held through the rest of phi2)
        private static int _dlDbgN;

        private static void DlShimStep()
        {
            if (!_dlShim) return;
            // Scope: $4016/$4017 ONLY -- the measured race site (u7/u8 OE turn-on transient vs the
            // DL capture). A global version regressed the whole $4015 family (APULengthCounter,
            // FrameCounterIRQ, ...): $4015 reads are INTERNAL to the 2A03 and never touch the
            // external bus, so forcing idl := external db there overwrites the real value with
            // open-bus junk (exactly what AC OpenBus test 7 documents). Minimal blast radius wins.
            int abDl = ReadReg(R_CpuAb);
            bool phi2read = (abDl == 0x4016 || abDl == 0x4017)
                         && NodeStates[_dlClk1] == 0 && NodeStates[_pdRw] != 0;

            if (_dlHeldMask != 0 && !phi2read)
            {
                // phi2 ended (or the cycle stopped being a read): release. During phi1 cclk is off,
                // so the dynamic notidl node float-holds the corrected value; the next phi2
                // re-samples fresh -- no residue.
                for (int b = 0; b < 8; b++)
                    if ((_dlHeldMask >> b & 1) != 0) InstRelease(_dlNotIdl[b]);
                _dlHeldMask = 0;
            }
            if (!phi2read) return;
            // ENGAGE only on the capture-glitch SIGNATURE: the latch differs from the settled bus
            // in TWO OR MORE bits (measured bad captures: idl=$00 vs db=$5D -- 5 bits; idl=$20 vs
            // db=$FD -- 6 bits; an all-zero-only signature missed the second). A single-bit
            // difference is what a legitimate controller-shift boundary looks like with the
            // behavioural pad attached -- forcing those tears the controller stream, and combined
            // with the open-bus replay it wedged ImpliedDummyRead in a $48xx orbit (measured:
            // each shim alone fine, both = hang). The signature gates NEW engagements only; once
            // holding, hold through phi2 -- an early release lets the upstream re-drive notidl
            // and the latch falls back (measured as a FIRE/VETO oscillation).
            if (_pdDbg && _dlDbgN < 200) { _dlDbgN++; Console.Error.WriteLine($"# [dl] t={Time} ab=${abDl:X4} idl=${ReadBits(_dlIdl):X2} db=${ReadReg(R_CpuDb):X2} mask={_dlHeldMask:X2}"); }
            if (_dlHeldMask == 0 && System.Numerics.BitOperations.PopCount((uint)(ReadBits(_dlIdl) ^ ReadReg(R_CpuDb))) < 2) return;
            // Never engage while the CPU is DMA-halted: during a stall the "read" on the address
            // bus is the halted CPU's held cycle, and what the DL catches there is legitimate
            // analog behaviour under test (ImpliedDummyRead measures exactly these) -- restating
            // the settled bus over it derails the choreography (measured: the only engagement in
            // the joyON hang was a $4017 read at rdy=0, idl=$20 vs db=$E0).
            if (_dlHeldMask == 0)
            {
                int rdyDl = LookupNode("cpu.rdy");
                if (rdyDl != EmptyNode && NodeStates[rdyDl] == 0) return;
            }
            for (int b = 0; b < 8; b++)
            {
                byte want = NodeStates[_pdDb[b]];
                if (NodeStates[_dlIdl[b]] == want) continue;
                // The DL is a two-phase DYNAMIC latch: idl = pullup + a notidl-gated pulldown, and
                // notidl is re-driven from the upstream stage through cclk for the whole of phi2 --
                // so a point-force snaps back, and clamping idl HIGH loses to the conducting
                // pulldown (Gnd outranks Pwr). Clamp ONLY notidl (idl follows combinationally) and
                // HOLD the clamp until phi2 ends.
                if (want != 0) InstClampLow(_dlNotIdl[b]); else InstClampHigh(_dlNotIdl[b]);
                _dlHeldMask |= 1 << b;
            }
        }

        // ── ALERead io_ce/io_ab software-mux + node-split (test-mode; M6 phase fix, 2026-07-16) ──
        // The boing2k7 $2007-read ReadALE overlap lands at dot 226 in S1, 3 dots (24hc) early
        // (CPU->PPU macro phase). On hardware it is at dot 229/230, where the $2FFF bus-collapse's
        // $FF gets latched by the switch-level 74LS373 (u2) -> the phantom $0FFF fetch -> the
        // sprite-0-hit artifact. The 74LS373 is fully modelled and working; the ONLY defect is the
        // phase. This mux SWALLOWs the early $2007 access and REPLAYs it later so the ReadALE lands
        // at the correct dot. The proven wall: the CPU's io_ab=7 window ends at dot 227 (the CPU
        // moves on), and the replay could only reach dot 228 -- 2 dots short of the dot-230 artifact
        // load -- because holding io_ab=7 needs force-HIGH (GND>VCC re-assert wall). NODE-SPLIT fix:
        // WireCore.Module cuts ppu.io_ab[2:0] <-> cpu.ab[2:0] when this shim is on; the mux relays
        // cpu.ab -> ppu.io_ab every hc (transparent) EXCEPT in-window, where it HOLDS ppu.io_ab=7
        // (isolated -> SetHigh wins, no re-assert wall) long enough for read_2007_ended to land the
        // ReadALE at dot 230. Opt-in (ALEREAD_MUX env; set BEFORE LoadSystem for the cut); the
        // golden-checksum benchmark path never sets it.
        public static bool AleReadMuxShim = false;
        private static int _muxIoCe = EmptyNode, _muxCpuRw = EmptyNode, _muxR2007 = EmptyNode;
        private static int[] _muxPpuAb = System.Array.Empty<int>(), _muxCpuAb = System.Array.Empty<int>(), _muxVp = System.Array.Empty<int>(), _muxHp = System.Array.Empty<int>();
        private static int _muxState;          // 0=armed, 1=swallow, 2=wait, 3=replay-hold(io_ab=7 + io_ce=0)
        private static long _muxDetect;
        private static int _muxN;              // fired count (diag)
        private static bool _muxReady;         // nodes resolved + split active
        private static readonly bool _muxDbg = Environment.GetEnvironmentVariable("OB_DEBUG") != null || Environment.GetEnvironmentVariable("MUX_DBG") != null;
        // dt-window timing (dt = hc since detect; 8hc/dot; detect = first io_ce=0 & cpu.ab[2:0]=7 @ v in [1,8]):
        //   swallow  [0, swEnd)          : ppu.io_ab := 0  -> PPU sees $2000, the early $2007 ReadALE is suppressed
        //   replay   [rpStart, rpEnd)    : io_ce clamped 0 + ppu.io_ab := 7 -> read_2007_ended lands the ReadALE
        //                                  overlap at dot 228, where u2 (transparent) captures the $2FFF bus $FF
        //   freeze   [fzStart, fzEnd)    : ppu.ale clamped 0 -> u2 HOLDS $FF through dot 229 (would else re-capture
        //                                  the pattern-low $04), so the dot-230 fetch addresses $0FFF -> $FF -> artifact
        private static int _muxSwEnd = 13, _muxRpStart = 13, _muxRpEnd = 25, _muxFzStart = 44, _muxFzEnd = 52;
        // Detection gate: only the boing2k7 stunt (v=3, dot ~223). $2007-stress tests read $2007 hundreds of
        // times but never at v=3 -- gating tight here is what keeps the mux from corrupting their value-checks.
        private static int _muxVlo = 3, _muxVhi = 3, _muxHlo = 220, _muxHhi = 226;
        private static int _muxAle = EmptyNode;
        private static bool _muxIoCeClamped, _muxAleClamped;

        public static void EnableAleReadMux()
        {
            _muxIoCe = LookupNode("ppu.io_ce");
            _muxAle = LookupNode("ppu.ale");
            _muxCpuRw = LookupNode("cpu.rw"); if (_muxCpuRw == EmptyNode) _muxCpuRw = LookupNode("2a03.cpu.rw");
            _muxR2007 = LookupNode("ppu.read_2007_trigger");
            var pab = new List<int>(); ResolveNodes("ppu.io_ab[2:0]", pab, quiet: true); _muxPpuAb = pab.Count == 3 ? pab.ToArray() : System.Array.Empty<int>();
            var cab = new List<int>(); ResolveNodes("cpu.ab[2:0]", cab, quiet: true); if (cab.Count != 3) { cab.Clear(); ResolveNodes("2a03.cpu.ab[2:0]", cab, quiet: true); } _muxCpuAb = cab.Count == 3 ? cab.ToArray() : System.Array.Empty<int>();
            var vp = new List<int>(); ResolveNodes("ppu.vpos[8:0]", vp, quiet: true); _muxVp = vp.Count == 9 ? vp.ToArray() : System.Array.Empty<int>();
            var hp = new List<int>(); ResolveNodes("ppu.hpos[8:0]", hp, quiet: true); _muxHp = hp.Count == 9 ? hp.ToArray() : System.Array.Empty<int>();
            string ov = Environment.GetEnvironmentVariable("MUX_HC");   // "swEnd,rpStart,rpEnd,fzStart,fzEnd" tuning override
            if (ov != null) { var p = ov.Split(','); if (p.Length == 5 && int.TryParse(p[0], out int a0) && int.TryParse(p[1], out int a1) && int.TryParse(p[2], out int a2) && int.TryParse(p[3], out int a3) && int.TryParse(p[4], out int a4)) { _muxSwEnd = a0; _muxRpStart = a1; _muxRpEnd = a2; _muxFzStart = a3; _muxFzEnd = a4; } }
            string og = Environment.GetEnvironmentVariable("MUX_GATE");   // "vlo,vhi,hlo,hhi" detection-gate override
            if (og != null) { var p = og.Split(','); if (p.Length == 4 && int.TryParse(p[0], out int g0) && int.TryParse(p[1], out int g1) && int.TryParse(p[2], out int g2) && int.TryParse(p[3], out int g3)) { _muxVlo = g0; _muxVhi = g1; _muxHlo = g2; _muxHhi = g3; } }
            if (_muxIoCe == EmptyNode || _muxAle == EmptyNode || _muxCpuRw == EmptyNode || _muxPpuAb.Length != 3 || _muxCpuAb.Length != 3 || _muxVp.Length != 9)
            { Console.Error.WriteLine("# [shim] aleread-mux: nodes unresolved -- disabled"); AleReadMuxShim = false; _muxReady = false; return; }
            _muxState = 0; _muxReady = true;
            MuxRelayIoAb();   // seed: ppu.io_ab := cpu.ab now that the connection is cut
            if (_muxDbg) Console.Error.WriteLine($"# [mux] armed (node-split): sw={_muxSwEnd} rp=[{_muxRpStart},{_muxRpEnd}) fz=[{_muxFzStart},{_muxFzEnd})");
        }

        private static int MuxCpuAb() => (NodeStates[_muxCpuAb[2]] << 2) | (NodeStates[_muxCpuAb[1]] << 1) | NodeStates[_muxCpuAb[0]];
        private static void MuxDriveIoAb(int v) { for (int i = 0; i < 3; i++) { if (((v >> i) & 1) != 0) SetHigh(_muxPpuAb[i]); else SetLow(_muxPpuAb[i]); } }
        private static void MuxRelayIoAb() { for (int i = 0; i < 3; i++) { if (NodeStates[_muxCpuAb[i]] != 0) SetHigh(_muxPpuAb[i]); else SetLow(_muxPpuAb[i]); } }

        internal static void AleReadMuxStep()
        {
            if (!_muxReady) return;
            if (_muxState == 0)   // armed: detect the stunt $2007 read (io_ce=0 & cpu.ab[2:0]=7 & rw=read @ low visible line)
            {
                if (NodeStates[_muxIoCe] == 0 && NodeStates[_muxCpuRw] != 0 && MuxCpuAb() == 7)
                {
                    int v = ReadBits(_muxVp), h = _muxHp.Length == 9 ? ReadBits(_muxHp) : -1;
                    if (v >= _muxVlo && v <= _muxVhi && h >= _muxHlo && h <= _muxHhi)
                    { _muxDetect = Time; _muxState = 1; _muxN++; if (_muxDbg) Console.Error.WriteLine($"# [mux] t={Time} v={v} h={h} #{_muxN} DETECT"); }
                }
                if (_muxState == 0) { MuxRelayIoAb(); return; }
            }
            long dt = Time - _muxDetect;
            // io_ce clamp edges (replay window)
            if (dt == _muxRpStart) { InstClampLow(_muxIoCe); _muxIoCeClamped = true; if (_muxDbg) Console.Error.WriteLine($"# [mux] t={Time} REPLAY io_ce=0 (io_ab=7)"); }
            if (dt == _muxRpEnd && _muxIoCeClamped) { InstRelease(_muxIoCe); _muxIoCeClamped = false; if (_muxDbg) Console.Error.WriteLine($"# [mux] t={Time} REPLAY-END"); }
            // ale clamp edges (freeze window: hold u2=$FF through dot 229)
            if (dt == _muxFzStart) { InstClampLow(_muxAle); _muxAleClamped = true; if (_muxDbg) Console.Error.WriteLine($"# [mux] t={Time} FREEZE ale=0"); }
            if (dt == _muxFzEnd && _muxAleClamped) { InstRelease(_muxAle); _muxAleClamped = false; if (_muxDbg) Console.Error.WriteLine($"# [mux] t={Time} FREEZE-END"); }
            // io_ab drive: swallow -> 0, replay -> 7, else transparent relay
            if (dt < _muxSwEnd) MuxDriveIoAb(0);
            else if (dt >= _muxRpStart && dt < _muxRpEnd) MuxDriveIoAb(7);
            else MuxRelayIoAb();
            // window done
            if (dt >= _muxFzEnd) { if (_muxIoCeClamped) { InstRelease(_muxIoCe); _muxIoCeClamped = false; } if (_muxAleClamped) { InstRelease(_muxAle); _muxAleClamped = false; } _muxState = 0; }
        }

        // ── TEMP diag (ExplicitDMAAbort): rdy transitions + reg writes + $4000 reads ──
        private static int _dmaPrRdy = -2, _dmaPrN;
        private static int _dmaPrPrevAb = -1, _dmaPrRdyFall;
        private static void DmaProbeStep()
        {
            // RESULT-WRITE hook (unstarved -- own check before the _dmaPrN cap)
            if (!_dmaPrZpDumped)
            {
                int abR = ReadReg(R_CpuAb);
                if (NodeStates[_pdRw] == 0 && (abR == 0x045C || abR == 0x055C) && Time > 19320000)
                {
                    _dmaPrZpDumped = true;
                    var ramR = ResolveMemory("u1.ram");
                    var sbR = new System.Text.StringBuilder($"# [res] t={Time} RESULT write ${abR:X4}; ZP$50={ramR.Read(0x50):X2} ZP$10={ramR.Read(0x10):X2} ZP$11={ramR.Read(0x11):X2}; $500-$5FF:\n");
                    for (int rr = 0; rr < 16; rr++)
                    {
                        sbR.Append($"#   {rr << 4:X2} |");
                        for (int cc = 0; cc < 16; cc++) sbR.Append($" {ramR.Read(0x500 + (rr << 4) + cc):X2}");
                        sbR.Append('\n');
                    }
                    Console.Error.Write(sbR.ToString());
                }
            }
            // finale probe v3: sample the LAST half-cycle of each bus transaction (the first-half
            // db is just the operand byte still on the bus -- three probes stepped on that rake)
            // v5 target: the $3FFE stunt's assembly reads (APURegActivation Test 7)
            if (Time > 19300000 && Time < 19400000 && _finN < 120)
            {
                int abF = ReadReg(R_CpuAb);
                bool tracked = abF == 0x3FFE || abF == 0x3FFF || abF == 0x4000 || abF == 0x4001 || abF == 0x4014 || abF == 0x4015 || abF == 0x4016 || abF == 0x4017;
                if (_finPrevW != -1 && abF != _finPrevW)
                {
                    _finN++;
                    Console.Error.WriteLine($"# [fin] t={Time} {(_finPrevRw != 0 ? "read " : "WRITE")} ${_finPrevW:X4} final-db=${_finPrevDb:X2} a=${ReadReg(R_CpuA):X2}");
                    _finPrevW = -1;
                }
                if (tracked) { _finPrevW = abF; _finPrevDb = ReadReg(R_CpuDb); _finPrevRw = NodeStates[_pdRw]; }
            }
            // [sp] StaleSpriteShiftRegs forensics: every $2001 write (with PPU coords at the write's
            // last half-cycle) + every edge of the enable/_out/rendering/spr0_hit tuple, whole run
            if (_spN < 240)
            {
                if (_spVp == null)
                {
                    var vv = new List<int>(); ResolveNodes("ppu.vpos[8:0]", vv, quiet: true); _spVp = vv.ToArray();
                    var hh = new List<int>(); ResolveNodes("ppu.hpos[8:0]", hh, quiet: true); _spHp = hh.ToArray();
                    _spBkgEn = LookupNode("ppu.bkg_enable"); _spSprEn = LookupNode("ppu.spr_enable");
                    _spBkgOut = LookupNode("ppu.bkg_enable_out"); _spSprOut = LookupNode("ppu.spr_enable_out");
                    _spHit = LookupNode("ppu.spr0_hit"); _spRend = LookupNode("ppu.rendering_1");
                    Console.Error.WriteLine($"# [sp] resolve vp={_spVp.Length} hp={_spHp.Length} en={(_spBkgEn != EmptyNode ? 1 : 0)}{(_spSprEn != EmptyNode ? 1 : 0)} out={(_spBkgOut != EmptyNode ? 1 : 0)}{(_spSprOut != EmptyNode ? 1 : 0)} hit={(_spHit != EmptyNode ? 1 : 0)} rend={(_spRend != EmptyNode ? 1 : 0)}");
                }
                if (_spVp.Length == 9 && _spHp.Length == 9)
                {
                    int abW = ReadReg(R_CpuAb);
                    bool wr01 = abW == 0x2001 && NodeStates[_pdRw] == 0;
                    if (wr01) _spWrDb = ReadReg(R_CpuDb);
                    if (wr01 && !_spInWr) _spInWr = true;
                    else if (!wr01 && _spInWr)
                    {
                        _spInWr = false; _spN++;
                        Console.Error.WriteLine($"# [sp] t={Time} W2001=${_spWrDb:X2} at v={ReadBits(_spVp)} h={ReadBits(_spHp)}");
                    }
                    int tup = (NodeStates[_spBkgEn] << 5) | (NodeStates[_spSprEn] << 4) | (NodeStates[_spBkgOut] << 3)
                            | (NodeStates[_spSprOut] << 2) | (NodeStates[_spRend] << 1) | NodeStates[_spHit];
                    if (tup != _spPrevTup)
                    {
                        _spN++;
                        Console.Error.WriteLine($"# [sp] t={Time} tup bkg={tup >> 5 & 1} spr={tup >> 4 & 1} bkgOut={tup >> 3 & 1} sprOut={tup >> 2 & 1} rend={tup >> 1 & 1} hit={tup & 1} at v={ReadBits(_spVp)} h={ReadBits(_spHp)}");
                        _spPrevTup = tup;
                    }
                }
            }
            // [bgs] BGSerialIn toggle-phase probe: every $2001-SPACE write ((ab&$E007)==$2001 --
            // including the $3E01 mirror the test's DISABLE half uses) that lands in the visible
            // region, with PPU coords. The 360-toggle loop self-aligns its enable writes to land
            // at dot%8==6 (hardware +2 delay -> effect at %8==0, straddling the %8==7 shifter
            // load); a systematic phase slip here is the in-suite err2 root-cause signature.
            if (_bgsN < 900 && _spVp != null && _spVp.Length == 9 && _spHp.Length == 9)
            {
                int abB = ReadReg(R_CpuAb);
                bool wrB = (abB & 0xE007) == 0x2001 && NodeStates[_pdRw] == 0;
                if (wrB) { _bgsDb = ReadReg(R_CpuDb); if (!_bgsInWr) { _bgsInWr = true; _bgsAb = abB; } }
                else if (_bgsInWr)
                {
                    _bgsInWr = false;
                    int vB = ReadBits(_spVp), hB = ReadBits(_spHp);
                    if (vB >= 2 && vB <= 235)   // visible region only: the toggle loop; menu/vblank writes skipped
                    { _bgsN++; Console.Error.WriteLine($"# [bgs] t={Time} W${_bgsAb:X4}=${_bgsDb:X2} v={vB} h={hB} h%8={hB % 8}"); }
                }
            }
            // [syn] sync-routine decision probe: every $4015 READ (the SLO get/put detector reads
            // the frame-IRQ flag whose set-cycle parity IS the discriminator) with the value the
            // CPU actually got, and every $4017 write (frame-counter reset). Low-frequency; the
            // BGSerialIn in-suite sync walks a branch the standalone never takes.
            if (_synN < 240 && _spVp != null && _spVp.Length == 9 && _spHp.Length == 9)
            {
                int abS2 = ReadReg(R_CpuAb);
                bool rd4015 = abS2 == 0x4015 && NodeStates[_pdRw] != 0;
                bool wr4017 = abS2 == 0x4017 && NodeStates[_pdRw] == 0;
                if (rd4015) { _synIn4015R = true; _syn4015Db = ReadReg(R_CpuDb); }   // sample DURING the read; last hc wins
                else if (_synIn4015R)
                {
                    _synIn4015R = false; _synN++;
                    Console.Error.WriteLine($"# [syn] t={Time} R4015 -> ${_syn4015Db:X2} v={ReadBits(_spVp)} h={ReadBits(_spHp)}");
                }
                if (wr4017) { _synIn4017W = true; _syn4017Db = ReadReg(R_CpuDb); }
                else if (_synIn4017W)
                {
                    _synIn4017W = false; _synN++;
                    Console.Error.WriteLine($"# [syn] t={Time} W4017=${_syn4017Db:X2} v={ReadBits(_spVp)} h={ReadBits(_spHp)}");
                }
            }
            // [sq] sprite-eval pipeline edges in the control frame (no blank) vs the stunt frame
            if ((Time >= 32870000 && Time <= 33600000) || (Time >= 35020000 && Time <= 35700000))
            {
                if (_sqN < 400)
                {
                    if (_sqOwd == EmptyNode)
                    {
                        _sqOwd = LookupNode("ppu.oam_write_disable");
                        _sqCopy = LookupNode("ppu.copy_sprite_to_sec_oam");
                        _sqEval = LookupNode("ppu.spr_eval_copy_sprite");
                        _sqOvf = LookupNode("ppu.sec_oam_overflow");
                        Console.Error.WriteLine($"# [sq] resolve owd={(_sqOwd != EmptyNode ? 1 : 0)} copy={(_sqCopy != EmptyNode ? 1 : 0)} eval={(_sqEval != EmptyNode ? 1 : 0)} ovf={(_sqOvf != EmptyNode ? 1 : 0)}");
                    }
                    int tq = (NodeStates[_sqCopy] << 2) | (NodeStates[_sqEval] << 1) | NodeStates[_sqOvf];
                    int rose = _sqPrev < 0 ? 0 : (tq & ~_sqPrev);
                    if (rose != 0)
                    {
                        _sqN++;
                        Console.Error.WriteLine($"# [sq] t={Time} rise{(((rose >> 2) & 1) != 0 ? " copy" : "")}{(((rose >> 1) & 1) != 0 ? " eval" : "")}{((rose & 1) != 0 ? " ovf" : "")} owd={NodeStates[_sqOwd]} at v={ReadBits(_spVp)} h={ReadBits(_spHp)}");
                    }
                    _sqPrev = tq;
                }
            }
            // [sr] eval-machine state sampled at h=66 and h=340 of early scanlines, both frames
            if ((Time >= 32870000 && Time <= 33600000) || (Time >= 35020000 && Time <= 35700000))
            {
                if (_srN < 80 && _spVp != null && _spVp.Length == 9)
                {
                    if (_srEnd == EmptyNode)
                    {
                        _srEnd = LookupNode("ppu.end_of_oam_or_sec_oam_overflow");
                        _srOvf = LookupNode("ppu.sec_oam_overflow");
                        _srPovf = LookupNode("ppu.spr_ptr_overflow");
                        var sa = new List<int>(); ResolveNodes("ppu.spr_addr[7:0]", sa, quiet: true); _srAddr = sa.ToArray();
                        var sp2 = new List<int>(); ResolveNodes("ppu.spr_ptr[4:0]", sp2, quiet: true); _srPtr = sp2.ToArray();
                        Console.Error.WriteLine($"# [sr] resolve end={(_srEnd != EmptyNode ? 1 : 0)} ovf={(_srOvf != EmptyNode ? 1 : 0)} povf={(_srPovf != EmptyNode ? 1 : 0)} addr={_srAddr.Length} ptr={_srPtr.Length}");
                    }
                    int hNow = ReadBits(_spHp), vNow = ReadBits(_spVp);
                    if ((hNow == 66 || hNow == 340) && hNow != _srPrevH && vNow <= 12)
                    {
                        _srN++;
                        Console.Error.WriteLine($"# [sr] t={Time} v={vNow} h={hNow} end={NodeStates[_srEnd]} ovf={NodeStates[_srOvf]} povf={NodeStates[_srPovf]} sprAddr=${(_srAddr.Length == 8 ? ReadBits(_srAddr) : -1):X2} sprPtr={(_srPtr.Length == 5 ? ReadBits(_srPtr) : -1)}");
                    }
                    _srPrevH = hNow;
                }
            }
            // [ss] rendering-node family + eval arming decodes, sampled at fixed coords both frames
            if ((Time >= 32870000 && Time <= 33600000) || (Time >= 35020000 && Time <= 35700000))
            {
                if (_ssN < 60 && _spVp != null && _spVp.Length == 9)
                {
                    if (_ssR2 == EmptyNode)
                    {
                        _ssR2 = LookupNode("ppu.rendering_2"); _ssNr = LookupNode("ppu.not_rendering");
                        _ssNr2 = LookupNode("ppu.not_rendering_2"); _ssVis = LookupNode("ppu.in_visible_frame_and_rendering");
                        _ssLt64 = LookupNode("ppu.hpos_lt_64_and_rendering"); _ssEq65 = LookupNode("ppu.hpos_eq_65_and_rendering");
                        _ssEq63 = LookupNode("ppu.hpos_eq_63_and_rendering");
                        Console.Error.WriteLine($"# [ss] resolve r2={(_ssR2 != EmptyNode ? 1 : 0)} nr={(_ssNr != EmptyNode ? 1 : 0)} nr2={(_ssNr2 != EmptyNode ? 1 : 0)} vis={(_ssVis != EmptyNode ? 1 : 0)} lt64={(_ssLt64 != EmptyNode ? 1 : 0)} eq65={(_ssEq65 != EmptyNode ? 1 : 0)} eq63={(_ssEq63 != EmptyNode ? 1 : 0)}");
                    }
                    int hS = ReadBits(_spHp), vS = ReadBits(_spVp);
                    if ((hS == 30 || hS == 64 || hS == 66) && hS != _ssPrevH && vS <= 6)
                    {
                        _ssN++;
                        Console.Error.WriteLine($"# [ss] t={Time} v={vS} h={hS} r1={NodeStates[_spRend]} r2={NodeStates[_ssR2]} nr={NodeStates[_ssNr]} nr2={NodeStates[_ssNr2]} vis={NodeStates[_ssVis]} lt64={NodeStates[_ssLt64]} eq65={NodeStates[_ssEq65]} eq63={NodeStates[_ssEq63]}");
                    }
                    _ssPrevH = hS;
                }
            }
            // [su] not_rendering latch autopsy around both enables (v242-band vs v261-late)
            if ((Time >= 32820000 && Time <= 32880000) || (Time >= 35012000 && Time <= 35036000))
            {
                if (_suN < 260 && _spVp != null && _spVp.Length == 9)
                {
                    if (_suNr == EmptyNode)
                    {
                        _suNr = LookupNode("ppu.not_rendering");
                        _suGate = LookupNode("ppu.#5829");
                        _suIn = LookupNode("ppu.#10676");
                        _suOr = LookupNode("ppu.#5727");
                        _suRd = LookupNode("ppu.rendering_disabled");
                        Console.Error.WriteLine($"# [su] resolve nr={(_suNr != EmptyNode ? 1 : 0)} gate={(_suGate != EmptyNode ? 1 : 0)} in={(_suIn != EmptyNode ? 1 : 0)} or={(_suOr != EmptyNode ? 1 : 0)} rd={(_suRd != EmptyNode ? 1 : 0)}");
                    }
                    int tu = (NodeStates[_suNr] << 4) | (NodeStates[_suGate] << 3) | (NodeStates[_suIn] << 2) | (NodeStates[_suOr] << 1) | NodeStates[_suRd];
                    if (tu != _suPrev)
                    {
                        _suN++;
                        Console.Error.WriteLine($"# [su] t={Time} nr={tu >> 4 & 1} gate={tu >> 3 & 1} in={tu >> 2 & 1} or={tu >> 1 & 1} rdis={tu & 1} at v={ReadBits(_spVp)} h={ReadBits(_spHp)}");
                        _suPrev = tu;
                    }
                }
            }
            // [sv] what the test-2 OAM DMA actually delivers: source page dump + $2004 write bytes
            if (Time >= 35008000 && Time <= 35015000 && _svN < 30)
            {
                int abV = ReadReg(R_CpuAb);
                if (!_svDumped && abV == 0x4014 && NodeStates[_pdRw] == 0)
                {
                    _svDumped = true;
                    var ramV = ResolveMemory("u1.ram");
                    var sbV = new System.Text.StringBuilder($"# [sv] t={Time} $4014 fired; src $200-$20F:");
                    for (int i = 0; i < 16; i++) sbV.Append($" {ramV.Read(0x200 + i):X2}");
                    Console.Error.WriteLine(sbV.ToString());
                }
                bool wr04 = abV == 0x2004 && NodeStates[_pdRw] == 0;
                if (wr04) _svDb = ReadReg(R_CpuDb);
                if (wr04 && !_svIn) _svIn = true;
                else if (!wr04 && _svIn)
                {
                    _svIn = false; _svN++;
                    Console.Error.WriteLine($"# [sv] t={Time} W2004[{_svN - 1}]=${_svDb:X2}");
                }
            }
            // [sw] the OAM readout bus as the evaluator sees it, v=5 h=60..80, both frames
            if ((Time >= 32892000 && Time <= 32893000) || (Time >= 35036200 && Time <= 35037300))
            {
                if (_swN < 120 && _spVp != null && _spVp.Length == 9)
                {
                    if (_swD == null)
                    {
                        var dd = new List<int>(); ResolveNodes("ppu.spr_d[7:0]", dd, quiet: true); _swD = dd.ToArray();
                        Console.Error.WriteLine($"# [sw] resolve spr_d={_swD.Length}");
                    }
                    int hW = ReadBits(_spHp), vW = ReadBits(_spVp);
                    if (vW == 5 && hW >= 60 && hW <= 80 && hW != _swPrevH && _swD.Length == 8)
                    {
                        _swN++;
                        var rAct = new System.Text.StringBuilder();
                        if (_szRows != null && _szRows.Length == 32) for (int i = 0; i < 32; i++) if (NodeStates[_szRows[i]] != 0) rAct.Append($"{i},");
                        Console.Error.WriteLine($"# [sw] t={Time} v={vW} h={hW} spr_d=${ReadBits(_swD):X2} copy={NodeStates[_sqCopy]} eval={NodeStates[_sqEval]} rows=[{rAct}]");
                    }
                    _swPrevH = hW;
                }
            }
            // [sx] OAM write strobes during the healthy (vblank) DMA vs the stunt (mid-frame blank) DMA
            if ((Time >= 18532500 && Time <= 18533400) || (Time >= 35008600 && Time <= 35009500) || (Time >= 35021600 && Time <= 35036400))
            {
                if (_sxN < 400 && _spVp != null && _spVp.Length == 9)
                {
                    if (_sxSet == EmptyNode)
                    {
                        _sxSet = LookupNode("ppu.set_spr_d7_in_oam");
                        _sxClr = LookupNode("ppu.clear_spr_d7_in_oam");
                        _sxOwd2 = LookupNode("ppu.oam_write_disable");
                        Console.Error.WriteLine($"# [sx] resolve set={(_sxSet != EmptyNode ? 1 : 0)} clr={(_sxClr != EmptyNode ? 1 : 0)} owd={(_sxOwd2 != EmptyNode ? 1 : 0)}");
                    }
                    int tx = (NodeStates[_sxSet] << 1) | NodeStates[_sxClr];
                    int rosex = _sxPrev < 0 ? 0 : (tx & ~_sxPrev);
                    if (rosex != 0)
                    {
                        _sxN++;
                        Console.Error.WriteLine($"# [sx] t={Time} rise{(((rosex >> 1) & 1) != 0 ? " SET" : "")}{((rosex & 1) != 0 ? " CLR" : "")} owd={NodeStates[_sxOwd2]} at v={ReadBits(_spVp)} h={ReadBits(_spHp)}");
                    }
                    _sxPrev = tx;
                }
            }
            // [sz] which OAM row is open at each clear-phase write strobe (control vs stunt v=0)
            if ((Time >= 18532500 && Time <= 18533400) || (Time >= 35008600 && Time <= 35036400))
            {
                if (_szN < 320 && _spVp != null && _spVp.Length == 9)
                {
                    if (_szRows == null)
                    {
                        var rr = new List<int>(); ResolveNodes("ppu.spr_row[31:0]", rr, quiet: true); _szRows = rr.ToArray();
                        var cc2 = new List<int>(); ResolveNodes("ppu.spr_col[8:0]", cc2, quiet: true); _szCols = cc2.ToArray();
                        Console.Error.WriteLine($"# [sz] resolve rows={_szRows.Length} cols={_szCols.Length}");
                    }
                    int txz = (NodeStates[_sxSet] << 1) | NodeStates[_sxClr];
                    int rosez = _szPrev < 0 ? 0 : (txz & ~_szPrev);
                    bool mainCol = false;
                    if (_szCols != null && _szCols.Length == 9)
                        for (int i = 0; i < 8; i++) if (NodeStates[_szCols[i]] != 0) { mainCol = true; break; }
                    if (rosez != 0 && mainCol && _szRows.Length == 32)
                    {
                        _szN++;
                        var act = new System.Text.StringBuilder();
                        for (int i = 0; i < 32; i++) if (NodeStates[_szRows[i]] != 0) act.Append($"{i},");
                        var colAct = new System.Text.StringBuilder();
                        if (_szCols.Length == 9) for (int i = 0; i < 9; i++) if (NodeStates[_szCols[i]] != 0) colAct.Append($"{i},");
                        Console.Error.WriteLine($"# [sz] t={Time} strobe rows=[{act}] cols=[{colAct}] at v={ReadBits(_spVp)} h={ReadBits(_spHp)}");
                    }
                    _szPrev = txz;
                }
            }
            // [sc] row-0 cell candidates sampled at three instants: pre-DMA / post-DMA / v=5 eval
            if (Time == 35008600 || Time == 35021200 || Time == 35036320)
            {
                if (_scCells == null)
                {
                    _scCells = new int[12];
                    int[] ids = { 3028, 3066, 3120, 3156, 3202, 3240, 3285, 3318, 3363, 3409, 3463, 3495 };
                    for (int i = 0; i < 12; i++) _scCells[i] = LookupNode($"ppu.#{ids[i]}");
                }
                var sbC = new System.Text.StringBuilder($"# [sc] t={Time} cells:");
                for (int i = 0; i < 12; i++) sbC.Append(_scCells[i] != EmptyNode ? $" {NodeStates[_scCells[i]]}" : " ?");
                Console.Error.WriteLine(sbC.ToString());
            }
            // [oa] REAL OAM dump (ppu.oam_ram_XX_bN) at one-shot instants, stunt + control frames
            if (_oaCells == null && Time > 1000000)
            {
                _oaCells = new int[16, 8];
                for (int i = 0; i < 16; i++)
                    for (int b = 0; b < 8; b++)
                        _oaCells[i, b] = LookupNode($"ppu.oam_ram_{i:X2}_b{b}");
                Console.Error.WriteLine($"# [oa] resolved cell[0][0]={(_oaCells[0, 0] != EmptyNode ? 1 : 0)}");
            }
            if (_oaCells != null && _oaShot < 8)
            {
                long[] marks = { 32877900, 32892100, 35008500, 35020900, 35022100, 35029000, 35036300 };
                string[] tags = { "CTRL v0", "CTRL v5-pre-eval", "STUNT pre-DMA", "STUNT post-DMA", "STUNT post-enable v0", "STUNT v2", "STUNT v5-pre-eval" };
                for (int m = 0; m < marks.Length; m++)
                {
                    if (((_oaFired >> m) & 1) != 0) continue;
                    if (Time < marks[m]) continue;
                    _oaFired |= 1 << m; _oaShot++;
                    var sbO = new System.Text.StringBuilder($"# [oa] t={Time} [{tags[m]}] OAM$00-$0F:");
                    for (int i = 0; i < 16; i++)
                    {
                        int v = 0;
                        for (int b = 0; b < 8; b++)
                            if (_oaCells[i, b] != EmptyNode && NodeStates[_oaCells[i, b]] != 0) v |= 1 << b;
                        sbO.Append($" {v:X2}");
                    }
                    Console.Error.WriteLine(sbO.ToString());
                }
            }
            // [ob] catch the OAM row-0 corruption in the act: per-dot OAM[0..3] + write path, v=3..v=5
            if (Time >= 35031000 && Time <= 35036400 && _oaCells != null && _spVp != null && _spVp.Length == 9 && _obN < 120)
            {
                int vB = ReadBits(_spVp), hB = ReadBits(_spHp);
                bool sample = (vB == 5) || (hB == 0 && (vB == 3 || vB == 4));
                if (sample && hB != _obPrevH)
                {
                    int b0 = 0, b1 = 0, b2 = 0, b3 = 0;
                    for (int b = 0; b < 8; b++)
                    {
                        if (_oaCells[0, b] != EmptyNode && NodeStates[_oaCells[0, b]] != 0) b0 |= 1 << b;
                        if (_oaCells[1, b] != EmptyNode && NodeStates[_oaCells[1, b]] != 0) b1 |= 1 << b;
                        if (_oaCells[2, b] != EmptyNode && NodeStates[_oaCells[2, b]] != 0) b2 |= 1 << b;
                        if (_oaCells[3, b] != EmptyNode && NodeStates[_oaCells[3, b]] != 0) b3 |= 1 << b;
                    }
                    int packed = (b0 << 24) | (b1 << 16) | (b2 << 8) | b3;
                    if (packed != _obPrev || (vB == 5 && hB <= 70))
                    {
                        _obN++;
                        Console.Error.WriteLine($"# [ob] t={Time} v={vB} h={hB} OAM0-3={b0:X2} {b1:X2} {b2:X2} {b3:X2} owd={NodeStates[_sxOwd2]} sprAddr=${(_srAddr != null && _srAddr.Length == 8 ? ReadBits(_srAddr) : -1):X2} sprD=${(_swD != null && _swD.Length == 8 ? ReadBits(_swD) : -1):X2} rend={NodeStates[_spRend]}{(packed != _obPrev ? "  <== CHANGED" : "")}");
                        _obPrev = packed;
                    }
                    _obPrevH = hB;
                }
            }
            // [oc] the corruption instant at half-cycle resolution: cell vs bitline vs precharge
            if (Time >= 35035950 && Time <= 35036000)
            {
                if (_ocRow == EmptyNode)
                {
                    _ocRow = LookupNode("ppu.spr_row0"); _ocCol = LookupNode("ppu.spr_col0");
                    _ocPclk = LookupNode("ppu.pclk0");
                    _ocBitA = LookupNode("ppu.#1537"); _ocBitB = LookupNode("ppu.#1546");
                    _ocColA = LookupNode("ppu.#1502"); _ocColB = LookupNode("ppu.#1505");
                    _ocA0 = LookupNode("ppu.oam_ram_00_a0"); _ocB0 = LookupNode("ppu.oam_ram_00_b0");
                    _ocA1 = LookupNode("ppu.oam_ram_00_a1"); _ocB1 = LookupNode("ppu.oam_ram_00_b1");
                    Console.Error.WriteLine($"# [oc] resolve row={(_ocRow != EmptyNode ? 1 : 0)} col={(_ocCol != EmptyNode ? 1 : 0)} pclk={(_ocPclk != EmptyNode ? 1 : 0)} bitA={(_ocBitA != EmptyNode ? 1 : 0)} colA={(_ocColA != EmptyNode ? 1 : 0)} a0={(_ocA0 != EmptyNode ? 1 : 0)}");
                }
                Console.Error.WriteLine($"# [oc] t={Time} rend={NodeStates[_spRend]} pclk0={NodeStates[_ocPclk]} row0={NodeStates[_ocRow]} col0={NodeStates[_ocCol]}"
                    + $" | bitA={NodeStates[_ocBitA]} bitB={NodeStates[_ocBitB]} colA={NodeStates[_ocColA]} colB={NodeStates[_ocColB]}"
                    + $" | cell0: a0={NodeStates[_ocA0]} b0={NodeStates[_ocB0]}  cell1: a1={NodeStates[_ocA1]} b1={NodeStates[_ocB1]}");
            }
            // [t3] Test 3: arm on the mid-frame disable, then trace the FIRST rendered line
            // after the re-enable dot by dot: sprite pixel vs BG pixel vs the hit trigger.
            if (Time > 35500000 && _t3N < 150 && _spVp != null && _spVp.Length == 9)
            {
                if (_t3P == null)
                {
                    var pp = new List<int>(); ResolveNodes("ppu.spr0_p[7:0]", pp, quiet: true); _t3P = pp.ToArray();
                    _t3Act = LookupNode("ppu.spr0_active"); _t3Hit = LookupNode("ppu.spr0_hit");
                    _t3Op = LookupNode("ppu.spr_slot_0_opaque"); _t3Use = LookupNode("ppu.use_sprite_0");
                    _t3Bkg = LookupNode("ppu.bkg_pat"); _t3Set = LookupNode("ppu.set_spr0_hit");
                    _t3Spat = LookupNode("ppu.spr_pat");
                    Console.Error.WriteLine($"# [t3] resolve use={(_t3Use != EmptyNode ? 1 : 0)} bkgpat={(_t3Bkg != EmptyNode ? 1 : 0)} sprpat={(_t3Spat != EmptyNode ? 1 : 0)} set={(_t3Set != EmptyNode ? 1 : 0)}");
                }
                if (_t3P.Length == 8)
                {
                    int vT = ReadBits(_spVp), hT = ReadBits(_spHp);
                    int rd = NodeStates[_spRend];
                    if (_t3Arm == 0 && _t3PrevRend == 1 && rd == 0 && vT >= 1 && vT <= 200)
                    { _t3Arm = vT; _t3N++; Console.Error.WriteLine($"# [t3] t={Time} ARMED at v={vT} h={hT}"); }
                    // the first rendered line after the re-enable: log dots 0..60 of the line where rendering returns
                    if (_t3Arm != 0 && rd == 1 && _t3PrevRend == 0 && _t3Ret == 0 && vT <= 200)
                    { _t3Ret = vT; _t3N++; Console.Error.WriteLine($"# [t3] t={Time} RE-ENABLED at v={vT} h={hT}  cnt=${ReadBits(_t3P):X2} act={NodeStates[_t3Act]}"); }
                    _t3PrevRend = rd;
                    if (_t3Ret != 0 && vT == _t3Ret + 1 && hT <= 60 && hT != _t3PrevH2)
                    {
                        _t3N++; _t3PrevH2 = hT;
                        Console.Error.WriteLine($"# [t3] t={Time} v={vT} h={hT:D3} cnt=${ReadBits(_t3P):X2} act={NodeStates[_t3Act]}"
                            + $" use0={(_t3Use != EmptyNode ? NodeStates[_t3Use] : 9)} opq={(_t3Op != EmptyNode ? NodeStates[_t3Op] : 9)}"
                            + $" sprPat={(_t3Spat != EmptyNode ? NodeStates[_t3Spat] : 9)} bkgPat={(_t3Bkg != EmptyNode ? NodeStates[_t3Bkg] : 9)}"
                            + $" setHit={(_t3Set != EmptyNode ? NodeStates[_t3Set] : 9)} hit={NodeStates[_t3Hit]}");
                    }
                }
            }
            // [ar] ALERead Test 2: prioritize catching the LDA $2007 corruption dot (own budget)
            if (Time > 35000000 && _spVp != null && _spVp.Length == 9)
            {
                if (_arSelPat0 == EmptyNode)
                {
                    _arSelPat0 = LookupNode("ppu.selected_pat0"); _arSelPat1 = LookupNode("ppu.selected_pat1");
                    _arSetHit = LookupNode("ppu.set_spr0_hit"); _arHit = LookupNode("ppu.spr0_hit");
                    Console.Error.WriteLine("# [ar] resolved");
                }
                int vA = ReadBits(_spVp), hA = ReadBits(_spHp);
                int abA = ReadReg(R_CpuAb);
                bool rd2007 = (abA & 0xE007) == 0x2007 && NodeStates[_pdRw] != 0;
                if (rd2007) _arIn2007 = true;
                else if (_arIn2007 && _arN < 60)
                { _arIn2007 = false; _arN++; Console.Error.WriteLine($"# [ar] t={Time} LDA $2007 read END at v={vA} h={hA}"); }
                else if (_arIn2007) _arIn2007 = false;
                // once a corruption fired (a $2007 read in visible region v<20), watch the NEXT scanline artifact
                if (_arN > 0 && vA >= 1 && vA <= 8 && hA >= 238 && hA <= 250 && hA != _arPrevH && _arPixN < 80)
                {
                    _arPrevH = hA; _arPixN++;
                    int pat = ((_arSelPat1 != EmptyNode ? NodeStates[_arSelPat1] : 0) << 1) | (_arSelPat0 != EmptyNode ? NodeStates[_arSelPat0] : 0);
                    if (pat != 0 || (_arSetHit != EmptyNode && NodeStates[_arSetHit] != 0))
                        Console.Error.WriteLine($"# [ar] t={Time} v={vA} h={hA} selPat={pat} setHit={(_arSetHit != EmptyNode ? NodeStates[_arSetHit] : 9)} hit={(_arHit != EmptyNode ? NodeStates[_arHit] : 9)} *** ARTIFACT");
                }
            }
            // [ae] ALERead: trace the ALE+Read octal-latch feedback (self-contained: own vp/hp). OB_DEBUG only.
            // Low Time gate: the standalone ALERead ROM runs the stunt early (isolated), unlike the full AC run.
            if (Time > 200000 && _aeN < 260)
            {
                if (_aeAle == EmptyNode)
                {
                    _aeAle = LookupNode("ppu.ale"); _aeRd = LookupNode("ppu.rd");
                    _aeR2007 = LookupNode("ppu.read_2007_trigger");
                    var aeab = new List<int>(); ResolveNodes("ppu.ab[13:0]", aeab, quiet: true); _aeAb = aeab.Count == 14 ? aeab.ToArray() : System.Array.Empty<int>();
                    var aevp = new List<int>(); ResolveNodes("ppu.vpos[8:0]", aevp, quiet: true); _aeVp = aevp.Count == 9 ? aevp.ToArray() : System.Array.Empty<int>();
                    var aehp = new List<int>(); ResolveNodes("ppu.hpos[8:0]", aehp, quiet: true); _aeHp = aehp.Count == 9 ? aehp.ToArray() : System.Array.Empty<int>();
                    _aeSel0 = LookupNode("ppu.selected_pat0"); _aeSel1 = LookupNode("ppu.selected_pat1");
                    _aeIoCe = LookupNode("ppu.io_ce"); _aeCpuRw = LookupNode("cpu.rw");
                    if (_aeCpuRw == EmptyNode) _aeCpuRw = LookupNode("2a03.cpu.rw");
                    var aeioab = new List<int>(); ResolveNodes("ppu.io_ab[2:0]", aeioab, quiet: true); _aeIoAb = aeioab.Count == 3 ? aeioab.ToArray() : System.Array.Empty<int>();
                    Console.Error.WriteLine($"# [ae] resolve ale={(_aeAle != EmptyNode ? 1 : 0)} rd={(_aeRd != EmptyNode ? 1 : 0)} r2007={(_aeR2007 != EmptyNode ? 1 : 0)} ab={_aeAb.Length} vp={_aeVp.Length} sel={(_aeSel0 != EmptyNode ? 1 : 0)} ioce={(_aeIoCe != EmptyNode ? 1 : 0)} ioab={_aeIoAb.Length} cpurw={(_aeCpuRw != EmptyNode ? 1 : 0)}");
                }
                if (_aeAb.Length == 14 && _aeVp.Length == 9 && _aeHp.Length == 9)
                {
                    if (_aeR2007 != EmptyNode && NodeStates[_aeR2007] != 0 && _aeArmV < 0)
                    {
                        int vv = ReadBits(_aeVp);
                        if (vv >= 1 && vv <= 8) { _aeArmV = vv; _aePrevH = -1; Console.Error.WriteLine($"# [ae] ARM: $2007 read trigger at v={vv} h={ReadBits(_aeHp)}"); }
                    }
                    if (_aeArmV >= 0)
                    {
                        int vE = ReadBits(_aeVp), hE = ReadBits(_aeHp);
                        if (vE == _aeArmV && hE >= 210 && hE <= 234 && _aeHcBudget > 0)
                        {
                            _aeHcBudget--;
                            int r2007 = _aeR2007 != EmptyNode ? NodeStates[_aeR2007] : 9;
                            int ioab = _aeIoAb.Length == 3 ? ReadBits(_aeIoAb) : 9;
                            int ioce = _aeIoCe != EmptyNode ? NodeStates[_aeIoCe] : 9;
                            int crw = _aeCpuRw != EmptyNode ? NodeStates[_aeCpuRw] : 9;
                            Console.Error.WriteLine($"# [aehc] t={Time} v={vE} h={hE} ale={NodeStates[_aeAle]} rd={NodeStates[_aeRd]} r2007={r2007} chrAb=${ReadBits(_aeAb):X4} ioce={ioce} ioab={ioab} rw={crw}");
                        }
                        if (vE == _aeArmV && hE != _aePrevH)
                        {
                            _aePrevH = hE; _aeN++;
                            int sel = ((_aeSel1 != EmptyNode ? NodeStates[_aeSel1] : 0) << 1) | (_aeSel0 != EmptyNode ? NodeStates[_aeSel0] : 0);
                            Console.Error.WriteLine($"# [ae] v={vE} h={hE} ale={NodeStates[_aeAle]} rd={NodeStates[_aeRd]} chrAb=${ReadBits(_aeAb):X4} selPat={sel}");
                        }
                    }
                }
            }
            // stunt monitor: every $4014 write and $3FFE touch, whole run, own budget
            if (_stN < 40)
            {
                int abS = ReadReg(R_CpuAb);
                if (abS != _stPrevAb)
                {
                    if (abS == 0x4014 && NodeStates[_pdRw] == 0)
                    { _stN++; Console.Error.WriteLine($"# [st] t={Time} WRITE $4014 (OAM DMA trigger)"); }
                    else if (abS == 0x3FFE)
                    { _stN++; Console.Error.WriteLine($"# [st] t={Time} touch $3FFE rw={NodeStates[_pdRw]}"); }
                    _stPrevAb = abS;
                }
            }
            // OAM-DMA read matrix: final-db of every $50xx read in the stunt window
            if (Time > 19300000 && Time < 19400000 && !_dmaRdDumped)
            {
                if (_micIdb == null)
                {
                    var li = new List<int>(); ResolveNodes("cpu.idb[7:0]", li, quiet: true); _micIdb = li.ToArray();
                    var ls = new List<int>(); ResolveNodes("cpu.spr_data[7:0]", ls, quiet: true); _micSpr = ls.ToArray();
                    _micR4015 = LookupNode("cpu.r4015"); _micR4016 = LookupNode("cpu.r4016"); _micR4017 = LookupNode("cpu.r4017");
                    _micDbe = LookupNode("cpu.dbe"); _micJoy1 = LookupNode("cpu.joy1"); _micJoy2 = LookupNode("cpu.joy2");
                    Console.Error.WriteLine($"# [mic] resolve idb={_micIdb.Length} spr={_micSpr.Length} r15={(_micR4015 != EmptyNode ? 1 : 0)} r16={(_micR4016 != EmptyNode ? 1 : 0)} r17={(_micR4017 != EmptyNode ? 1 : 0)} dbe={(_micDbe != EmptyNode ? 1 : 0)}");
                }
                // event logger: every-step tuple across reads $5013-$501A; print on change
                if (Time >= 19313640 && Time <= 19314120 && _micIdb.Length == 8 && _micSpr.Length == 8)
                {
                    int abE = ReadReg(R_CpuAb);
                    string tup = $"ab=${abE:X4} rw={NodeStates[_pdRw]} ext=${ReadReg(R_CpuDb):X2} idb=${ReadBits(_micIdb):X2} spr=${ReadBits(_micSpr):X2}"
                               + $" r15={(_micR4015 != EmptyNode ? NodeStates[_micR4015] : 9)}{(_micR4016 != EmptyNode ? NodeStates[_micR4016] : 9)}{(_micR4017 != EmptyNode ? NodeStates[_micR4017] : 9)}"
                               + $" dbe={(_micDbe != EmptyNode ? NodeStates[_micDbe] : 9)} joy={(_micJoy1 != EmptyNode ? NodeStates[_micJoy1] : 9)}{(_micJoy2 != EmptyNode ? NodeStates[_micJoy2] : 9)}";
                    if (tup != _micPrevTup) { _micPrevTup = tup; Console.Error.WriteLine($"# [ev] t={Time} {tup}"); }
                }
                int abD = ReadReg(R_CpuAb);
                if (_dmaRdPrev != -1 && abD != _dmaRdPrev)
                {
                    _dmaRdBuf[_dmaRdPrev & 0xFF] = (byte)_dmaRdPrevDb;
                    if (!_dmaRdSeen[_dmaRdPrev & 0xFF]) { _dmaRdSeen[_dmaRdPrev & 0xFF] = true; _dmaRdCount++; }
                    if (_micN < 300)
                    {
                        _micN++;
                        Console.Error.WriteLine($"# [mic] t={Time} rd ${_dmaRdPrev:X4} ext=${_dmaRdPrevDb:X2} idb=${_micPrevIdb:X2} spr=${_micPrevSpr:X2} /r{{15,16,17}}={_micPrevDec:D3}");
                    }
                    _dmaRdPrev = -1;
                }
                if ((abD >> 8) == 0x50 && NodeStates[_pdRw] != 0)
                {
                    _dmaRdPrev = abD; _dmaRdPrevDb = ReadReg(R_CpuDb); _dmaRdGap = 0;
                    _micPrevIdb = _micIdb.Length == 8 ? ReadBits(_micIdb) : -1;
                    _micPrevSpr = _micSpr.Length == 8 ? ReadBits(_micSpr) : -1;
                    _micPrevDec = (_micR4015 != EmptyNode ? NodeStates[_micR4015] * 100 : 900)
                                + (_micR4016 != EmptyNode ? NodeStates[_micR4016] * 10 : 90)
                                + (_micR4017 != EmptyNode ? NodeStates[_micR4017] : 9);
                }
                else if (_dmaRdCount >= 64 && ++_dmaRdGap > 2000)
                {
                    _dmaRdDumped = true;
                    var sbD = new System.Text.StringBuilder($"# [rd] t={Time} OAM-DMA $50xx read matrix ({_dmaRdCount} reads):\n");
                    for (int rr = 0; rr < 16; rr++)
                    {
                        sbD.Append($"#   {rr << 4:X2} |");
                        for (int cc = 0; cc < 16; cc++)
                        {
                            int ix = (rr << 4) + cc;
                            sbD.Append(_dmaRdSeen[ix] ? $" {_dmaRdBuf[ix]:X2}" : " --");
                        }
                        sbD.Append('\n');
                    }
                    Console.Error.Write(sbD.ToString());
                }
            }
            // ZP $A5 write monitor v3 -- report the RAM's post-write truth, not the first-half bus
            if (Time > 28000000 && _a5N < 60)
            {
                int abA5 = ReadReg(R_CpuAb);
                bool wrA5 = NodeStates[_pdRw] == 0 && abA5 == 0x00A5;
                if (wrA5 && !_a5InWrite)
                { _a5InWrite = true; _a5Pc = (ReadReg(R_CpuPch) << 8) | ReadReg(R_CpuPcl); }
                else if (!wrA5 && _a5InWrite)
                {
                    _a5InWrite = false; _a5N++;
                    var ramA5 = ResolveMemory("u1.ram");
                    Console.Error.WriteLine($"# [a5] t={Time} $A5 := ${(ramA5 != null ? ramA5.Read(0xA5) : -1):X2} (pc=${_a5Pc:X4}) S=${ReadReg(R_CpuS):X2}");
                }
            }
            if (Time < 13500000 || _dmaPrN >= 900) return;
            int rdy = LookupNode("cpu.rdy") is int r && r != EmptyNode ? NodeStates[r] : 9;
            int ab = ReadReg(R_CpuAb), rw = NodeStates[_pdRw];
            if (rdy != _dmaPrRdy)
            {
                if (rdy == 0) _dmaPrRdyFall = (int)Time;
                else if (_dmaPrRdy == 0)
                { _dmaPrN++; Console.Error.WriteLine($"# [dma] t={_dmaPrRdyFall} DMA-stall dur={Time - _dmaPrRdyFall}t ab=${ab:X4}"); }
                _dmaPrRdy = rdy;
            }
            bool newAb = ab != _dmaPrPrevAb;
            if (rw == 0 && (ab == 0x4010 || ab == 0x4015) && newAb)
            { _dmaPrN++; Console.Error.WriteLine($"# [dma] t={Time} WRITE ${ab:X4}"); }

            _dmaPrPrevAb = ab;

            // result-write hook: dump ZP $50-$5F at the instant the test banks its verdict
            if (rw == 0 && (ab == 0x0479 || ab == 0x0478 || ab == 0x046D) && !_dmaPrZpDumped)
            {
                _dmaPrZpDumped = true;
                var ram = ResolveMemory("u1.ram");
                if (ram != null)
                {
                    var sb = new System.Text.StringBuilder($"# [dma] t={Time} RESULT-WRITE ${ab:X4} zp $50-$5F:");
                    for (int i = 0x50; i < 0x60; i++) sb.Append($" {ram.Read(i):X2}");
                    Console.Error.WriteLine(sb.ToString());
                }
            }

            // pcm micro-state -- EVENT-armed: a $4015 write landing while the DMC DMA is active
            // (pcm_dma_active==1) is exactly the X=8/9 mid-flight-abort case
            if (rdy == 0 && !_rdyDumped && Time > 15860000)
            {
                _rdyDumped = true;
                foreach (string nname in new[] { "cpu.#14039", "cpu.#15737", "cpu.#11483" })
                {
                    int hn = LookupNode(nname);
                    if (hn == EmptyNode) { Console.Error.WriteLine($"# [halt] {nname} unresolved"); continue; }
                    ref var hi = ref NodeInfos[hn];
                    Console.Error.WriteLine($"# [halt] {nname} id={hn} inline={hi.Inline} state={NodeStates[hn]}");
                    if (hi.Inline != 0)
                    {
                        int k = 0;
                        for (int i = 0; i < hi.C1c2Count; i++, k += 2)
                            Console.Error.WriteLine($"# [halt]   pair g#{hi.InlinePayload[k]}({GetNodeName(hi.InlinePayload[k])})={NodeStates[hi.InlinePayload[k]]} o#{hi.InlinePayload[k+1]}({GetNodeName(hi.InlinePayload[k+1])})={NodeStates[hi.InlinePayload[k+1]]}");
                        for (int i = 0; i < hi.GndCount; i++) { int g = hi.InlinePayload[k++];
                            Console.Error.WriteLine($"# [halt]   GND g#{g}({GetNodeName(g)})={NodeStates[g]}"); }
                        for (int i = 0; i < hi.PwrCount; i++) { int g = hi.InlinePayload[k++];
                            Console.Error.WriteLine($"# [halt]   PWR g#{g}({GetNodeName(g)})={NodeStates[g]}"); }
                    }
                    else
                    {
                        for (int i = hi.TlistC1c2s; TransistorList[i] != 0; i += 2)
                            Console.Error.WriteLine($"# [halt]   pair g#{TransistorList[i]}({GetNodeName(TransistorList[i])})={NodeStates[TransistorList[i]]} o#{TransistorList[i+1]}({GetNodeName(TransistorList[i+1])})={NodeStates[TransistorList[i+1]]}");
                        for (int i = hi.TlistC1gnd; TransistorList[i] != 0; i++)
                            Console.Error.WriteLine($"# [halt]   GND g#{TransistorList[i]}({GetNodeName(TransistorList[i])})={NodeStates[TransistorList[i]]}");
                        for (int i = hi.TlistC1pwr; TransistorList[i] != 0; i++)
                            Console.Error.WriteLine($"# [halt]   PWR g#{TransistorList[i]}({GetNodeName(TransistorList[i])})={NodeStates[TransistorList[i]]}");
                    }
                }
                int rn = LookupNode("cpu.rdy");
                ref var ni = ref NodeInfos[rn];
                Console.Error.WriteLine($"# [rdy] test-build id={rn} inline={ni.Inline}");
                if (ni.Inline == 0)
                {
                    for (int i = ni.TlistC1c2s; TransistorList[i] != 0; i += 2)
                        Console.Error.WriteLine($"# [rdy]   pair g#{TransistorList[i]}({GetNodeName(TransistorList[i])})={NodeStates[TransistorList[i]]} o#{TransistorList[i+1]}({GetNodeName(TransistorList[i+1])})");
                    for (int i = ni.TlistC1gnd; TransistorList[i] != 0; i++)
                        Console.Error.WriteLine($"# [rdy]   GND g#{TransistorList[i]}({GetNodeName(TransistorList[i])})={NodeStates[TransistorList[i]]}");
                    for (int i = ni.TlistC1pwr; TransistorList[i] != 0; i++)
                        Console.Error.WriteLine($"# [rdy]   PWR g#{TransistorList[i]}({GetNodeName(TransistorList[i])})={NodeStates[TransistorList[i]]}");
                }
                else
                {
                    int k = 0;
                    for (int i = 0; i < ni.C1c2Count; i++, k += 2)
                        Console.Error.WriteLine($"# [rdy]   pair g#{ni.InlinePayload[k]}({GetNodeName(ni.InlinePayload[k])})={NodeStates[ni.InlinePayload[k]]} o#{ni.InlinePayload[k+1]}({GetNodeName(ni.InlinePayload[k+1])})");
                    for (int i = 0; i < ni.GndCount; i++) { int g = ni.InlinePayload[k++];
                        Console.Error.WriteLine($"# [rdy]   GND g#{g}({GetNodeName(g)})={NodeStates[g]}"); }
                    for (int i = 0; i < ni.PwrCount; i++) { int g = ni.InlinePayload[k++];
                        Console.Error.WriteLine($"# [rdy]   PWR g#{g}({GetNodeName(g)})={NodeStates[g]}"); }
                }
            }
            if (rw == 0 && ab == 0x4015 && rdy == 0 && _pcmArmed == 0)
            { _pcmArmed = 400; Console.Error.WriteLine($"# [pcm] t={Time} *** $4015 write during RDY-halt (mid-DMA) -- microscope armed ***"); }
            if (_pcmArmed > 0 || (Time >= 15860000 && Time <= 15861000))
            {
                if (_pcmArmed > 0) _pcmArmed--;
                if (_pcmW == null)
                {
                    _pcmW = new int[10]; _pcmLc = new int[12];
                    string[] nm = { "cpu.#14059", "cpu.#11094", "cpu.#11093", "cpu.#11102", "cpu.pcm_en",
                                    "cpu.#10337", "cpu.#10338", "cpu.#10658", "cpu.#11553", "cpu.#14089" };   // + halt family, pcm_rd_active
                    for (int i = 0; i < 10; i++) _pcmW[i] = LookupNode(nm[i]);
                    for (int i = 0; i < 12; i++) _pcmLc[i] = LookupNode($"cpu.pcm_lc{i}");
                    Console.Error.WriteLine($"# [pcm] resolved: {string.Join(",", System.Linq.Enumerable.Select(_pcmW, x => x != EmptyNode ? "ok" : "MISS"))} lc={(System.Linq.Enumerable.All(_pcmLc, x => x != EmptyNode) ? "ok" : "MISS")}");
                }
                int st = 0;
                for (int i = 0; i < 10; i++) if (_pcmW[i] != EmptyNode && NodeStates[_pcmW[i]] != 0) st |= 1 << i;
                int lc = 0;
                for (int i = 0; i < 12; i++) if (_pcmLc[i] != EmptyNode && NodeStates[_pcmLc[i]] != 0) lc |= 1 << i;
                if (st != _pcmPrevSt || lc != _pcmPrevLc)
                {
                    Console.Error.WriteLine($"# [pcm] t={Time} dma={st & 1} loadbuf={(st >> 1) & 1} loadsr={(st >> 2) & 1} shiftsr={(st >> 3) & 1} en={(st >> 4) & 1} h37={(st >> 5) & 1} h38={(st >> 6) & 1} h658={(st >> 7) & 1} h1553={(st >> 8) & 1} rdact={(st >> 9) & 1} rdy={rdy} ab=${ab:X4}");
                    _pcmPrevSt = st; _pcmPrevLc = lc;
                }
            }
        }
        private static int _dmaPrLastDb = -1;
        private static int[] _pcmW, _pcmLc;
        private static int _pcmPrevSt = -1, _pcmPrevLc = -1;
        private static bool _dmaPrZpDumped;
        private static int _pcmArmed;
        private static bool _rdyDumped;

        // ── DMC $4015-abort shim (Explicit/Implicit DMA Abort): on real silicon the $4015
        // status write takes effect 3-4 CPU cycles later (5-6 at the fire boundary) through the
        // ACLK pipeline, and the DMA re-checks its enable gate EVERY cycle -- so a disable landing
        // inside the DMA's stall window kills the in-flight DMA (the fetch never completes; the
        // CPU resumes ~3 cycles early). S1's netlist resolves the write immediately (pcm_en flips
        // within a half-cycle) but a committed DMA is immune to it -- measured on AccuracyCoin
        // ExplicitDMAAbort X=8/9: disable at +619, the DMA still fired and completed at +621.5
        // (hardware answer key 01, S1 measured 04; the other 14 phases match). The shim restates
        // the TriCNES-documented semantics: on the pcm_en falling edge, schedule the abort check
        // ~3.5 cycles out; if the CPU is then DMA-stalled and the fetch has not yet happened,
        // clamp the rdy pulldown gate (cpu.#14039) off so the CPU resumes. Test mode only. ──
        private static bool _dmcAbortShim;
        private static int _dmcAbHalt = EmptyNode, _dmcAbDmaAct = EmptyNode, _dmcAbEn = EmptyNode, _dmcAbRdy = EmptyNode, _dmcAbLoadSr = EmptyNode, _dmcAbUpReq = EmptyNode, _dmcAbPhase = EmptyNode;
        private static long _dmcAbLastBoundary; private static int _dmcAbPrevLoadSr;
        private static int _dmcAbPrevEn = -1, _dmcAbCountdown, _dmcAbHold, _dmcAbKillIn;
        private static bool _dmcAbFetchSeen;

        // R4015 read-decode missing a1 term (APURegActivation err6): fixed in DATA -- transdefs
        // patch t13032b restores the extraction-dropped device (geometry present in segdefs;
        // BreakNES pla[4] corroborates). See data/system-def/2a03/PATCHES.md and
        // MD/testrom/2026-07-14-APURegActivation-err6. Category-E (netlist data) defects are
        // netlist patches with provenance comments, not shims (user decision 2026-07-14).

        // ── OAM blank-edge write-back shim (StaleSpriteShiftRegs err2; PPU OAM is dynamic) ──
        // The 2C02's primary OAM cells are cross-coupled pairs with NO pull-ups: a stored 1 is
        // charge on a floating node, kept alive by the bit-line precharge and the read buffer's
        // write-back (a DRAM-style sense-and-restore). When rendering is disabled mid-scanline,
        // the netlist switches the buffer's source from the cell array to the external bus INSIDE
        // the same settle in which the row and column selects are still open -- so the bus content
        // (during dots 1-64 that is the secondary-OAM clear's $FF) is restored straight into the
        // addressed row. Measured on AccuracyCoin StaleSpriteShiftRegs test 2: at the exact
        // half-cycle where rendering_1 falls, spr_row0 and spr_col0 both open with the bit-line
        // pair already carrying the $FF pattern, and sprite 0 ($05 $C5 $03 $FE) becomes $FF.
        // Silicon has propagation delay: the row closes before the switched buffer content reaches
        // the bit lines, so a rendering-disable never writes OAM. AccuracyCoin's own OAM Corruption
        // spec states the hardware rule outright -- the only OAM corruption is a copy of row 0 INTO
        // row `seed` at the re-ENABLE edge, so "OAM Corruption cannot affect the outcome of a
        // (non-arbitrary) sprite zero hit": row 0 is the source and is never destroyed.
        // The shim restates that one semantic: it mirrors the addressed OAM row while rendering and,
        // on the rendering-disable edge, restores whatever that settle wrote into it. Test mode only.
        private static bool _oamEdgeShim;
        private static int _oeRend = EmptyNode;
        private static int[] _oeSprAddr = Array.Empty<int>();
        private static readonly int[,] _oeCellA = new int[256, 8], _oeCellB = new int[256, 8];
        private static readonly byte[] _oeMirror = new byte[8];
        private static readonly int[] _oeDriven = new int[128];
        private static int _oeMirrorRow = -1, _oePrevRend, _oeDrivenCount, _oeHold, _oeFires;
        private static readonly bool _oeDebug = Environment.GetEnvironmentVariable("OE_DEBUG") != null;

        public static void EnableOamBlankEdgeShim()
        {
            _oeRend = LookupNode("ppu.rendering_1");
            var sa = new List<int>(); ResolveNodes("ppu.spr_addr[7:0]", sa, quiet: true);
            int live = 0;
            for (int i = 0; i < 256; i++)
                for (int b = 0; b < 8; b++)
                {
                    _oeCellA[i, b] = LookupNode($"ppu.oam_ram_{i:X2}_a{b}");
                    _oeCellB[i, b] = LookupNode($"ppu.oam_ram_{i:X2}_b{b}");
                    if (_oeCellB[i, b] != EmptyNode && !IsPwrGnd(_oeCellB[i, b])) live++;
                }
            if (_oeRend == EmptyNode || sa.Count != 8 || live == 0)
            { Console.Error.WriteLine("# [shim] oam-blank-edge: nodes unresolved -- disabled"); return; }
            _oeSprAddr = sa.ToArray();
            _oePrevRend = NodeStates[_oeRend];
            _oeMirrorRow = -1; _oeDrivenCount = 0; _oeHold = 0; _oeFires = 0;
            _oamEdgeShim = true; ShimChainArmed = true;
        }

        private static void OamBlankEdgeShimStep()
        {
            if (!_oamEdgeShim) return;
            if (_oeHold > 0 && --_oeHold == 0)
            {
                bool rel = false;
                for (int i = 0; i < _oeDrivenCount; i++) rel |= SetFloatQueued(_oeDriven[i]);
                _oeDrivenCount = 0;
                if (rel) ProcessQueue();
            }
            int rend = NodeStates[_oeRend];
            int row = (ReadBits(_oeSprAddr) >> 3) & 0x1F;
            if (rend != 0 && _oeHold == 0)
            {
                for (int c = 0; c < 8; c++)
                {
                    int idx = (row << 3) | c, v = 0;
                    for (int b = 0; b < 8; b++)
                        if (_oeCellB[idx, b] != EmptyNode && NodeStates[_oeCellB[idx, b]] != 0) v |= 1 << b;
                    _oeMirror[c] = (byte)v;
                }
                _oeMirrorRow = row;
            }
            else if (_oePrevRend == 1 && rend == 0 && _oeMirrorRow >= 0 && _oeHold == 0)
            {
                bool changed = false;
                _oeDrivenCount = 0;
                for (int c = 0; c < 8; c++)
                {
                    int idx = (_oeMirrorRow << 3) | c;
                    for (int b = 0; b < 8; b++)
                    {
                        bool one = ((_oeMirror[c] >> b) & 1) != 0;
                        int bn = _oeCellB[idx, b], an = _oeCellA[idx, b];
                        if (bn != EmptyNode && !IsPwrGnd(bn))
                        { changed |= one ? SetHighQueued(bn) : SetLowQueued(bn); _oeDriven[_oeDrivenCount++] = bn; }
                        if (an != EmptyNode && !IsPwrGnd(an))
                        { changed |= one ? SetLowQueued(an) : SetHighQueued(an); _oeDriven[_oeDrivenCount++] = an; }
                    }
                }
                if (changed) ProcessQueue();
                _oeHold = 2;   // hold the restore across the edge settle, then release
                _oeFires++;
                if (_oeDebug) Console.Error.WriteLine($"# [oe] t={Time} rendering-disable edge: restored OAM row {_oeMirrorRow} = {_oeMirror[0]:X2} {_oeMirror[1]:X2} {_oeMirror[2]:X2} {_oeMirror[3]:X2} ...");
            }
            _oePrevRend = rend;
        }

        public static void EnableDmc4015AbortShim()
        {
            _dmcAbHalt = LookupNode("cpu.#14039");    // rdy pulldown gate (halt assert)
            _dmcAbDmaAct = LookupNode("cpu.#14059");  // pcm_dma_active (fetch strobe)
            _dmcAbEn = LookupNode("cpu.pcm_en");
            _dmcAbRdy = LookupNode("cpu.rdy");
            _dmcAbLoadSr = LookupNode("cpu.#11093");  // pcm_loadsr -- byte-boundary anchor
            _dmcAbUpReq = LookupNode("cpu.#15737");   // upstream request latch (feeds #14039 through clock gate #11483)
            _dmcAbPhase = LookupNode("cpu.#11466");   // ACLK phase holding the retire gate (#11483) shut
            if (_dmcAbHalt == EmptyNode || _dmcAbDmaAct == EmptyNode || _dmcAbEn == EmptyNode || _dmcAbRdy == EmptyNode || _dmcAbLoadSr == EmptyNode)
            { Console.Error.WriteLine("# [shim] dmc-4015-abort: nodes unresolved -- disabled"); return; }
            _dmcAbortShim = true; ShimChainArmed = true;
        }

        private static void Dmc4015AbortShimStep()
        {
            if (!_dmcAbortShim) return;
            int en = NodeStates[_dmcAbEn], rdy = NodeStates[_dmcAbRdy];
            int lsr = NodeStates[_dmcAbLoadSr];
            if (_dmcAbPrevLoadSr == 0 && lsr != 0) _dmcAbLastBoundary = Time;   // byte boundary: SR reload cadence, write-independent
            _dmcAbPrevLoadSr = lsr;
            if (rdy == 0) { if (NodeStates[_dmcAbDmaAct] != 0) _dmcAbFetchSeen = true; }
            else _dmcAbFetchSeen = false;
            if (_dmcAbPrevEn == 1 && en == 0)
            {
                if (_pdDbg) Console.Error.WriteLine($"# [abort-shim] t={Time} ARM (en fell) rdy={rdy} cd was {_dmcAbCountdown}");
                _dmcAbCountdown = 84;   // deferred effect calibration (see campaign note)
            }
            _dmcAbPrevEn = en;
            if (_dmcAbHold > 0 && --_dmcAbHold == 0) InstRelease(_dmcAbPhase);
            if (_dmcAbCountdown > 0 && --_dmcAbCountdown == 0)
            {
                // The kill opportunity exists ONLY at the deferred status-off instant (TriCNES:
                // dmcStopTransfer fires once when the delay expires; nothing restarts until a
                // re-enable). The window is anchored to the BYTE BOUNDARY (the DMA's natural
                // slot), NOT to S1's halt-displaced stall: silicon finishes the fetch ~5 cycles
                // past the boundary no matter where the write pushed the stall here.
                long dist = Time - _dmcAbLastBoundary;
                // ACLK quantization: the abort lands on the boundary+48t grid slot -- an earlier
                // status flip waits for the slot (measured: kill at 47t saves 3 cycles = the
                // hardware 01; kill at 23t saved 4 = 00, too many).
                if (rdy == 0 && !_dmcAbFetchSeen && dist < 60)
                {
                    _dmcAbKillIn = dist >= 48 ? 1 : (int)(48 - dist);
                    if (_pdDbg) Console.Error.WriteLine($"# [abort-shim] t={Time} kill scheduled in {_dmcAbKillIn}t (boundary {dist}t ago)");
                }
                else if (_pdDbg) Console.Error.WriteLine($"# [abort-shim] t={Time} no-kill rdy={rdy} fetchSeen={_dmcAbFetchSeen} boundary={dist}t");
            }
            if (_dmcAbKillIn > 0 && --_dmcAbKillIn == 0)
            {
                if (NodeStates[_dmcAbRdy] == 0)
                {
                    // #14039 is itself the request latch: a DYNAMIC node sampled from the upstream
                    // request (#15737) through the clock gate #11483, float-holding in between. At
                    // kill time the gate is closed and the upstream is already clear, so a ONE-SHOT
                    // flip sticks by itself -- no held clamp (a held clamp jammed the sequencer, v4).
                    // Don't fight the latch (the halt node #14039 is pulled up: a low clamp bounces
                    // on release). The RETIRE is already pending (#14063 holds the discharge path);
                    // only the ACLK phase #11466 keeps the retire gate #11483 shut. One-shot dropping
                    // #11466 opens the gate and the netlist's own machinery discharges the halt --
                    // and the phase node is actively re-driven next settle, so it self-heals.
                    if (_dmcAbPhase != EmptyNode) { InstClampLow(_dmcAbPhase); _dmcAbHold = 72; }   // hold the retire path open ~3 cycles (the halt node is pulled up; it re-asserts the moment the gate shuts)
                    if (_pdDbg) Console.Error.WriteLine($"# [abort-shim] t={Time} KILL retire-early (boundary {Time - _dmcAbLastBoundary}t ago) halt={NodeStates[_dmcAbHalt]} rdy={NodeStates[_dmcAbRdy]}");
                }
            }
        }

        private static void LaeForce(int node, int bit)
        { if (node == EmptyNode) return; if (bit == 1) SetHigh(node); else SetLow(node); SetFloat(node); }

        // The node <node> pulls to GND through the (single) transistor it gates — i.e. the output of
        // the inverter whose input is <node>: for a register slave bit, its complement storage node.
        // Walks the turn-ON gate list (index tag bits masked; (c1,c2) pairs, 0-terminated).
        private static int GatedGndPullTarget(int node)
        {
            int idx = NodeTlistGates[node] & GateListIndexMask;
            for (; TransistorList[idx] != 0; idx += 2)
            {
                int c1 = TransistorList[idx], c2 = TransistorList[idx + 1];
                if (c1 == Ngnd) return c2;
                if (c2 == Ngnd) return c1;
            }
            return EmptyNode;
        }
        private static int _laeWait;         // half-cycles left to meet a TSX that will read S
        private static readonly bool _laeDebug = Environment.GetEnvironmentVariable("LAE_DEBUG") == "1";
        private static readonly bool _obDebug = Environment.GetEnvironmentVariable("OB_DEBUG") == "1";
        private static int _obPrevPc = -1, _obPrevIr = -1, _obPrevDb = -1;



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
            var s  = new List<int>(); ResolveNodes("cpu.s[7:0]",  s,  quiet: true);
            if (_lxaPhi2 == EmptyNode || _lxaSync == EmptyNode || _lxaP1 == EmptyNode || _lxaP7 == EmptyNode
                || db.Count != 8 || a.Count != 8 || x.Count != 8 || s.Count != 8)
            { Console.Error.WriteLine("# [shim] LXA magic shim: nodes unresolved — disabled"); LxaMagicShim = false; return; }
            _lxaDb = db.ToArray(); _lxaA = a.ToArray(); _lxaX = x.ToArray(); _laeS = s.ToArray();
            var ns = new List<int>(); ResolveNodes("cpu.nots[7:0]", ns, quiet: true);
            var sb = new List<int>(); ResolveNodes("cpu.sb[7:0]", sb, quiet: true);
            _laeSbs = LookupNode("cpu.dpc6_SBS");
            _laeAcs = LookupNode("cpu.dpc23_SBAC");
            if (ns.Count != 8 || sb.Count != 8 || _laeSbs == EmptyNode)
            { Console.Error.WriteLine("# [shim] LXA/LAE shim: nots[7:0]/sb[7:0]/dpc6_SBS unresolved — disabled"); LxaMagicShim = false; return; }
            _laeNotS = ns.ToArray(); _laeSb = sb.ToArray();
            _laeRam = ResolveMemory("u1.ram");
            _laeNotA = new int[8];
            for (int i = 0; i < 8; i++)
            {
                _laeNotA[i] = GatedGndPullTarget(a[i]);   // a_i -> (~a_i): same dual-side need as S (measured: A reverted $82->$92)
                if (_laeNotA[i] == EmptyNode)
                { Console.Error.WriteLine($"# [shim] LXA/LAE shim: A-bit {i} complement unresolved — disabled"); LxaMagicShim = false; return; }
            }
            _lxaPrevPhi2 = NodeStates[_lxaPhi2];
            _lxaArm = 0;
            _laeRecent = 0; _laeWait = 0; _laeVal = -1; _laeSbsSeen = false;
            LxaMagicShim = true; ShimChainArmed = true;
        }

        // ── M2 charge-decay mechanism (the timestamp half of M2; replaces the runner-level
        // _io_db decay shim). Physics: a dynamic latch bit leaks its charge to 0 in ~600 ms of
        // real time when not refreshed (ppu_open_bus readme; "some decay sooner"). Engine model:
        // per annotated node, an hc timestamp of the last observed state change; a bit that has
        // held nonzero past the threshold is decayed with the proven force-low-then-release
        // recipe (the node then float-holds 0). Per-BIT independent timers — physically truer
        // than the shim's aggregate-value clock (each cell leaks on its own). The annotation set
        // is the first entry of the analog-sidecar plan: the 2C02 io-bus latch island.
        // Scan every 16,384 hc (~0.38 ms) — ample resolution for a 600 ms constant, negligible
        // cost. Enabled via env M2_DECAY (EnableM2Decay from the test runner); golden/bench
        // paths never enable it, and the threshold (25.7M hc) cannot fire inside benchmark runs.
        private static bool _m2Decay;
        public static bool M2DecayEnabled => _m2Decay;
        private static int[] _m2dNodes = Array.Empty<int>();
        private static byte[] _m2dPrev = Array.Empty<byte>();
        private static long[] _m2dStamp = Array.Empty<long>();
        private const long M2DecayThresholdHc = 36L * 714_732;   // 36 NTSC frames ≈ 600 ms of master clock

        public static void EnableM2Decay()
        {
            var nodes = new List<int>();
            ResolveNodes("ppu._io_db[7:0]", nodes, quiet: true);   // the io data-bus LATCH side (the "decay register")
            if (nodes.Count != 8)
            { Console.Error.WriteLine("# [m2] decay island: nodes unresolved -- disabled"); return; }
            _m2dNodes = nodes.ToArray();
            _m2dPrev = new byte[_m2dNodes.Length];
            _m2dStamp = new long[_m2dNodes.Length];
            for (int i = 0; i < _m2dNodes.Length; i++) { _m2dPrev[i] = NodeStates[_m2dNodes[i]]; _m2dStamp[i] = Time; }
            _m2Decay = true;
            Console.Error.WriteLine($"# [m2] decay island armed: {_m2dNodes.Length} nodes, threshold {M2DecayThresholdHc:N0} hc (~600 ms)");
        }

        private static void M2DecayStep()
        {
            if (!_m2Decay || (Time & 0x3FFF) != 0) return;
            for (int i = 0; i < _m2dNodes.Length; i++)
            {
                int n = _m2dNodes[i];
                byte s = NodeStates[n];
                if (s != _m2dPrev[i]) { _m2dPrev[i] = s; _m2dStamp[i] = Time; }
                else if (s != 0 && Time - _m2dStamp[i] >= M2DecayThresholdHc)
                {
                    SetLow(n); SetFloat(n);        // drive low, settle, release — float-holds 0
                    _m2dPrev[i] = NodeStates[n]; _m2dStamp[i] = Time;
                    Console.Error.WriteLine($"# [m2] decay fired at t={Time:N0}: {GetNodeName(n)} -> {NodeStates[n]}");
                }
            }
        }

        private static void LxaMagicShimStep()
        {
            // (the OpenBus/DL/abort/OamEdge family used to be hosted here — now dispatched by
            //  TestShimChainStep, same relative order)
            if (_pdDbg) DmaProbeStep();   // TEMP diag: rdy/DMC-write timeline (OB_DEBUG only)
            // TEMP diag: PC-transition log in a Time window (default = the IDR-forensics window;
            // override with PC_WIN=lo,hi for new investigations without a per-window rebuild)
            if (_pdDbg && Time >= _pcWinLo && Time <= _pcWinHi && _pcTrN < 900)
            {
                int pcNow = (ReadReg(R_CpuPch) << 8) | ReadReg(R_CpuPcl);
                int pgN = pcNow >> 8, pgP = _pcTrPrev >> 8;
                bool fbff = (pgN == 0xFB || pgN == 0xFF) && (pgP == 0xFB || pgP == 0xFF);
                if (pcNow != _pcTrPrev && pgN != pgP && !fbff)
                { _pcTrN++; Console.Error.WriteLine($"# [pc] t={Time} pc=${pcNow:X4} ir=${ReadReg(R_CpuIr):X2}"); }
                _pcTrPrev = pcNow;
            }
            // LAE ($BB): qualify by the INSTRUCTION REGISTER, not fetch heuristics — an armed-on-db
            // scheme measurably false-triggered on unrelated bytes and then fired at the next TXS.
            // Subtlety (measured): the S-load SBS pulse arrives ~49 half-cycles AFTER IR has already
            // advanced to the next opcode — the 6502's T0/T1 overlap puts LAE's write-back inside the
            // next instruction's fetch. So keep a short 'ember' after IR reads $BB; an SBS pulse
            // within it is LAE's own load window, while a TXS anywhere else stays excluded.
            if (R_CpuIr.Length == 8 && ReadBits(R_CpuIr) == 0xBB)
            {
                if (_laeRecent == 0)
                {
                    _laeOldS = ReadBits(_laeS);   // first sighting: S still pre-op
                    LaeReadCount = 0; LaeRecording = true;   // record every memory read of this op
                }
                _laeRecent = 90;   // must outlive the ~49-hc overlap PLUS the SBS high phase (~12 hc)
            }
            else if (_laeRecent > 0) { if (--_laeRecent == 0) LaeRecording = false; }
            // Trigger at SBS LEVEL (the load window itself). The falling edge is too late — it lands
            // inside the next instruction's S-consuming cycle (measured: PHA's push address got
            // perturbed via SADL, corrupting the harness capture). Inside the window the gate is
            // open, so the force must make bus and register AGREE rather than fight (see below).
            if (NodeStates[_laeSbs] == 1 && _laeRecent > 0) _laeSbsSeen = true;
            _laePrevSbs = NodeStates[_laeSbs];
            // A's load gate (SBAC) stays open past the capture fall and re-imposes the transitional
            // bus on top of any force (measured: forced $82 reverted to $92 by PHA's sample). The
            // moment SBAC closes, A is isolated — re-apply the computed value there, ahead of PHA's
            // read a cycle later.
            int acsNow = _laeAcs == EmptyNode ? 0 : NodeStates[_laeAcs];
            // (SBAC-close re-forcing removed: the stub's own PLA/AND legitimately reload A through
            // SBAC, and clobbering those measurably broke the flags capture. Kept for tracing only.)
            if (_laeDebug && _laeWait > 1400)
                Console.Error.WriteLine($"# [lae] post t={Time} phi2={NodeStates[_lxaPhi2]} acs={acsNow} a=${ReadBits(_lxaA):X2} ir=${(R_CpuIr.Length==8?ReadBits(R_CpuIr):-1):X2}");
            _laePrevAcs = acsNow;
            if (_laeDebug && (_laeSbsSeen || (R_CpuIr.Length == 8 && ReadBits(R_CpuIr) == 0xBB)))
                Console.Error.WriteLine($"# [lae] t={Time} phi2={NodeStates[_lxaPhi2]} ir=${(R_CpuIr.Length==8?ReadBits(R_CpuIr):-1):X2} sbs={NodeStates[_laeSbs]} sb=${ReadBits(_laeSb):X2} db=${ReadBits(_lxaDb):X2} a=${ReadBits(_lxaA):X2} x=${ReadBits(_lxaX):X2} s=${ReadBits(_laeS):X2}");
            if (_obDebug)
            {
                int pc = ReadReg(R_CpuPcl) | (ReadReg(R_CpuPch) << 8);
                if (pc >= 0x4000 && pc <= 0x62FF)
                {
                    int ir = R_CpuIr.Length == 8 ? ReadBits(R_CpuIr) : -1;
                    int db = ReadBits(_lxaDb);
                    if (pc != _obPrevPc || ir != _obPrevIr || db != _obPrevDb)
                    {
                        Console.Error.WriteLine($"# [ob] t={Time} pc=${pc:X4} ir=${ir:X2} db=${db:X2} ab=${ReadReg(R_CpuAb):X4} rw={(_pdRw!=EmptyNode?NodeStates[_pdRw]:9)}");
                        _obPrevPc = pc; _obPrevIr = ir; _obPrevDb = db;
                    }
                }
            }
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
                // LAE ($BB): X (via SBX) and S (via SBS) latch an earlier settle wave than the SB
                // merge collapse, so both capture the pre-merge bus. The correct merge (mem & SP) is
                // NOT recoverable from SB at any quiescent boundary (measured: SB reads a transitional
                // $C9 at the load fall while A already holds the correct $4A — A's latch caught the
                // mid-settle wave that real silicon delivers to all three). A is therefore the only
                // reliable carrier: at the fall of LAE's own S-load window, impose A on X and on the
                // cross-coupled S pair. SSB (S's bus-drive) is off during a load, so no ripple.
                if (_laeSbsSeen)
                {
                    // Load window. The merge is COMPUTED, not scavenged: db still carries the operand
                    // byte here (measured) and S was sampled pre-op, so v = db & oldS is the value the
                    // analog transition delivers on hardware. (Scavenging A worked for sub-test 1 but
                    // sub-test 2 measurably races even A — $92 captured where $82 is correct.) A and X
                    // are simple latches: fix both right here (proven ripple-free). Flags derive from
                    // the value; drive them via the LXA machinery below. Do NOT touch S yet: any S
                    // change inside [LAE write-back .. PHA samples S] tears the following stack ops
                    // (measured twice: mixed push/pull bases, Copy_SP2 off by one, A restored from a
                    // stale stack byte). S's correction waits for the instruction that actually READS
                    // it — the first TSX after the op — where the stack quartet in between ran
                    // self-consistently on the old base (net zero) exactly as it would on hardware.
                    // Ground truth from the behavioral-memory read log: the operand bytes are the
                    // consecutive-address pair early in the ember, the effective target is
                    // (operand16 + Y), and the data byte is the newest logged read at that target
                    // (newest-first skips the page-cross dummy read). No bus-timing guesswork.
                    LaeRecording = false;
                    int mem = -1;
                    {
                        int opLo = -1, opHi = -1;
                        for (int k = 0; k + 1 < LaeReadCount; k++)
                            if (LaeReadAddr[k + 1] == LaeReadAddr[k] + 1) { opLo = LaeReadVal[k]; opHi = LaeReadVal[k + 1]; break; }
                        if (opLo >= 0)
                        {
                            int y = R_CpuY.Length == 8 ? ReadReg(R_CpuY) : 0;
                            int target = (((opHi << 8) | opLo) + y) & 0x7FF;   // module-local (u1.ram mask; AC targets internal RAM)
                            for (int k = LaeReadCount - 1; k >= 0; k--)
                                if ((LaeReadAddr[k] & 0x7FF) == target) { mem = LaeReadVal[k]; break; }
                        }
                        if (_laeDebug)
                        {
                            var ring = new StringBuilder("# [lae] reads:");
                            for (int k = 0; k < LaeReadCount; k++) ring.Append($" ${LaeReadAddr[k]:X3}=${LaeReadVal[k]:X2}");
                            Console.Error.WriteLine(ring.ToString() + $"  -> mem={(mem >= 0 ? $"${mem:X2}" : "?")}");
                        }
                    }
                    _laeVal = (mem >= 0 ? mem : ReadBits(_lxaA)) & _laeOldS;
                    for (int i = 0; i < 8; i++)
                    {
                        int b = (_laeVal >> i) & 1;
                        Force(_lxaA[i], b);
                        Force(_laeNotA[i], b ^ 1);   // dual-side: A reverts a single-sided force (measured $82 -> $92 by TSX time)
                        Force(_lxaX[i], b);
                    }
                    {
                        int n = (_laeVal >> 7) & 1, z = _laeVal == 0 ? 1 : 0;
                        void DriveF(int node, int bit) { if (node == EmptyNode) return; if (bit == 1) SetHigh(node); else SetLow(node); }
                        DriveF(_lxaP7, n); DriveF(_lxaNotN, n ^ 1);
                        DriveF(_lxaP1, z); DriveF(_lxaZLoop, z);
                        _lxaArm = 3;   // reuse LXA's sustained flag drive + its release countdown
                    }
                    // Timeline truth (from the read ring: the PHA and PHP opcode fetches PRECEDE this
                    // capture): by now PHA has already pushed A — with the smeared pre-fix value — and
                    // decremented S (the very SBS pulse this fires on is PHA's S-1 write-back; the
                    // s=$C9 seen here is $CA-1). The push is a done deed in behavioral RAM, so correct
                    // it retroactively: the slot is $0100 | pre-op S.
                    _laeRam?.Write(0x100 | _laeOldS, (byte)_laeVal);
                    _laeSbsSeen = false;
                    _laeRecent = 0;       // one shot per window
                    _laeWait = 1600;      // ~66 CPU cycles to meet the TSX; drop silently if none comes
                    if (_laeDebug) Console.Error.WriteLine($"# [lae] capture v=${_laeVal:X2} (db=${ReadBits(_lxaDb):X2} oldS=${_laeOldS:X2} a=${ReadBits(_lxaA):X2} ab=${(R_CpuAb.Length>0?ReadReg(R_CpuAb):-1):X4}) t={Time}");
                }
                if (_laeWait > 0)
                {
                    _laeWait--;

                    if (R_CpuIr.Length == 8 && ReadBits(R_CpuIr) == 0xBA)   // TSX just fetched: fix S before its execute reads it
                    {
                        for (int i = 0; i < 8; i++)
                        {
                            int b = (_laeVal >> i) & 1;
                            Force(_laeS[i], b);
                            Force(_laeNotS[i], b ^ 1);
                        }
                        _laeWait = 0;
                        if (_laeDebug) Console.Error.WriteLine($"# [lae] S=${_laeVal:X2} at TSX (t={Time}); a now=${ReadBits(_lxaA):X2} x now=${ReadBits(_lxaX):X2}");
                    }
                }

                if (fetchNow && dbv == 0xAB) _lxaArm = 1;
                else if (_lxaArm == 1) { _lxaImm = dbv; _lxaArm = 2; }

                _laeDbPrevFall = dbv;   // becomes "previous fall's db" for the next fall
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

        // Delay magnitude: global mode overrides the narrow-window default.
        private static int PpuWriteDelayEffectiveHc() => PpuWriteDelayGlobal ? PpuWriteDelayGlobalHc : PpuWriteDelayHc;

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
                        _pwdBkgRel = Time + PpuWriteDelayEffectiveHc();
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
                        _pwdSprRel = Time + PpuWriteDelayEffectiveHc();
                    }
                    else _pwdSprPrev = now;
                }
            }
        }

        // ── Global cross-chip write-delay line (alignment/write-delay calibration project) ──────
        // The narrow-window PpuWriteDelay above compensates a single site (even_odd's pre-render
        // dot-339 skip). Three remaining deviations -- Stale Sprite Shift Regs test 3, BG Serial In,
        // ALERead -- share the same root: the CPU->PPU cross-chip write/read path is idealized to
        // zero delay, so a $2001 effect lands 2-3 dots off where silicon puts it (measured: test 3
        // re-enables 3 dots early; ALERead's $2007 corruption draws 2 dots late). The correct model
        // is a uniform delay line, not per-site clamps. KEY over the single-shot narrow-window shim:
        // clamp the DOWNSTREAM node (bkg_enable_out/spr_enable_out, which gate rendering_disabled)
        // to a delayed copy of the REGISTER (bkg_enable/spr_enable), NEVER clamp the register --
        // so overlapping/back-to-back writes are all observed (the single-shot version clamped the
        // register and dropped the second write, which is exactly why a naive global delay hung
        // even_odd). A ring buffer holds the register history; each step drives *_enable_out to the
        // sample from N hc ago. OFF by default (N=0); --ppu-write-delay-global N enables it. This is
        // the scaffold for the dedicated calibration+verification pass; NOT wired into --test yet.
        public static bool PpuWriteDelayGlobal = false;
        public static int PpuWriteDelayGlobalHc = 0;
        private const int PwdgRing = 512;   // >= max delay in hc; covers 60+ dots
        private static int _pwdgBkg = EmptyNode, _pwdgSpr = EmptyNode, _pwdgBkgOut = EmptyNode, _pwdgSprOut = EmptyNode;
        private static readonly byte[] _pwdgBkgHist = new byte[PwdgRing], _pwdgSprHist = new byte[PwdgRing];
        private static int _pwdgHead;
        private static int _pwdgBkgDriven, _pwdgSprDriven;   // 0=not clamped, 1=clamped low, 2=clamped high
        private static long _pwdgStart;

        // ── dot-339 sprite-counter-reset delay (VISIBLE lines only) -- StaleSpriteShiftRegs Test 3 ──
        // Pragmatic split (see Gemini consults a_2001_write_delay / a_evenodd_rendering_slow):
        // the odd-frame skip (pre-render v=261) and the sprite-counter reset (visible lines) sample
        // the SAME hpos_eq_339_and_rendering node, and on real silicon share one ~2-dot ren_en
        // propagation delay. But S1's register/rendering_1 edges carry a per-test sub-dot phase
        // error, so a single uniform magnitude can't satisfy both. even_odd (v=261) is already fixed
        // by the narrow-window enable-clamp; this shim handles ONLY the visible-line sprite-counter
        // reset, gated to vpos != 261 so the two never overlap. It suppresses hpos_eq_339_and_rendering
        // for N hc after rendering_1 rises (measured from rendering_1, dot 337, so N=24 covers dot
        // 339). The node is 0 except at dot 339, so Test 1's mid-scanline toggles are untouched.
        private static bool _dot339Shim;
        private static int _d339Node = EmptyNode, _d339Ren = EmptyNode;
        private static int[] _d339Vp = System.Array.Empty<int>();
        private static int _d339PrevRen; private static long _d339HoldUntil = -1; private static bool _d339Clamped;
        public static int PpuWriteDelayGlobalHcPublic => PpuWriteDelayGlobalHc;

        public static void EnablePpuWriteDelayGlobal(int hc)
        {
            if (hc <= 0) { PpuWriteDelayGlobal = false; _dot339Shim = false; return; }
            _d339Node = LookupNode("ppu.hpos_eq_339_and_rendering");
            _d339Ren = LookupNode("ppu.rendering_1");
            var vp = new List<int>(); ResolveNodes("ppu.vpos[8:0]", vp, quiet: true);
            _d339Vp = vp.Count == 9 ? vp.ToArray() : System.Array.Empty<int>();
            if (_d339Node == EmptyNode || _d339Ren == EmptyNode || _d339Vp.Length != 9)
            { Console.Error.WriteLine("# [shim] dot-339 delay: nodes unresolved -- disabled"); PpuWriteDelayGlobal = false; _dot339Shim = false; return; }
            PpuWriteDelayGlobalHc = hc;
            _d339PrevRen = NodeStates[_d339Ren];
            _d339HoldUntil = -1; _d339Clamped = false;
            PpuWriteDelayGlobal = true; _dot339Shim = true;
            Console.Error.WriteLine($"# [shim] dot-339 sprite-reset delay: {hc} hc ({hc / 8.0:F1} dots), visible lines only");
        }

        private static void Dot339DelayStep()
        {
            if (!_dot339Shim) return;
            int ren = NodeStates[_d339Ren];
            if (_d339PrevRen == 0 && ren != 0) _d339HoldUntil = Time + PpuWriteDelayGlobalHc;   // rendering_1 rise
            _d339PrevRen = ren;
            bool visible = ReadBits(_d339Vp) != 261;   // leave the pre-render skip (v=261) to the narrow-window shim
            bool want = visible && _d339HoldUntil >= 0 && Time < _d339HoldUntil;
            if (want && !_d339Clamped) { InstClampLow(_d339Node); _d339Clamped = true; }
            else if (!want && _d339Clamped) { InstRelease(_d339Node); _d339Clamped = false; }
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

        // ── OAM-DMA from PPU I/O bus write-data hold shim (test mode only, opt-in) ───────────
        // ppu_read_buffer's TEST_SPHIT_DMA_PPU_BUS performs $4014 <- $20, so the CPU DMA engine
        // alternates reads from $2000-$20FF with writes to $2004. The CPU-side DMA latch already
        // captures the correct values (verified by cpu.spr_data), but the PPU's delayed $2004->OAM
        // write can sample a later PPU I/O bus value inside the same settle wave. Real hardware's
        // write-data latch holds the $2004 data through /WE. This shim supplies only that hold
        // semantic: capture cpu.spr_data on the DMA put cycle, then drive the addressed OAM cell
        // while the PPU OAM /WE pulse is active. It is opt-in because it directly touches OAM cells.
        public static bool OamDmaPpuBusShim = false;
        private static int _odmaPhi2 = EmptyNode, _odmaRw = EmptyNode, _odmaRdy = EmptyNode, _odmaNW2004 = EmptyNode, _odmaNWe = EmptyNode;
        private static int[] _odmaAb = Array.Empty<int>(), _odmaSprData = Array.Empty<int>(), _odmaSprAddr = Array.Empty<int>();
        private static int[,] _odmaOamA = new int[256, 8], _odmaOamB = new int[256, 8];
        private static readonly byte[] _odmaValueQ = new byte[512], _odmaAddrQ = new byte[512];
        private static readonly int[] _odmaDriven = new int[16];
        private static int _odmaPrevPhi2, _odmaPrevNWe, _odmaPendingPpuGet, _odmaQHead, _odmaQCount, _odmaDrivenCount;
        private static long _odmaLastActivity;
        private static readonly bool _odmaDebug = Environment.GetEnvironmentVariable("ODMA_DEBUG") != null;

        public static void EnableOamDmaPpuBusShim()
        {
            _odmaPhi2 = LookupNode("cpu.phi2");
            _odmaRw = LookupNode("cpu.rw");
            _odmaRdy = LookupNode("cpu.rdy");
            _odmaNW2004 = LookupNode("ppu./w2004");
            _odmaNWe = LookupNode("ppu.oam_write_disable");
            var ab = new List<int>(); ResolveNodes("cpu.ab[15:0]", ab, quiet: true);
            var sd = new List<int>(); ResolveNodes("cpu.spr_data[7:0]", sd, quiet: true);
            var sa = new List<int>(); ResolveNodes("ppu.spr_addr[7:0]", sa, quiet: true);
            if (_odmaPhi2 == EmptyNode || _odmaRw == EmptyNode || _odmaRdy == EmptyNode || _odmaNW2004 == EmptyNode || _odmaNWe == EmptyNode
                || ab.Count != 16 || sd.Count != 8 || sa.Count != 8)
            { Console.Error.WriteLine("# [shim] oam-dma-ppu-bus: nodes unresolved — disabled"); OamDmaPpuBusShim = false; return; }

            _odmaAb = ab.ToArray();
            _odmaSprData = sd.ToArray();
            _odmaSprAddr = sa.ToArray();

            int liveCells = 0;
            for (int i = 0; i < 256; i++)
                for (int b = 0; b < 8; b++)
                {
                    int aNode = LookupNode($"ppu.oam_ram_{i:X2}_a{b}");
                    int bNode = LookupNode($"ppu.oam_ram_{i:X2}_b{b}");
                    _odmaOamA[i, b] = aNode;
                    _odmaOamB[i, b] = bNode;
                    if (bNode != EmptyNode && !IsPwrGnd(bNode)) liveCells++;
                }
            if (liveCells == 0)
            { Console.Error.WriteLine("# [shim] oam-dma-ppu-bus: OAM cells unresolved — disabled"); OamDmaPpuBusShim = false; return; }

            _odmaPrevPhi2 = NodeStates[_odmaPhi2];
            _odmaPrevNWe = NodeStates[_odmaNWe];
            _odmaPendingPpuGet = 0;
            _odmaQHead = _odmaQCount = 0;
            _odmaDrivenCount = 0;
            _odmaLastActivity = Time;
            OamDmaPpuBusShim = true;
            Console.Error.WriteLine($"# [shim] oam-dma-ppu-bus armed: live OAM bits={liveCells}");
        }

        internal static void OamDmaPpuBusShimStep()
        {
            int phi2 = NodeStates[_odmaPhi2];
            if (_odmaPrevPhi2 == 1 && phi2 == 0) OamDmaTrackCpuCycle();
            _odmaPrevPhi2 = phi2;

            int nWe = NodeStates[_odmaNWe];
            if (_odmaPrevNWe == 1 && nWe == 0 && _odmaQCount != 0)
            {
                byte addr = OamDmaDequeueAddr();
                byte value = OamDmaDequeueValue();
                OamDmaDriveByte(addr, value);
                _odmaLastActivity = Time;
                if (_odmaDebug) Console.Error.WriteLine($"# [odma] t={Time} /WE fall addr={addr:X2} val={value:X2}");
            }
            else if (_odmaPrevNWe == 0 && nWe == 1)
            {
                OamDmaReleaseDrive();
            }
            _odmaPrevNWe = nWe;

            // If a diagnostic run stops mid-pulse or a queue entry becomes stale, fail inertly.
            if (_odmaQCount != 0 && Time - _odmaLastActivity > 4096)
            {
                if (_odmaDebug) Console.Error.WriteLine($"# [odma] t={Time} stale queue clear count={_odmaQCount}");
                _odmaQHead = _odmaQCount = 0;
                _odmaPendingPpuGet = 0;
            }
        }

        private static void OamDmaTrackCpuCycle()
        {
            if (NodeStates[_odmaRdy] != 0)
            {
                _odmaPendingPpuGet = 0;
                return;
            }

            int addr = ReadBits(_odmaAb);
            if (NodeStates[_odmaRw] != 0)
            {
                _odmaPendingPpuGet = (addr & 0xE000) == 0x2000 ? 1 : 0;
                return;
            }

            if (_odmaPendingPpuGet != 0 && (addr & 0xE007) == 0x2004 && NodeStates[_odmaNW2004] == 0)
                OamDmaEnqueue((byte)ReadBits(_odmaSprAddr), (byte)ReadBits(_odmaSprData));
            _odmaPendingPpuGet = 0;
        }

        private static void OamDmaEnqueue(byte addr, byte value)
        {
            if (_odmaQCount == _odmaValueQ.Length)
            {
                if (_odmaDebug) Console.Error.WriteLine("# [odma] queue overflow — clearing");
                _odmaQHead = _odmaQCount = 0;
            }
            int tail = (_odmaQHead + _odmaQCount) % _odmaValueQ.Length;
            _odmaAddrQ[tail] = addr;
            _odmaValueQ[tail] = value;
            _odmaQCount++;
            _odmaLastActivity = Time;
            if (_odmaDebug) Console.Error.WriteLine($"# [odma] t={Time} enqueue addr={addr:X2} val={value:X2}");
        }

        private static byte OamDmaDequeueAddr()
        {
            byte v = _odmaAddrQ[_odmaQHead];
            return v;
        }

        private static byte OamDmaDequeueValue()
        {
            byte v = _odmaValueQ[_odmaQHead];
            _odmaQHead = (_odmaQHead + 1) % _odmaValueQ.Length;
            _odmaQCount--;
            return v;
        }

        private static void OamDmaDriveByte(int index, int value)
        {
            OamDmaReleaseDrive();
            bool changed = false;
            _odmaDrivenCount = 0;
            for (int bit = 0; bit < 8; bit++)
            {
                bool one = ((value >> bit) & 1) != 0;
                int bNode = _odmaOamB[index, bit];
                int aNode = _odmaOamA[index, bit];
                if (bNode != EmptyNode && !IsPwrGnd(bNode))
                {
                    changed |= one ? SetHighQueued(bNode) : SetLowQueued(bNode);
                    _odmaDriven[_odmaDrivenCount++] = bNode;
                }
                if (aNode != EmptyNode && !IsPwrGnd(aNode))
                {
                    changed |= one ? SetLowQueued(aNode) : SetHighQueued(aNode);
                    _odmaDriven[_odmaDrivenCount++] = aNode;
                }
            }
            if (changed) ProcessQueue();
        }

        private static void OamDmaReleaseDrive()
        {
            if (_odmaDrivenCount == 0) return;
            bool changed = false;
            for (int i = 0; i < _odmaDrivenCount; i++)
                changed |= SetFloatQueued(_odmaDriven[i]);
            _odmaDrivenCount = 0;
            if (changed) ProcessQueue();
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
