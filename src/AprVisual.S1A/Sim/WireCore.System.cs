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
        internal static NativeNodeList R_CpuA, R_CpuX, R_CpuY, R_CpuP, R_CpuS, R_CpuIr;
        internal static NativeNodeList R_CpuPcl, R_CpuPch, R_CpuAb, R_CpuDb;
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
            ResetS1aRuntimeState();
            // S1A full-engine arming — pre-load half. These flags are consumed DURING the compose passes
            // (ALERead node-split cut in WireCore.Module, raw-id aliases) or at Reset (PowerUpStateShim),
            // so they must be set before the pass loop below. The post-load half (ArmS1aMechanisms) runs at
            // the end of this method. S1A IS the full engine — there is no opt-out.
            // PowerUpStateShim gives S1A the REALISTIC console power-up state (palette residue + CPU P=$34)
            // in every mode — S1A is its own engine, so this need not match the raw S1 power-up.
            AleReadMuxShim = true; RegisterRawIdAliases = true; PowerUpStateShim = true;
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

            // S1A full-engine arming — post-load half. The graph is composed and reset; resolve + arm the
            // M1–M6 mechanisms (every mode — --benchmark included — runs the full engine; there is no gate).
            ArmS1aMechanisms();
        }

        // LoadSystem may be called repeatedly by --test-dir. Native mechanism storage belongs to the
        // previous composed graph and is freed by ResetBuild/ResetHandlers, so clear every post-arm
        // runtime gate before any reset half-cycles of the next graph can execute.
        private static void ResetS1aRuntimeState()
        {
            N_Res = N_PpuInVblank = N_CpuSync = EmptyNode;
            M_EramRam = null;
            R_CpuA = R_CpuX = R_CpuY = R_CpuP = R_CpuS = R_CpuIr = default;
            R_CpuPcl = R_CpuPch = R_CpuAb = R_CpuDb = default;

            _m6xRows = null; _m6xRowCount = 0; _m6xHp = _m6xVp = _m6xHp3 = default;
            _m4Rows = null; _m4RowCount = 0; _m4Edge = false;

            LxaMagicShim = false; M1LxaEnabled = false;
            _lxaPhi2 = _lxaSync = _lxaP1 = _lxaP7 = _lxaZLoop = _lxaNotN = EmptyNode;
            _lxaDb = _lxaA = _lxaX = _laeS = _laeNotS = _laeNotA = default;
            _laeSbs = _laeAcs = EmptyNode;
            _lxaPrevPhi2 = _lxaArm = _lxaImm = _laeRecent = _laePrevSbs = _laePrevAcs = _laeWait = 0;
            _lxaPrevSync = _laeSbsSeen = false;
            _laeVal = _laeOldS = _laeDbPrevFall = -1; _laeRam = null;

            _openBusShim = false; _pdDb = default; _pdRw = EmptyNode; _pdObTop = _pdLastBus = 0;
            _dlShim = false; _dlIdl = _dlNotIdl = default; _dlClk1 = EmptyNode; _dlHeldMask = 0;

            AleReadMuxShim = false;
            _muxIoCe = _muxCpuRw = _muxAle = EmptyNode;
            _muxPpuAb = _muxCpuAb = _muxVp = _muxHp = default;
            _muxState = 0; _muxDetect = 0; _muxReady = _muxIoCeClamped = _muxAleClamped = false;

            _dmcAbortShim = false; M3AbortEnabled = false;
            _dmcAbHalt = _dmcAbDmaAct = _dmcAbEn = _dmcAbRdy = _dmcAbLoadSr = _dmcAbPhase = EmptyNode;
            _dmcAbLastBoundary = 0; _dmcAbPrevLoadSr = 0; _dmcAbPrevEn = -1;
            _dmcAbCountdown = _dmcAbHold = _dmcAbKillIn = 0; _dmcAbFetchSeen = false;

            _oamEdgeShim = false; M4OeEnabled = false; _oeRend = EmptyNode; _oeSprAddr = default;
            _oeCellA = _oeCellB = null; _oeMirror = null; _oeDriven = null;
            _oeMirrorRow = -1; _oePrevRend = _oeDrivenCount = _oeHold = 0;

            _m2Decay = false; _m2dNodes = default; _m2dPrev = null; _m2dStamp = null;

            FrameIrqShim = false; M4FiEnabled = false;
            _fiFlag = _fiNFlag = _fiRdClr = _fiInh = _fiRes = EmptyNode; _fiPrev = 0;

            Dbl2007Shim = false;
            _d27Pal = _d27Rw = _d27Rdy = _d27Phi2 = _d27Nr2007 = EmptyNode;
            _d27Inbuf = _d27Ab = _d27Db = default; _d27Prev = _d27Clamped = _d27Phi2Prev = 0; _d27T0 = 0;

            M4P1Enabled = false; _m4p1Clamp = _m4p1Queue = false;
            OamDmaPpuBusShim = false;
            _odmaPhi2 = _odmaRw = _odmaRdy = _odmaNW2004 = _odmaNWe = EmptyNode;
            _odmaAb = _odmaSprData = _odmaSprAddr = default;
            _odmaOamA = _odmaOamB = null; _odmaValueQ = _odmaAddrQ = null; _odmaDriven = null;
            _odmaPrevPhi2 = _odmaPrevNWe = _odmaPendingPpuGet = _odmaQHead = _odmaQCount = _odmaDrivenCount = 0;
            _odmaLastActivity = 0;
        }

        // The unconditional S1A mechanism/shim arming (order matters: DL follows OpenBus; the ALERead mux
        // resolves after its node-split cut, which already happened in the compose passes above).
        private static void ArmS1aMechanisms()
        {
            EnableM4EdgeLatch();        // DmcLatch data-wins + AluLatch hold
            EnableM6xPhase();           // even_odd + BGSerialIn + dot-339 cross-chip clamp
            EnableM4P1();               // Dbl2007 ClampBus + OamDmaPpuBus QueuedDrive
            EnableOamBlankEdgeMech();   // OAM blank-edge hold
            EnableDmc4015AbortMech();   // deferred-$4015 DMC-DMA abort
            EnableLxaMagicMech();       // LXA $AB magic-merge
            EnableFrameIrqMech();       // frame-IRQ flag-hold
            EnableM2Decay();            // 2C02 io-bus per-bit charge-decay
            EnableOpenBusShim();        // open bus = last transferred byte
            EnableDlShim();             // DL phi2 transparency at $4016/$4017 -- must follow EnableOpenBusShim
            EnableAleReadMux();         // ALERead $2007 phase-mux (the node-split cut was armed pre-load)
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
            R_CpuA   = ResolveNativeQuiet("cpu.a[7:0]");
            R_CpuX   = ResolveNativeQuiet("cpu.x[7:0]");
            R_CpuY   = ResolveNativeQuiet("cpu.y[7:0]");
            R_CpuP   = ResolveNativeQuiet("cpu.p[7:0]");
            R_CpuS   = ResolveNativeQuiet("cpu.s[7:0]");
            R_CpuIr  = ResolveNativeQuiet("cpu.ir[7:0]");
            R_CpuPcl = ResolveNativeQuiet("cpu.pcl[7:0]");
            R_CpuPch = ResolveNativeQuiet("cpu.pch[7:0]");
            R_CpuAb  = ResolveNativeQuiet("cpu.ab[15:0]");
            R_CpuDb  = ResolveNativeQuiet("cpu.db[7:0]");
            M_EramRam = ResolveMemory("cart.eram.ram");
        }

        private static NativeNodeList ResolveNativeQuiet(string expr)
        {
            var l = new List<int>();
            ResolveNodes(expr, l, quiet: true);
            return CopyHandlerNodeList(l);
        }

        // ── S1A power-up state mechanism (see MD/testrom/2026-07-03-fail-analysis §A/§3.2) ──
        // The netlist's artificial power-on (discharge → pull-ups → settle) is not any real console's
        // power-on. Test mode injects the CONVENTIONAL power-up state after the raw settle, using the
        // drive → settle → RELEASE (SetFloat) pattern so every touched cell float-holds the injected
        // value and remains fully writable afterwards:
        //   1. the 2C02 palette cells get the blargg-console table from power_up_palette's own source
        //      (which became the emulator-consensus power-up palette), and
        //   2. the 2A03 Z-flag latch (cpu.p1) is cleared — the netlist settles to P=$36, real consoles
        //      (and cpu_reset/registers' expectation) power on with P=$34; only the Z bit differs.
        // Set unconditionally by LoadSystem — S1A powers up in the realistic console state, every mode.
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

        // ── M6×M3 unified cross-chip phase-arbitration mechanism ─────────────────────────────
        // The three downstream-clamp delay shims (dot-339, even_odd, BGSerialIn) are one physics:
        // a cross-chip control change arrives ~16–24 hc late (M3's delay), and a 2C02 counter-
        // comparator (M6's site) must not act until it does. Instead of three hand-written
        // ShimSteps, one table of (trigger, gate, delay, window) — the first entries of the
        // cpu_ppu_access_delays sidecar (MD/S1a/01). M6's census (m6_interface_census.py) is the
        // source of the gate list; M3's Elmore binner the delays. Two actions cover all three:
        //   ClampGate      — hold a downstream comparator gate LOW for N hc (dot-339, BGSerialIn);
        //   DelayTransition — hold a two-sided enable's transition by clamping its old-value side
        //                     LOW for N hc (even_odd).
        // Triggers: NodeRise (an on-die control signal's own arrival) or RegWrite ($2001 write).
        // The S1A full engine arms the built-in rows unconditionally. Force-LOW only (no GND>VCC
        // wall); each row remains dormant until its trigger and timing window occur.
        private enum M6xTrig { NodeRise, RegWrite }
        private enum M6xWin { VisibleLines, PreRenderNarrow, Hpos8Ge4 }
        private enum M6xAct { ClampGate, DelayTransition }
        private struct M6xRow
        {
            public M6xTrig Trig; public M6xAct Act; public M6xWin Win;
            public int Reg, EnableMask;             // RegWrite: $2001 + db enable bits
            public int TrigNode;                    // NodeRise: the arriving control signal
            public int Gate, GateComp;              // ClampGate: Gate; DelayTransition: Gate + its complement
            public int DelayHc;
            // state
            public byte PrevTrig; public long HoldUntil; public int ClampedNode; public bool InWr; public int WrDb;
        }
        private static M6xRow* _m6xRows;
        private static int _m6xRowCount;
        private static NativeNodeList _m6xHp, _m6xVp, _m6xHp3;

        private static void AddM6xClamp(string name, M6xTrig trig, int trigNode, int reg, int mask, string gateName, int delay, M6xWin win)
        {
            int gate = LookupNode(gateName);
            if (gate == EmptyNode) { Console.Error.WriteLine($"# [m6x] row {name}: gate {gateName} unresolved -- skipped"); return; }
            _m6xRows[_m6xRowCount++] = new M6xRow
            {
                Trig = trig, Act = M6xAct.ClampGate, Win = win,
                Reg = reg, EnableMask = mask, TrigNode = trigNode, Gate = gate, GateComp = EmptyNode,
                DelayHc = delay, PrevTrig = trigNode != EmptyNode ? NodeStates[trigNode] : (byte)0,
                HoldUntil = -1, ClampedNode = EmptyNode,
            };
        }

        public static void EnableM6xPhase()
        {
            var hp = new List<int>(); ResolveNodes("ppu.hpos[8:0]", hp, quiet: true); _m6xHp = hp.Count == 9 ? CopyHandlerNodeList(hp) : default;
            var vp = new List<int>(); ResolveNodes("ppu.vpos[8:0]", vp, quiet: true); _m6xVp = vp.Count == 9 ? CopyHandlerNodeList(vp) : default;
            var h3 = new List<int>(); ResolveNodes("ppu.hpos[2:0]", h3, quiet: true); _m6xHp3 = h3.Count == 3 ? CopyHandlerNodeList(h3) : default;
            _m6xRows = AllocHandlerArray<M6xRow>(4);
            _m6xRowCount = 0;
            // row: dot-339 — rendering_1 rise clamps hpos_eq_339_and_rendering for 24hc on visible lines
            int ren = LookupNode("ppu.rendering_1");
            if (ren != EmptyNode) AddM6xClamp("dot339", M6xTrig.NodeRise, ren, 0, 0, "ppu.hpos_eq_339_and_rendering", 24, M6xWin.VisibleLines);
            // row: BGSerialIn — $2001 enable write clamps the shifter-reload gate for 16hc at hpos%8>=4
            int bgg = LookupNode("ppu.hpos_mod_8_eq_6_or_7_and_rendering");
            if (bgg == EmptyNode) bgg = LookupNode("ppu.hpos_mod_8_eq_6_or_7");
            if (bgg != EmptyNode)
                _m6xRows[_m6xRowCount++] = new M6xRow { Trig = M6xTrig.RegWrite, Act = M6xAct.ClampGate, Win = M6xWin.Hpos8Ge4,
                                                          Reg = 0x2001, EnableMask = 0x18, TrigNode = EmptyNode, Gate = bgg, GateComp = EmptyNode,
                                                          DelayHc = 16, PrevTrig = 0, HoldUntil = -1, ClampedNode = EmptyNode };
            // rows: even_odd — a bkg/spr_enable transition in the vpos261/hpos338-339 pre-render skip
            // window is delayed 16hc by holding the OLD value (clamp the old-value side low). This is
            // the DelayTransition action: it delays a two-sided enable's edge, not a one-sided gate.
            foreach (var (nm, en, comp) in new[] { ("eo_bkg", "ppu.bkg_enable", "ppu./bkg_enable"),
                                                   ("eo_spr", "ppu.spr_enable", "ppu./spr_enable") })
            {
                int g = LookupNode(en), gc = LookupNode(comp);
                if (g != EmptyNode && gc != EmptyNode)
                    _m6xRows[_m6xRowCount++] = new M6xRow { Trig = M6xTrig.NodeRise /*unused for DelayTransition*/, Act = M6xAct.DelayTransition,
                                                              Win = M6xWin.PreRenderNarrow, Reg = 0, EnableMask = 0, TrigNode = g, Gate = g, GateComp = gc,
                                                              DelayHc = 16, PrevTrig = NodeStates[g], HoldUntil = -1, ClampedNode = EmptyNode };
                else Console.Error.WriteLine($"# [m6x] row {nm}: {en}/{comp} unresolved -- skipped");
            }
            Console.Error.WriteLine($"# [m6x] cross-chip phase arbitration armed: {_m6xRowCount} rows (dot339 + bgserial + even_odd)");
        }

        private static bool M6xWindowOk(M6xWin win)
        {
            int v = _m6xVp.Length == 9 ? ReadBits(_m6xVp.Nodes, _m6xVp.Length) : -1;
            switch (win)
            {
                case M6xWin.VisibleLines:    return v != 261;
                case M6xWin.PreRenderNarrow: return v == 261 && _m6xHp.Length == 9 && ReadBits(_m6xHp.Nodes, _m6xHp.Length) >= 338 && ReadBits(_m6xHp.Nodes, _m6xHp.Length) <= 339;
                case M6xWin.Hpos8Ge4:        return _m6xHp3.Length == 3 && ReadBits(_m6xHp3.Nodes, _m6xHp3.Length) >= 4;
            }
            return false;
        }

        private static void M6xPhaseStep()
        {
            for (int r = 0; r < _m6xRowCount; r++)
            {
                ref var row = ref _m6xRows[r];
                // release when the hold expires
                if (row.HoldUntil >= 0 && Time >= row.HoldUntil)
                {
                    if (row.ClampedNode != EmptyNode) InstRelease(row.ClampedNode);
                    row.ClampedNode = EmptyNode; row.HoldUntil = -1;
                }
                // arm on trigger
                if (row.Act == M6xAct.DelayTransition)
                {
                    // even_odd: on a two-sided enable's transition in the window, hold the OLD value
                    // by clamping the old-value side low (old 0 -> clamp Gate; old 1 -> clamp GateComp).
                    byte cur = NodeStates[row.Gate];
                    if (cur != row.PrevTrig && row.HoldUntil < 0 && M6xWindowOk(row.Win))
                    {
                        int hold = row.PrevTrig == 0 ? row.Gate : row.GateComp;
                        InstClampLow(hold); row.ClampedNode = hold; row.HoldUntil = Time + row.DelayHc;
                    }
                    else if (row.HoldUntil < 0) row.PrevTrig = cur;   // track only while not clamped
                }
                else if (row.Trig == M6xTrig.NodeRise)
                {
                    byte cur = NodeStates[row.TrigNode];
                    bool rise = row.PrevTrig == 0 && cur != 0;
                    row.PrevTrig = cur;
                    if (rise && row.HoldUntil < 0 && M6xWindowOk(row.Win))
                    { InstClampLow(row.Gate); row.ClampedNode = row.Gate; row.HoldUntil = Time + row.DelayHc; }
                    // NodeRise/ClampGate also needs to keep the clamp asserted only while the window holds
                    else if (row.HoldUntil >= 0 && !M6xWindowOk(row.Win))
                    { if (row.ClampedNode != EmptyNode) InstRelease(row.ClampedNode); row.ClampedNode = EmptyNode; row.HoldUntil = -1; }
                }
                else   // RegWrite
                {
                    // Most half-cycles are reads. Do not gather the 16-bit CPU address unless
                    // R/W says this can be the $2001 write that this row observes.
                    bool wr = NodeStates[_pdRw] == 0 && (ReadReg(R_CpuAb) & 0xE007) == row.Reg;
                    if (wr) { row.InWr = true; row.WrDb = ReadReg(R_CpuDb); continue; }
                    if (!row.InWr) continue;
                    row.InWr = false;
                    if ((row.WrDb & row.EnableMask) == 0) continue;
                    if (!M6xWindowOk(row.Win)) continue;
                    if (row.HoldUntil < 0) { InstClampLow(row.Gate); row.ClampedNode = row.Gate; }
                    row.HoldUntil = Time + row.DelayHc;
                }
            }
        }

        // ── M4 edge-latch mechanism (the generic edge-capture primitive) ─────────────────────
        // A transparent latch's edge defines the cell's value; zero-delay settling lets same-wave
        // races and mid-settle glitches corrupt it. One primitive expresses three measured verdicts:
        //   DataWins   : at the enable's FALLING edge the cell captures the post-settle DATA value
        //                (DMC pcm_latch — analog clock-decay overlap lets the data through);
        //   Hold       : at the enable's FALLING edge the cell RESTORES its pre-edge snapshot
        //                (ALU input latches — hold time met, the same-wave collapse must not leak);
        //   Transparent: while the enable is HIGH the cell TRACKS the settled DATA, overwriting a
        //                mid-settle capture glitch (DL/idl — the input latch is transparent through
        //                phi2, so the settled bus wins over a group-resolution transient). This is
        //                the glitch-immunity verdict: inertial delay expressed as "the settled value
        //                at the right phase wins over the wrong-phase transient."
        // A row may qualify Transparent with a behavioral address window (AddrLo..AddrHi on the CPU
        // address bus) and a divergence threshold (MinBits) — DL only restates the bus on the
        // measured $4016/$4017 race sites, and only on a >=2-bit capture signature; a single-bit
        // difference is a legitimate controller-shift boundary. That per-site SCOPING is the shim's
        // irreducible content (the latch physics is generic; where/when to apply it is calibration).
        private enum M4Kind { DataWins, Hold, Transparent }
        private struct M4Row
        {
            public M4Kind Kind;
            public int Enable; public NativeNodeList Cells; public NativeNodeList Datas;
            public byte PrevEnable; public fixed byte Snap[8];
            public int AddrLo, AddrHi, MinBits;   // Transparent scoping (0/0/0 = unscoped)
        }
        private static bool _m4Edge;
        public static bool M4EdgeEnabled => _m4Edge;
        private static M4Row* _m4Rows;
        private static int _m4RowCount;

        public static void EnableM4EdgeLatch()
        {
            _m4Rows = AllocHandlerArray<M4Row>(4);
            _m4RowCount = 0;
            {   // row: DMC pcm_latch — data-wins (measured: blargg 7-dmc_basics #19 reads $80)
                int en = LookupNode("cpu.apu_clk1"), d = LookupNode("cpu.#13907"), c = LookupNode("cpu.#13947");
                if (en != EmptyNode && d != EmptyNode && c != EmptyNode)
                {
                    int* cells = AllocHandlerArray<int>(1); cells[0] = c;
                    int* datas = AllocHandlerArray<int>(1); datas[0] = d;
                    _m4Rows[_m4RowCount++] = new M4Row { Kind = M4Kind.DataWins, Enable = en,
                                                           Cells = new NativeNodeList { Nodes = cells, Length = 1 },
                                                           Datas = new NativeNodeList { Nodes = datas, Length = 1 },
                                                           PrevEnable = NodeStates[en] };
                }
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
                    ref M4Row row = ref _m4Rows[_m4RowCount++];
                    row = new M4Row { Kind = M4Kind.Hold, Enable = en,
                                      Cells = CopyHandlerNodeList(cells), PrevEnable = NodeStates[en] };
                    for (int i = 0; i < 8; i++) row.Snap[i] = NodeStates[row.Cells.Nodes[i]];
                }
                else Console.Error.WriteLine($"# [m4] edge-latch row {nm}: nodes unresolved -- skipped");
            }
            if (Environment.GetEnvironmentVariable("M4_DL") is { Length: > 0 } m4dl && m4dl != "0")
            {   // row: DL/idl input latch — TRANSPARENT (glitch immunity). Enable = clk1out==0 (phi2)
                // of a read; cell tracks the settled db. Scoped to $4016/$4017 with a >=2-bit
                // capture signature (the DL shim's measured blast-radius guard). Datas = cpu.db.
                int en = LookupNode("cpu.clk1out");
                var idl = new List<int>(); ResolveNodes("cpu.idl[7:0]", idl, quiet: true);
                var db = new List<int>(); ResolveNodes("cpu.db[7:0]", db, quiet: true);
                if (en != EmptyNode && idl.Count == 8 && db.Count == 8)
                    _m4Rows[_m4RowCount++] = new M4Row { Kind = M4Kind.Transparent, Enable = en,
                                                           Cells = CopyHandlerNodeList(idl), Datas = CopyHandlerNodeList(db),
                                                           PrevEnable = NodeStates[en],
                                                           AddrLo = 0x4016, AddrHi = 0x4017, MinBits = 2 };
                else Console.Error.WriteLine("# [m4] edge-latch row dl_idl: nodes unresolved -- skipped");
            }
            _m4Edge = _m4RowCount > 0;
            Console.Error.WriteLine($"# [m4] edge-latch armed: {_m4RowCount} annotation rows");
        }

        private static void M4EdgeLatchStep()
        {
            for (int r = 0; r < _m4RowCount; r++)
            {
                ref var row = ref _m4Rows[r];
                byte cur = NodeStates[row.Enable];
                if (row.Kind == M4Kind.Transparent)
                {
                    // enable HIGH for a transparent latch = its transparent phase. For DL the
                    // netlist enable is clk1out==0 (phi2), so treat LOW as the transparent window.
                    bool window = cur == 0;
                    if (window && row.AddrHi != 0)
                    {
                        int ab = ReadReg(R_CpuAb);
                        if (ab < row.AddrLo || ab > row.AddrHi || NodeStates[_pdRw] == 0) window = false;
                    }
                    if (window)
                    {
                        int diff = 0;
                        for (int i = 0; i < row.Cells.Length; i++)
                            if (NodeStates[row.Cells.Nodes[i]] != NodeStates[row.Datas.Nodes[i]]) diff++;
                        if (diff >= row.MinBits)
                            for (int i = 0; i < row.Cells.Length; i++)
                            {
                                byte want = NodeStates[row.Datas.Nodes[i]];
                                int n = row.Cells.Nodes[i];
                                if (NodeStates[n] != want) { if (want == 1) SetHigh(n); else SetLow(n); SetFloat(n); }
                            }
                    }
                    row.PrevEnable = cur;
                    continue;
                }
                if (row.PrevEnable == 1 && cur == 0)
                {
                    for (int i = 0; i < row.Cells.Length; i++)
                    {
                        byte want = row.Kind == M4Kind.DataWins ? NodeStates[row.Datas.Nodes[i]] : row.Snap[i];
                        int n = row.Cells.Nodes[i];
                        if (NodeStates[n] != want)
                        { if (want == 1) SetHigh(n); else SetLow(n); SetFloat(n); }
                    }
                }
                row.PrevEnable = cur;
                if (row.Kind == M4Kind.Hold)
                    for (int i = 0; i < row.Cells.Length; i++) row.Snap[i] = NodeStates[row.Cells.Nodes[i]];
            }
        }

        // ── per-hc S1A mechanism dispatch (flattened 2026-07-18) ─────────────────────────────
        // Was a daisy chain hosted inside DmcLatch→Alu→Lxa: any single shim's kill switch
        // silently disabled the whole downstream family — a confounded control for retirement
        // experiments. Flattened with the ORIGINAL execution order preserved exactly:
        // OpenBus → DL → abort → OamEdge → Lxa-rest → M4·P1 (DMC/ALU are M4EdgeLatch M4Row entries
        // now, dispatched at the top). Each step self-guards; an inactive row costs only its guard.
        internal static void TestShimChainStep()
        {
            if (M4EdgeEnabled) M4EdgeLatchStep();   // M4 edge-latch mechanism (DMC data-wins + ALU hold rows)
            OpenBusShimStep();        // self-guarded no-ops unless armed —
            DlShimStep();             // (this was the block hosted at the top of LxaMagicShimStep)
            Dmc4015AbortShimStep();
            OamBlankEdgeShimStep();
            if (LxaMagicShim || M1LxaEnabled) LxaMagicShimStep();
            if (M4P1Enabled) M4P1Step();   // M4.P1 merge-clamp mechanism (supersedes Dbl2007 shim).
                                           // Runs right after LxaMagicShimStep — the exact point the
                                           // shim ran at (its nested FrameIrq->Dbl2007 tail), so it is
                                           // decoupled from LxaMagic/FrameIrq being armed, and mechanism
                                           // -on reproduces shim-on bit-for-bit.
        }

        // ── LXA ($AB) magic-constant mechanism ───────────────────────────────────────────────
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
        private static NativeNodeList _lxaDb, _lxaA, _lxaX;
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
        private static NativeNodeList _laeS;
        private static NativeNodeList _laeNotS;
        private static int _laeSbs = EmptyNode;
        private static int _laeRecent;       // half-cycles since IR last read $BB (write-back overlaps the next fetch)
        private static bool _laeSbsSeen;
        private static int _laePrevSbs;
        private static int _laeVal = -1;     // the correct merge, COMPUTED as (db & pre-op S) in the load window
        private static int _laeOldS = -1;    // S sampled when IR first reads $BB (pre-load)
        private static int _laeDbPrevFall = -1;   // db at the PREVIOUS phi2 fall (the data-read cycle)
        private static NativeNodeList _laeNotA;   // A's complement storage (~a_i), found by topology
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
        // no conducting channel. The S1A full engine arms it; it is idle outside those accesses. ──
        private static bool _openBusShim;
        private static NativeNodeList _pdDb;
        private static int _pdRw = EmptyNode;
        private static int _pdObTop;   // top of the unmapped window ($5FFF with cart extra-ram, else $7FFF)

        public static void EnableOpenBusShim()
        {
            var db = new List<int>(); ResolveNodes("cpu.db[7:0]", db, quiet: true);
            _pdRw = LookupNode("cpu.rw");
            if (db.Count != 8 || _pdRw == EmptyNode)
            { Console.Error.WriteLine("# [shim] open-bus: nodes unresolved -- disabled"); return; }
            _pdDb = CopyHandlerNodeList(db);   // ascending bit order (ReadBits: index i = bit i)
            _pdObTop = LookupNode("cart.eram.gate") != EmptyNode ? 0x5FFF : 0x7FFF;
            _openBusShim = true;
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
        private static void OpenBusShimStep()
        {
            if (!_openBusShim) return;
            if (NodeStates[_pdRw] == 0)
            {
                _pdLastBus = ReadReg(R_CpuDb);   // writes always transfer their bus byte
                return;
            }
            int abNow = ReadReg(R_CpuAb);
            // Opcode FETCHES from APU register space ($4000-$401F) are open-bus too: reading
            // $4015 does not update the data bus (the status byte travels the internal bus), so
            // a PC parked there fetches whatever the wire remembers. AccuracyCoin's
            // ImpliedDummyRead stunts execute exactly this, and the DOR bit-4 precharge glitch
            // (the OpenBus err4 culprit) corrupted those fetches ($28->$38, $68->$78 measured:
            // PLP/PLA became SEC/SEI, leaking one stack byte per stunt). Data reads there keep
            // their native paths; only fetch cycles join the replay window.
            bool apuFetch = abNow >= 0x4000 && abNow <= 0x401F
                         && abNow == ((ReadReg(R_CpuPch) << 8) | ReadReg(R_CpuPcl));
            if ((abNow < 0x4020 && !apuFetch) || abNow > _pdObTop)
            {
                _pdLastBus = ReadReg(R_CpuDb); return;   // driven half-cycle (mapped, or a write) -- record the bus
            }
            for (int b = 0; b < 8; b++)                   // open-bus read -- replay the held byte
            {
                int dbNode = _pdDb.Nodes[b];
                if (AnyChannelOn(dbNode)) continue;        // someone is driving -- hands off
                byte want = (byte)((_pdLastBus >> b) & 1);
                if (NodeStates[dbNode] != want) LaeForce(dbNode, want);
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
        // db whenever they diverge. A no-op on every correctly-resolved cycle. ──
        private static bool _dlShim;
        private static NativeNodeList _dlIdl;
        private static NativeNodeList _dlNotIdl;   // complement side -- a single-sided force snaps back
        private static int _dlClk1 = EmptyNode;

        public static void EnableDlShim()
        {
            var idl = new List<int>(); ResolveNodes("cpu.idl[7:0]", idl, quiet: true);
            var nidl = new List<int>(); ResolveNodes("cpu.notidl[7:0]", nidl, quiet: true);
            _dlClk1 = LookupNode("cpu.clk1out");
            if (idl.Count != 8 || nidl.Count != 8 || _dlClk1 == EmptyNode || _pdRw == EmptyNode || _pdDb.Length != 8)
            { Console.Error.WriteLine("# [shim] DL-transparency: nodes unresolved -- disabled (enable AFTER EnableOpenBusShim)"); return; }
            _dlIdl = CopyHandlerNodeList(idl);
            _dlNotIdl = CopyHandlerNodeList(nidl);
            _dlShim = true;
        }

        private static int _dlHeldMask;   // notidl bits currently clamped (held through the rest of phi2)

        private static void DlShimStep()
        {
            if (!_dlShim) return;
            // Scope: $4016/$4017 ONLY -- the measured race site (u7/u8 OE turn-on transient vs the
            // DL capture). A global version regressed the whole $4015 family (APULengthCounter,
            // FrameCounterIRQ, ...): $4015 reads are INTERNAL to the 2A03 and never touch the
            // external bus, so forcing idl := external db there overwrites the real value with
            // open-bus junk (exactly what AC OpenBus test 7 documents). Minimal blast radius wins.
            bool phi2read = NodeStates[_dlClk1] == 0 && NodeStates[_pdRw] != 0
                          && (ReadReg(R_CpuAb) is 0x4016 or 0x4017);

            if (_dlHeldMask != 0 && !phi2read)
            {
                // phi2 ended (or the cycle stopped being a read): release. During phi1 cclk is off,
                // so the dynamic notidl node float-holds the corrected value; the next phi2
                // re-samples fresh -- no residue.
                for (int b = 0; b < 8; b++)
                    if ((_dlHeldMask >> b & 1) != 0) InstRelease(_dlNotIdl.Nodes[b]);
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
            if (_dlHeldMask == 0 && System.Numerics.BitOperations.PopCount((uint)(ReadBits(_dlIdl.Nodes, _dlIdl.Length) ^ ReadReg(R_CpuDb))) < 2) return;
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
                byte want = NodeStates[_pdDb.Nodes[b]];
                if (NodeStates[_dlIdl.Nodes[b]] == want) continue;
                // The DL is a two-phase DYNAMIC latch: idl = pullup + a notidl-gated pulldown, and
                // notidl is re-driven from the upstream stage through cclk for the whole of phi2 --
                // so a point-force snaps back, and clamping idl HIGH loses to the conducting
                // pulldown (Gnd outranks Pwr). Clamp ONLY notidl (idl follows combinationally) and
                // HOLD the clamp until phi2 ends.
                if (want != 0) InstClampLow(_dlNotIdl.Nodes[b]); else InstClampHigh(_dlNotIdl.Nodes[b]);
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
        // ReadALE at dot 230. The S1A load path enables this before composition so the node split
        // is present; the mux is idle unless the matching $2007-read timing window occurs.
        public static bool AleReadMuxShim = false;
        private static int _muxIoCe = EmptyNode, _muxCpuRw = EmptyNode;
        private static NativeNodeList _muxPpuAb, _muxCpuAb, _muxVp, _muxHp;
        private static int _muxState;          // 0=armed, 1=swallow, 2=wait, 3=replay-hold(io_ab=7 + io_ce=0)
        private static long _muxDetect;
        private static bool _muxReady;         // nodes resolved + split active
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
            var pab = new List<int>(); ResolveNodes("ppu.io_ab[2:0]", pab, quiet: true); _muxPpuAb = pab.Count == 3 ? CopyHandlerNodeList(pab) : default;
            var cab = new List<int>(); ResolveNodes("cpu.ab[2:0]", cab, quiet: true); if (cab.Count != 3) { cab.Clear(); ResolveNodes("2a03.cpu.ab[2:0]", cab, quiet: true); } _muxCpuAb = cab.Count == 3 ? CopyHandlerNodeList(cab) : default;
            var vp = new List<int>(); ResolveNodes("ppu.vpos[8:0]", vp, quiet: true); _muxVp = vp.Count == 9 ? CopyHandlerNodeList(vp) : default;
            var hp = new List<int>(); ResolveNodes("ppu.hpos[8:0]", hp, quiet: true); _muxHp = hp.Count == 9 ? CopyHandlerNodeList(hp) : default;
            if (_muxIoCe == EmptyNode || _muxAle == EmptyNode || _muxCpuRw == EmptyNode || _muxPpuAb.Length != 3 || _muxCpuAb.Length != 3 || _muxVp.Length != 9)
            { Console.Error.WriteLine("# [shim] aleread-mux: nodes unresolved -- disabled"); AleReadMuxShim = false; _muxReady = false; return; }
            _muxState = 0; _muxReady = true;
            MuxRelayIoAb();   // seed: ppu.io_ab := cpu.ab now that the connection is cut
        }

        private static int MuxCpuAb() => (NodeStates[_muxCpuAb.Nodes[2]] << 2) | (NodeStates[_muxCpuAb.Nodes[1]] << 1) | NodeStates[_muxCpuAb.Nodes[0]];
        private static void MuxDriveIoAb(int v) { for (int i = 0; i < 3; i++) { if (((v >> i) & 1) != 0) SetHigh(_muxPpuAb.Nodes[i]); else SetLow(_muxPpuAb.Nodes[i]); } }
        private static void MuxRelayIoAb() { for (int i = 0; i < 3; i++) { if (NodeStates[_muxCpuAb.Nodes[i]] != 0) SetHigh(_muxPpuAb.Nodes[i]); else SetLow(_muxPpuAb.Nodes[i]); } }

        internal static void AleReadMuxStep()
        {
            if (!_muxReady) return;
            if (_muxState == 0)   // armed: detect the stunt $2007 read (io_ce=0 & cpu.ab[2:0]=7 & rw=read @ low visible line)
            {
                if (NodeStates[_muxIoCe] == 0 && NodeStates[_muxCpuRw] != 0 && MuxCpuAb() == 7)
                {
                    int v = ReadBits(_muxVp.Nodes, _muxVp.Length), h = _muxHp.Length == 9 ? ReadBits(_muxHp.Nodes, _muxHp.Length) : -1;
                    if (v >= _muxVlo && v <= _muxVhi && h >= _muxHlo && h <= _muxHhi)
                    { _muxDetect = Time; _muxState = 1; }
                }
                if (_muxState == 0) { MuxRelayIoAb(); return; }
            }
            long dt = Time - _muxDetect;
            // io_ce clamp edges (replay window)
            if (dt == _muxRpStart) { InstClampLow(_muxIoCe); _muxIoCeClamped = true; }
            if (dt == _muxRpEnd && _muxIoCeClamped) { InstRelease(_muxIoCe); _muxIoCeClamped = false; }
            // ale clamp edges (freeze window: hold u2=$FF through dot 229)
            if (dt == _muxFzStart) { InstClampLow(_muxAle); _muxAleClamped = true; }
            if (dt == _muxFzEnd && _muxAleClamped) { InstRelease(_muxAle); _muxAleClamped = false; }
            // io_ab drive: swallow -> 0, replay -> 7, else transparent relay
            if (dt < _muxSwEnd) MuxDriveIoAb(0);
            else if (dt >= _muxRpStart && dt < _muxRpEnd) MuxDriveIoAb(7);
            else MuxRelayIoAb();
            // window done
            if (dt >= _muxFzEnd) { if (_muxIoCeClamped) { InstRelease(_muxIoCe); _muxIoCeClamped = false; } if (_muxAleClamped) { InstRelease(_muxAle); _muxAleClamped = false; } _muxState = 0; }
        }

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
        // clamp the rdy pulldown gate (cpu.#14039) off so the CPU resumes. ──
        private static bool _dmcAbortShim;
        private static int _dmcAbHalt = EmptyNode, _dmcAbDmaAct = EmptyNode, _dmcAbEn = EmptyNode, _dmcAbRdy = EmptyNode, _dmcAbLoadSr = EmptyNode, _dmcAbPhase = EmptyNode;
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
        // The mechanism mirrors the addressed OAM row while rendering and, on the rendering-disable
        // edge, restores whatever that settle wrote into it.
        private static bool _oamEdgeShim;
        private static int _oeRend = EmptyNode;
        private static NativeNodeList _oeSprAddr;
        private static int* _oeCellA, _oeCellB;  // [256 * 8], index = cell * 8 + bit
        private static byte* _oeMirror;          // [8]
        private static int* _oeDriven;           // [128]
        private static int _oeMirrorRow = -1, _oePrevRend, _oeDrivenCount, _oeHold;

        public static void EnableOamBlankEdgeShim()
        {
            _oeRend = LookupNode("ppu.rendering_1");
            var sa = new List<int>(); ResolveNodes("ppu.spr_addr[7:0]", sa, quiet: true);
            _oeCellA = AllocHandlerArray<int>(256 * 8);
            _oeCellB = AllocHandlerArray<int>(256 * 8);
            _oeMirror = AllocHandlerArray<byte>(8);
            _oeDriven = AllocHandlerArray<int>(128);
            int live = 0;
            for (int i = 0; i < 256; i++)
                for (int b = 0; b < 8; b++)
                {
                    int offset = (i << 3) | b;
                    _oeCellA[offset] = LookupNode($"ppu.oam_ram_{i:X2}_a{b}");
                    _oeCellB[offset] = LookupNode($"ppu.oam_ram_{i:X2}_b{b}");
                    if (_oeCellB[offset] != EmptyNode && !IsPwrGnd(_oeCellB[offset])) live++;
                }
            if (_oeRend == EmptyNode || sa.Count != 8 || live == 0)
            { Console.Error.WriteLine("# [shim] oam-blank-edge: nodes unresolved -- disabled"); return; }
            _oeSprAddr = CopyHandlerNodeList(sa);
            _oePrevRend = NodeStates[_oeRend];
            _oeMirrorRow = -1; _oeDrivenCount = 0; _oeHold = 0;
            _oamEdgeShim = true;
        }

        // M4·hold-on-OAM mechanism: wraps the proven OAM-blank-edge state and switches its runtime
        // gate to M4OeEnabled. ArmS1aMechanisms invokes it for every S1A load.
        public static bool M4OeEnabled;
        public static void EnableOamBlankEdgeMech()
        {
            EnableOamBlankEdgeShim();
            if (_oamEdgeShim) { _oamEdgeShim = false; M4OeEnabled = true; }
            Console.Error.WriteLine(M4OeEnabled ? "# [m4oe] armed: OAM blank-edge hold mechanism"
                                                : "# [m4oe] arm failed -- nodes unresolved");
        }

        private static void OamBlankEdgeShimStep()
        {
            if (!_oamEdgeShim && !M4OeEnabled) return;
            if (_oeHold > 0 && --_oeHold == 0)
            {
                bool rel = false;
                for (int i = 0; i < _oeDrivenCount; i++) rel |= SetFloatQueued(_oeDriven[i]);
                _oeDrivenCount = 0;
                if (rel) ProcessQueue();
            }
            int rend = NodeStates[_oeRend];
            int row = (ReadBits(_oeSprAddr.Nodes, _oeSprAddr.Length) >> 3) & 0x1F;
            if (rend != 0 && _oeHold == 0)
            {
                for (int c = 0; c < 8; c++)
                {
                    int idx = (row << 3) | c, v = 0;
                    for (int b = 0; b < 8; b++)
                    {
                        int bn = _oeCellB[(idx << 3) | b];
                        if (bn != EmptyNode && NodeStates[bn] != 0) v |= 1 << b;
                    }
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
                        int offset = (idx << 3) | b;
                        int bn = _oeCellB[offset], an = _oeCellA[offset];
                        if (bn != EmptyNode && !IsPwrGnd(bn))
                        { changed |= one ? SetHighQueued(bn) : SetLowQueued(bn); _oeDriven[_oeDrivenCount++] = bn; }
                        if (an != EmptyNode && !IsPwrGnd(an))
                        { changed |= one ? SetLowQueued(an) : SetHighQueued(an); _oeDriven[_oeDrivenCount++] = an; }
                    }
                }
                if (changed) ProcessQueue();
                _oeHold = 2;   // hold the restore across the edge settle, then release
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
            _dmcAbPhase = LookupNode("cpu.#11466");   // ACLK phase holding the retire gate (#11483) shut
            if (_dmcAbHalt == EmptyNode || _dmcAbDmaAct == EmptyNode || _dmcAbEn == EmptyNode || _dmcAbRdy == EmptyNode || _dmcAbLoadSr == EmptyNode)
            { Console.Error.WriteLine("# [shim] dmc-4015-abort: nodes unresolved -- disabled"); return; }
            _dmcAbortShim = true;
        }

        // M3-abort mechanism: wraps the proven deferred-$4015 DMC-DMA-abort sequence and switches
        // its runtime gate. ArmS1aMechanisms invokes it for every S1A load.
        public static bool M3AbortEnabled;
        public static void EnableDmc4015AbortMech()
        {
            EnableDmc4015AbortShim();
            if (_dmcAbortShim) { _dmcAbortShim = false; M3AbortEnabled = true; }
            Console.Error.WriteLine(M3AbortEnabled ? "# [m3abort] armed: deferred-$4015 DMC-DMA-abort mechanism"
                                                   : "# [m3abort] arm failed -- nodes unresolved");
        }

        private static void Dmc4015AbortShimStep()
        {
            if (!_dmcAbortShim && !M3AbortEnabled) return;
            int en = NodeStates[_dmcAbEn], rdy = NodeStates[_dmcAbRdy];
            int lsr = NodeStates[_dmcAbLoadSr];
            if (_dmcAbPrevLoadSr == 0 && lsr != 0) _dmcAbLastBoundary = Time;   // byte boundary: SR reload cadence, write-independent
            _dmcAbPrevLoadSr = lsr;
            if (rdy == 0) { if (NodeStates[_dmcAbDmaAct] != 0) _dmcAbFetchSeen = true; }
            else _dmcAbFetchSeen = false;
            if (_dmcAbPrevEn == 1 && en == 0)
            {
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
                }
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
            _lxaDb = CopyHandlerNodeList(db); _lxaA = CopyHandlerNodeList(a); _lxaX = CopyHandlerNodeList(x); _laeS = CopyHandlerNodeList(s);
            var ns = new List<int>(); ResolveNodes("cpu.nots[7:0]", ns, quiet: true);
            var sb = new List<int>(); ResolveNodes("cpu.sb[7:0]", sb, quiet: true);
            _laeSbs = LookupNode("cpu.dpc6_SBS");
            _laeAcs = LookupNode("cpu.dpc23_SBAC");
            if (ns.Count != 8 || sb.Count != 8 || _laeSbs == EmptyNode)
            { Console.Error.WriteLine("# [shim] LXA/LAE shim: nots[7:0]/sb[7:0]/dpc6_SBS unresolved — disabled"); LxaMagicShim = false; return; }
            _laeNotS = CopyHandlerNodeList(ns);
            _laeRam = ResolveMemory("u1.ram");
            int* notA = AllocHandlerArray<int>(8);
            for (int i = 0; i < 8; i++)
            {
                notA[i] = GatedGndPullTarget(a[i]);   // a_i -> (~a_i): same dual-side need as S (measured: A reverted $82->$92)
                if (notA[i] == EmptyNode)
                { Console.Error.WriteLine($"# [shim] LXA/LAE shim: A-bit {i} complement unresolved — disabled"); LxaMagicShim = false; return; }
            }
            _laeNotA = new NativeNodeList { Nodes = notA, Length = 8 };
            LaeReadAddr = AllocHandlerArray<int>(16);
            LaeReadVal = AllocHandlerArray<int>(16);
            _lxaPrevPhi2 = NodeStates[_lxaPhi2];
            _lxaArm = 0;
            _laeRecent = 0; _laeWait = 0; _laeVal = -1; _laeSbsSeen = false;
            LxaMagicShim = true;
        }

        // M1-strength LXA mechanism: wraps the proven LXA/ANE magic-merge and switches its runtime
        // gate. ArmS1aMechanisms invokes it for every S1A load.
        public static bool M1LxaEnabled;
        public static void EnableLxaMagicMech()
        {
            EnableLxaMagicShim();
            if (LxaMagicShim) { LxaMagicShim = false; M1LxaEnabled = true; }
            Console.Error.WriteLine(M1LxaEnabled ? "# [m1lxa] armed: LXA/ANE magic-merge mechanism"
                                                 : "# [m1lxa] arm failed -- nodes unresolved");
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
        // cost. The full S1A engine arms it in every mode; the threshold (25.7M hc) cannot fire
        // inside the short benchmark runs used for checksum/performance comparison.
        private static bool _m2Decay;
        public static bool M2DecayEnabled => _m2Decay;
        private static NativeNodeList _m2dNodes;
        private static byte* _m2dPrev;
        private static long* _m2dStamp;
        private const long M2DecayThresholdHc = 36L * 714_732;   // 36 NTSC frames ≈ 600 ms of master clock

        public static void EnableM2Decay()
        {
            var nodes = new List<int>();
            ResolveNodes("ppu._io_db[7:0]", nodes, quiet: true);   // the io data-bus LATCH side (the "decay register")
            if (nodes.Count != 8)
            { Console.Error.WriteLine("# [m2] decay island: nodes unresolved -- disabled"); return; }
            _m2dNodes = CopyHandlerNodeList(nodes);
            _m2dPrev = AllocHandlerArray<byte>(_m2dNodes.Length);
            _m2dStamp = AllocHandlerArray<long>(_m2dNodes.Length);
            for (int i = 0; i < _m2dNodes.Length; i++) { _m2dPrev[i] = NodeStates[_m2dNodes.Nodes[i]]; _m2dStamp[i] = Time; }
            _m2Decay = true;
            Console.Error.WriteLine($"# [m2] decay island armed: {_m2dNodes.Length} nodes, threshold {M2DecayThresholdHc:N0} hc (~600 ms)");
        }

        private static void M2DecayStep()
        {
            if (!_m2Decay || (Time & 0x3FFF) != 0) return;
            for (int i = 0; i < _m2dNodes.Length; i++)
            {
                int n = _m2dNodes.Nodes[i];
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
            // LAE ($BB): qualify by the INSTRUCTION REGISTER, not fetch heuristics — an armed-on-db
            // scheme measurably false-triggered on unrelated bytes and then fired at the next TXS.
            // Subtlety (measured): the S-load SBS pulse arrives ~49 half-cycles AFTER IR has already
            // advanced to the next opcode — the 6502's T0/T1 overlap puts LAE's write-back inside the
            // next instruction's fetch. So keep a short 'ember' after IR reads $BB; an SBS pulse
            // within it is LAE's own load window, while a TXS anywhere else stays excluded.
            if (R_CpuIr.Length == 8 && ReadBits(R_CpuIr.Nodes, R_CpuIr.Length) == 0xBB)
            {
                if (_laeRecent == 0)
                {
                    _laeOldS = ReadBits(_laeS.Nodes, _laeS.Length);   // first sighting: S still pre-op
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
            _laePrevAcs = acsNow;
            int ph = NodeStates[_lxaPhi2];
            if (_lxaPrevPhi2 == 1 && ph == 0)   // phi2 falling = end of a CPU cycle
            {
                int dbv = ReadBits(_lxaDb.Nodes, _lxaDb.Length);
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
                    for (int i = 0; i < 8; i++) { int b = (imm >> i) & 1; Force(_lxaA.Nodes[i], b); Force(_lxaX.Nodes[i], b); }
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
                    }
                    _laeVal = (mem >= 0 ? mem : ReadBits(_lxaA.Nodes, _lxaA.Length)) & _laeOldS;
                    for (int i = 0; i < 8; i++)
                    {
                        int b = (_laeVal >> i) & 1;
                        Force(_lxaA.Nodes[i], b);
                        Force(_laeNotA.Nodes[i], b ^ 1);   // dual-side: A reverts a single-sided force (measured $82 -> $92 by TSX time)
                        Force(_lxaX.Nodes[i], b);
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
                }
                if (_laeWait > 0)
                {
                    _laeWait--;

                    if (R_CpuIr.Length == 8 && ReadBits(R_CpuIr.Nodes, R_CpuIr.Length) == 0xBA)   // TSX just fetched: fix S before its execute reads it
                    {
                        for (int i = 0; i < 8; i++)
                        {
                            int b = (_laeVal >> i) & 1;
                            Force(_laeS.Nodes[i], b);
                            Force(_laeNotS.Nodes[i], b ^ 1);
                        }
                        _laeWait = 0;
                    }
                }

                if (fetchNow && dbv == 0xAB) _lxaArm = 1;
                else if (_lxaArm == 1) { _lxaImm = dbv; _lxaArm = 2; }

                _laeDbPrevFall = dbv;   // becomes "previous fall's db" for the next fall
            }
            _lxaPrevPhi2 = ph;
            if (FrameIrqShim || M4FiEnabled) FrameIrqShimStep();
        }

        // ── Frame-IRQ flag hold mechanism ────────────────────────────────────────────────────
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

        // M4·P6 FrameIrq mechanism: wraps the proven guarded flag restore and switches its runtime
        // gate. ArmS1aMechanisms invokes it for every S1A load.
        public static bool M4FiEnabled;
        public static void EnableFrameIrqMech()
        {
            EnableFrameIrqShim();
            if (FrameIrqShim) { FrameIrqShim = false; M4FiEnabled = true; }
            Console.Error.WriteLine(M4FiEnabled ? "# [m4fi] armed: frame-IRQ flag-hold mechanism"
                                                : "# [m4fi] arm failed -- nodes unresolved");
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

        // ── $2007 double-read merge mechanism (global) ───────────────────────────────────────
        // Back-to-back $2007 reads (LDA abs,X page-cross dummy + real read) merge into ONE
        // buffer advance in the netlist — matching real hardware — but the reload's
        // staging→inbuf→io→db propagation AND the CPU's A load complete inside the same
        // final-phi2 settle wave (op-probe verified), so the CPU read-through gets the NEW
        // buffer value where all four blargg-documented real patterns keep old/transitional
        // values (double_2007_read). Behavioral references model exactly this at the memory
        // layer: a read that lands mid-reload returns the OLD buffer (AprNes ppu_r_2007 state
        // machine, TriCNES read-latch pipeline). The shim is the logical stand-in for the
        // missing physical propagation delay — hence global in S1A, not per-test.
        // ZERO-FOOTPRINT (Gemini consult 2026-07-05): no fake nodes/transistors — a load-time
        // attach renumbers the netlist and re-rolls alignment-lottery races in unrelated
        // tests (measured: dma_2007_read@K=1 flipped to a non-real pattern with zero shim
        // firings). Detection polls in the existing per-hc mechanism chain; the force uses the
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
        private static int _d27Pal = EmptyNode, _d27Rw = EmptyNode, _d27Rdy = EmptyNode, _d27Phi2 = EmptyNode, _d27Nr2007 = EmptyNode;
        private static NativeNodeList _d27Inbuf, _d27Ab, _d27Db;
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
            _d27Inbuf = CopyHandlerNodeList(ib); _d27Ab = CopyHandlerNodeList(ab); _d27Db = CopyHandlerNodeList(db);
            _d27Prev = ReadBits(_d27Inbuf.Nodes, _d27Inbuf.Length);
            _d27Phi2Prev = NodeStates[_d27Phi2];
            _d27Clamped = 0;
            Dbl2007Shim = true;
        }

        private static void Dbl2007ShimStep()
        {
            int now = ReadBits(_d27Inbuf.Nodes, _d27Inbuf.Length);
            if (now != _d27Prev)
            {
                if (_d27Clamped == 0
                    && NodeStates[_d27Nr2007] == 0
                    && NodeStates[_d27Phi2] != 0
                    && NodeStates[_d27Pal] == 0
                    && (ReadBits(_d27Ab.Nodes, _d27Ab.Length) & 0xE007) == 0x2007
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
                    for (int i = 0; i < 8; i++)
                        if (((rose >> i) & 1) != 0) InstClampLow(_d27Db.Nodes[i]);
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
                for (int i = 0; i < 8; i++)
                    if (((_d27Clamped >> i) & 1) != 0) InstRelease(_d27Db.Nodes[i]);
                _d27Clamped = 0;
                _d27Prev = ReadBits(_d27Inbuf.Nodes, _d27Inbuf.Length);
            }
            _d27Phi2Prev = phi2;
        }

        // ── M4·P1 pass-gate merge-clamp mechanism ────────────────────────────────────────────
        // The P1 family: a pass-gate sample/write must capture the value at its intended phase,
        // and a mid-settle change on the SOURCE must not leak into the SINK. Two realizations,
        // same abstraction, different action + scale (see the ledger comparison):
        //   ClampBus    = the $2007 read-buffer merge (Dbl2007). Source=inbuf, sink=cpu.db;
        //                 clamp the risen db bits low through the sample window, release at the
        //                 phi2 fall. Single sample, no queue. Reuses the proven Dbl2007 logic.
        //   QueuedDrive = OamDmaPpuBus (folds in next). Same "hold the intended value against a
        //                 mid-settle race", but forwards 256 batched DMA $2004 writes into OAM,
        //                 so it needs a queue (capture on the put cycle, drive on the later /WE).
        // It supersedes the corresponding shim and runs at the exact chain position the shim did
        // (LxaMagic tail), so its behavior is bit-for-bit identical. The full S1A engine arms it
        // in every mode.
        public static bool M4P1Enabled;
        private static bool _m4p1Clamp;   // ClampBus row (Dbl2007) — runs in M4P1Step (TestShimChainStep tail)
        internal static bool _m4p1Queue;  // QueuedDrive row (OamDmaPpuBus) — runs at WireCore.Recalc.cs (its shim's point)
        public static void EnableM4P1()
        {
            EnableDbl2007Shim();               // ClampBus: resolve + arm the _d27* node state
            if (Dbl2007Shim) { Dbl2007Shim = false; _m4p1Clamp = true; }
            EnableOamDmaPpuBusShim();           // QueuedDrive: resolve + arm the OAM-DMA queue state
            if (OamDmaPpuBusShim) { OamDmaPpuBusShim = false; _m4p1Queue = true; }
            M4P1Enabled = _m4p1Clamp || _m4p1Queue;
            Console.Error.WriteLine($"# [m4p1] armed: ClampBus(Dbl2007)={_m4p1Clamp} QueuedDrive(OamDmaPpuBus)={_m4p1Queue}");
        }
        internal static void M4P1Step()
        {
            // ClampBus only: the QueuedDrive row (OamDmaPpuBus) runs at its shim's own chain point
            // (WireCore.Recalc.cs, gated by `OamDmaPpuBusShim || _m4p1Queue`), so mechanism-on
            // reproduces shim-on bit-for-bit there too. Both members share this env; they do not
            // share a step point (different actions, different hc position — see the ledger).
            if (_m4p1Clamp) Dbl2007ShimStep();
        }

        // ── OAM-DMA from PPU I/O bus write-data hold mechanism ────────────────────────────────
        // ppu_read_buffer's TEST_SPHIT_DMA_PPU_BUS performs $4014 <- $20, so the CPU DMA engine
        // alternates reads from $2000-$20FF with writes to $2004. The CPU-side DMA latch already
        // captures the correct values (verified by cpu.spr_data), but the PPU's delayed $2004->OAM
        // write can sample a later PPU I/O bus value inside the same settle wave. Real hardware's
        // write-data latch holds the $2004 data through /WE. This shim supplies only that hold
        // semantic: capture cpu.spr_data on the DMA put cycle, then drive the addressed OAM cell
        // while the PPU OAM /WE pulse is active. It is the QueuedDrive row of M4·P1.
        public static bool OamDmaPpuBusShim = false;
        private static int _odmaPhi2 = EmptyNode, _odmaRw = EmptyNode, _odmaRdy = EmptyNode, _odmaNW2004 = EmptyNode, _odmaNWe = EmptyNode;
        private static NativeNodeList _odmaAb, _odmaSprData, _odmaSprAddr;
        private static int* _odmaOamA, _odmaOamB;  // [256 * 8], index = cell * 8 + bit
        private static byte* _odmaValueQ, _odmaAddrQ;
        private static int* _odmaDriven;
        private const int OamDmaQueueCapacity = 512;
        private static int _odmaPrevPhi2, _odmaPrevNWe, _odmaPendingPpuGet, _odmaQHead, _odmaQCount, _odmaDrivenCount;
        private static long _odmaLastActivity;

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

            _odmaAb = CopyHandlerNodeList(ab);
            _odmaSprData = CopyHandlerNodeList(sd);
            _odmaSprAddr = CopyHandlerNodeList(sa);
            _odmaOamA = AllocHandlerArray<int>(256 * 8);
            _odmaOamB = AllocHandlerArray<int>(256 * 8);
            _odmaValueQ = AllocHandlerArray<byte>(OamDmaQueueCapacity);
            _odmaAddrQ = AllocHandlerArray<byte>(OamDmaQueueCapacity);
            _odmaDriven = AllocHandlerArray<int>(16);

            int liveCells = 0;
            for (int i = 0; i < 256; i++)
                for (int b = 0; b < 8; b++)
                {
                    int aNode = LookupNode($"ppu.oam_ram_{i:X2}_a{b}");
                    int bNode = LookupNode($"ppu.oam_ram_{i:X2}_b{b}");
                    int offset = (i << 3) | b;
                    _odmaOamA[offset] = aNode;
                    _odmaOamB[offset] = bNode;
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
            if (!OamDmaPpuBusShim && !_m4p1Queue) return;
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
            }
            else if (_odmaPrevNWe == 0 && nWe == 1)
            {
                OamDmaReleaseDrive();
            }
            _odmaPrevNWe = nWe;

            // If a diagnostic run stops mid-pulse or a queue entry becomes stale, fail inertly.
            if (_odmaQCount != 0 && Time - _odmaLastActivity > 4096)
            {
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

            int addr = ReadBits(_odmaAb.Nodes, _odmaAb.Length);
            if (NodeStates[_odmaRw] != 0)
            {
                _odmaPendingPpuGet = (addr & 0xE000) == 0x2000 ? 1 : 0;
                return;
            }

            if (_odmaPendingPpuGet != 0 && (addr & 0xE007) == 0x2004 && NodeStates[_odmaNW2004] == 0)
                OamDmaEnqueue((byte)ReadBits(_odmaSprAddr.Nodes, _odmaSprAddr.Length), (byte)ReadBits(_odmaSprData.Nodes, _odmaSprData.Length));
            _odmaPendingPpuGet = 0;
        }

        private static void OamDmaEnqueue(byte addr, byte value)
        {
            if (_odmaQCount == OamDmaQueueCapacity)
            {
                _odmaQHead = _odmaQCount = 0;
            }
            int tail = (_odmaQHead + _odmaQCount) & (OamDmaQueueCapacity - 1);
            _odmaAddrQ[tail] = addr;
            _odmaValueQ[tail] = value;
            _odmaQCount++;
            _odmaLastActivity = Time;
        }

        private static byte OamDmaDequeueAddr()
        {
            byte v = _odmaAddrQ[_odmaQHead];
            return v;
        }

        private static byte OamDmaDequeueValue()
        {
            byte v = _odmaValueQ[_odmaQHead];
            _odmaQHead = (_odmaQHead + 1) & (OamDmaQueueCapacity - 1);
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
                int offset = (index << 3) | bit;
                int bNode = _odmaOamB[offset];
                int aNode = _odmaOamA[offset];
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
            if (_joyState == null) return;
            byte mask = (byte)(1 << osIdx);
            if (pad == 0)
            {
                if (pressed) _joyState->Buttons0 |= mask;
                else         _joyState->Buttons0 &= (byte)~mask;
            }
            else
            {
                if (pressed) _joyState->Buttons1 |= mask;
                else         _joyState->Buttons1 &= (byte)~mask;
            }
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
