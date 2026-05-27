using System;
using System.Runtime.CompilerServices;

namespace AprVisual.Sim
{
    internal static unsafe partial class WireCore
    {
        // ── Y (math-algos Phase 2): "Vectorized" event queue — actually a memory-level-parallelism
        //    micro-optimization on the AddNodeToGroup inner walk over TlistC1c2s. Real AVX2 SIMD
        //    on byte* NodeStates is awkward (no gather for byte/sbyte in C# Avx2 intrinsics; would
        //    require converting state to int*, 4× memory, defeating L1-hit baseline). Instead we
        //    manually unroll the inner walk by 4 + issue all 4 byte-loads of NodeStates[gate]
        //    before any branch — the CPU's out-of-order engine overlaps the 4 cache-line fetches
        //    via memory-level parallelism (MLP). Real benefit only on wide-list nodes (~30
        //    transistors / bus nodes); short lists fall through to scalar tail.
        //
        // Gemini's original Y was "pop 8 dirty nodes, VPGATHERDD their flags, VPSHUFB the LUT".
        // The block on that: per-node flag computation requires the group walk itself (variable
        // length / data-dependent recursion) — not vectorizable in one VPGATHERDD. So the actual
        // hot kernel we can attack is the channel-walk inner loop, which is what this does.
        //
        // Gated behind --simd-queue / WireCore.EnableSimdQueue (default off).

        public static bool EnableSimdQueue = false;

        // Per-call recursive helper, unrolled-by-4 over the TlistC1c2s walk.
        // Behaviour is exactly identical to the scalar AddNodeToGroup; only the inner walk shape differs.
        private static void AddNodeToGroupSimd(int nn)
        {
            if (_inGroup[nn] != 0) return;
            _inGroup[nn] = 1;

            ref NodeInfo ns = ref NodeInfos[nn];
            _groupBuf[_groupCount++] = (ushort)nn;

            if (ns.Connections > _maxConnections) { _maxState = NodeStates[nn]; _maxConnections = ns.Connections; }
            RecalcHash[nn] = 0;
            _groupFlags |= ns.Flags;

            // ── unrolled walk over TlistC1c2s ──
            if (ns.TlistC1c2s != 0)
            {
                ushort* p = TransistorList + ns.TlistC1c2s;
                // Unroll 4 (gate, other) pairs = 8 ints at a time. Each iteration:
                //   1. Pre-check that 4 pairs are available (p[7] != 0 means 4 full pairs follow).
                //   2. Issue all 4 NodeStates[gate_i] byte-loads BEFORE any branch — the CPU's
                //      out-of-order engine overlaps the 4 cache-line fetches.
                //   3. Branch on each. Recursing into one pair *can* mutate _inGroup / _groupBuf /
                //      _groupFlags / NodeStates → so each branch must observe state from after the
                //      previous recursion. The loads above are stale for lanes ≥1 of the same
                //      unroll if a prior recursion changed any of those bytes — but in practice
                //      that's rare (recursion changes _groupBuf etc., not NodeStates). The
                //      conduction values are stable within one ProcessQueue wave. So this is safe
                //      modulo a small visit-order shift on the recursion stack (Group resolution
                //      output is invariant to AddNodeToGroup's neighbor visit order).
                while (p[0] != 0 && p[1] != 0 && p[2] != 0 && p[3] != 0 && p[4] != 0 && p[5] != 0 && p[6] != 0 && p[7] != 0)
                {
                    int g0 = p[0], o0 = p[1];
                    int g1 = p[2], o1 = p[3];
                    int g2 = p[4], o2 = p[5];
                    int g3 = p[6], o3 = p[7];
                    // Force 4 independent byte loads — the CPU pipeline issues these concurrently.
                    byte s0 = NodeStates[g0];
                    byte s1 = NodeStates[g1];
                    byte s2 = NodeStates[g2];
                    byte s3 = NodeStates[g3];
                    p += 8;
                    if (s0 != 0) AddNodeToGroupSimd(o0);
                    if (s1 != 0) AddNodeToGroupSimd(o1);
                    if (s2 != 0) AddNodeToGroupSimd(o2);
                    if (s3 != 0) AddNodeToGroupSimd(o3);
                }
                // Scalar tail for the remaining 0-3 pairs (and the case where the whole list is <4).
                while (*p != 0)
                {
                    int gate = *p++;
                    int other = *p++;
                    if (NodeStates[gate] != 0) AddNodeToGroupSimd(other);
                }
            }
            if (ns.TlistC1gnd != 0)
            {
                ushort* p = TransistorList + ns.TlistC1gnd;
                while (*p != 0) { int gate = *p++; if (NodeStates[gate] != 0) { _groupFlags |= NodeFlags.Gnd; break; } }
            }
            if (ns.TlistC1pwr != 0)
            {
                ushort* p = TransistorList + ns.TlistC1pwr;
                while (*p != 0) { int gate = *p++; if (NodeStates[gate] != 0) { _groupFlags |= NodeFlags.Pwr; break; } }
            }
        }

        // Public entry: replaces the scalar ComputeNodeGroup's body when EnableSimdQueue.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static byte ComputeNodeGroupSimd(int nn)
        {
            for (int i = 0; i < _groupCount; i++) _inGroup[_groupBuf[i]] = 0;
            _groupFlags = NodeFlags.None;
            _groupCount = 0;
            _maxState = 0;
            _maxConnections = 0;
            AddNodeToGroupSimd(nn);
            return GetNodeValue();
        }
    }
}
