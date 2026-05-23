using System;
using AprVisual.Sim;

namespace AprVisual.Codegen
{
    /// <summary>
    /// Hand-coded AOT block eval functions — the "target shape" the future AotEmitter will produce
    /// automatically. Each function takes the NodeStates pointer + reads its boundary inputs by
    /// node ID + computes outputs + returns the result for verification.
    ///
    /// For MVP we use Lookup at registration time to resolve node IDs (cached); the eval is a
    /// hot inline call with no string lookups. Future AotEmitter will emit similar resolved code.
    /// </summary>
    public static unsafe class AotBlocks
    {
        // ────────────────────────────────────────────────────────────────────
        // Block #24 — ppu.pclk1_3
        //   FUNCTION: PPU 8-to-1 multiplexer. Selects one of tile_h[7:0] using fine_x[2:0],
        //             gated by the pclk1_3 phase clock. Output goes to +tile_h_bit_out.
        //   This is one of the two pattern-fetch MUXes per scanline (tile_h here, tile_l in #27).
        // ────────────────────────────────────────────────────────────────────

        public struct PpuTileHBitMuxIds
        {
            public int FineX0;       // ppu.finex0 (selector bit 0)
            public int FineX1;       // ppu.finex1
            public int FineX2;       // ppu.finex2
            public int Pclk1_3;      // ppu.pclk1_3 (enable)
            public int TileH0;       // ppu.tile_h0 (data bit 0)
            // tile_h1..7 follow consecutively (verified — see WireCore.LookupNode of all 8)
            public int Output;       // ppu.+tile_h_bit_out
        }

        public static PpuTileHBitMuxIds ResolveTileHBitMux()
        {
            int Need(string n)
            {
                int id = WireCore.LookupNode(n);
                if (id == WireCore.EmptyNode) throw new ArgumentException($"AotBlocks.ResolveTileHBitMux: node '{n}' not found");
                return id;
            }
            var ids = new PpuTileHBitMuxIds
            {
                FineX0   = Need("ppu.finex0"),
                FineX1   = Need("ppu.finex1"),
                FineX2   = Need("ppu.finex2"),
                Pclk1_3  = Need("ppu.pclk1_3"),
                TileH0   = Need("ppu.tile_h0"),
                Output   = Need("ppu.+tile_h_bit_out"),
            };
            // Sanity: verify tile_h[0..7] are at consecutive node IDs so we can compute
            // nodeStates[TileH0 + idx] in EvalTileHBitMux_Combinational. If they aren't, we
            // need an explicit array of 8 IDs instead — fail loud.
            for (int i = 1; i <= 7; i++)
            {
                int actual = Need($"ppu.tile_h{i}");
                int expected = ids.TileH0 + i;
                if (actual != expected)
                    throw new InvalidOperationException(
                        $"AotBlocks.ResolveTileHBitMux: tile_h[{i}] = {actual}, expected {expected} (= tile_h0 + {i}). " +
                        $"Non-consecutive IDs require a different eval shape.");
            }
            return ids;
        }

        /// <summary>Pure combinational MUX evaluation. Returns the selected tile_h bit
        /// (regardless of pclk1_3 — caller decides whether to apply the gate). </summary>
        public static byte EvalTileHBitMux_Combinational(byte* nodeStates, in PpuTileHBitMuxIds ids)
        {
            int idx = (nodeStates[ids.FineX2] << 2)
                    | (nodeStates[ids.FineX1] << 1)
                    |  nodeStates[ids.FineX0];
            // tile_h[0..7] are consecutive node IDs (verified at resolve time below)
            return nodeStates[ids.TileH0 + idx];
        }

        /// <summary>Phi-gated version: when pclk1_3 is low, returns the previous output value
        /// (latch hold). Reflects S1's likely behaviour for the +tile_h_bit_out node.</summary>
        public static byte EvalTileHBitMux_PhiGated(byte* nodeStates, in PpuTileHBitMuxIds ids)
        {
            if (nodeStates[ids.Pclk1_3] == 0) return nodeStates[ids.Output];   // latch hold
            return EvalTileHBitMux_Combinational(nodeStates, in ids);
        }

        // ────────────────────────────────────────────────────────────────────
        // Block: 6502 IR inverter ladder
        //   FUNCTION: notir[i] = NOT(ir[i]) for i in 0..7. Eight independent inverters.
        //             ir changes on every 6502 instruction fetch (1 per ~24 master hc), so this
        //             gets exercised frequently in any CPU test ROM. Pure combinational at the
        //             semantic level (S1 may add phi-latch effects but in steady state holds).
        // ────────────────────────────────────────────────────────────────────

        public sealed class IrInverterIds
        {
            public int[] Ir = new int[8];
            public int[] NotIr = new int[8];
        }

        public static IrInverterIds ResolveIrInverter()
        {
            int Need(string n)
            {
                int id = WireCore.LookupNode(n);
                if (id == WireCore.EmptyNode) throw new ArgumentException($"AotBlocks.ResolveIrInverter: node '{n}' not found");
                return id;
            }
            var ids = new IrInverterIds();
            for (int i = 0; i < 8; i++) { ids.Ir[i] = Need($"cpu.ir{i}"); ids.NotIr[i] = Need($"cpu.notir{i}"); }
            return ids;
        }

        /// <summary>Predict notir[i] from ir[i] for all 8 bits. Returns the predicted byte where
        /// bit i of return = NOT(NodeStates[ir[i]]).</summary>
        public static byte EvalIrInverter(byte* nodeStates, IrInverterIds ids)
        {
            byte predicted = 0;
            for (int i = 0; i < 8; i++)
                if (nodeStates[ids.Ir[i]] == 0) predicted |= (byte)(1 << i);
            return predicted;
        }

        /// <summary>Read S1's actual notir as a byte (bit i = NodeStates[notir[i]]).</summary>
        public static byte ReadIrInverterActual(byte* nodeStates, IrInverterIds ids)
        {
            byte actual = 0;
            for (int i = 0; i < 8; i++)
                actual |= (byte)(nodeStates[ids.NotIr[i]] << i);
            return actual;
        }
    }
}
