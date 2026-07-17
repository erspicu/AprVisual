using System;
using System.Collections.Generic;

namespace AprVisual.Sim
{
    // ── Class-major node renumbering, ALWAYS ON (range-prune + self-captured locality key) ─────────────
    //
    // Purpose 1 — RANGE-PRUNE (+3.6%): sort ids CLASS-MAJOR so each prune class is one contiguous id
    // block and SetNodeState's prune checks become register compares (see RangePrune* below).
    // Purpose 2 — LOCALITY (+6.2%): within each class block, order nodes by the TRUE first-touch order
    // of the production settle cascade, SELF-CAPTURED at load (no file, no flag, any ROM — the key is
    // re-derived from the actual chip every load, so it cannot go stale).
    //
    // Flow (LoadSystem, auto, three passes): pass 0 classifies the prune bits under identity ids;
    // pass 1 builds class-major with a temporary blind-BFS key — its ranges VERIFY, so the prunes are
    // ON — then warms past the reset transient and records first-touch through the cold instrumented
    // settle copy (WarmupCaptureFirstTouch), translated back to identity ids; pass 2 is the final
    // build with bits + captured key. Bit-exactness: the simulation has exactly ONE id-order-dependent
    // site — the power-on RecomputeAllNodes enqueue sweep — which (like NodeStatesChecksum) iterates
    // in ORIGINAL id order via the permutation, so a renumbered run produces the same physical event
    // sequence and the SAME golden checksums as an identity run. The hot path compiles ONLY the
    // range-compare form — no mode branch, no parameters.
    internal static unsafe partial class WireCore
    {

        /// <summary>Pass-1-captured PruneMask bits per identity id (consumed once by ApplyRenumber).</summary>
        internal static byte[]? PendingClassBits;

        /// <summary>Pass-1-captured STATE-AWARE locality order per identity id (consumed once by
        /// ApplyRenumber; uint.MaxValue = unreached → block tail in original relative order).</summary>
        internal static uint[]? PendingLocalityOrder;

        /// <summary>Capture every node's prune class after the pass-0 Reset() (identity numbering).
        /// Also stashed (StashedClassBits) so the capture pass can re-arm it for the final pass.</summary>
        internal static void CapturePruneClasses()
        {
            var bits = new byte[NodeCount];
            for (int nn = 0; nn < NodeCount; nn++) bits[nn] = (byte)(PruneMask[nn] & 3);
            PendingClassBits = bits;
            StashedClassBits = bits;
        }

        // ── [true first-touch capture] drive the pass-1 warm-up through a COLD copy of the settle
        // loop that records each node's first pop. The hot ProcessQueue stays untouched (zero cost in
        // the final build); this copy runs ONLY during pass-1 and is torn down with it. If it ever
        // drifts from ProcessQueue the damage is bounded to a worse locality KEY (perf-only — the
        // class blocks and all correctness machinery are independent of the key).
        /// <summary>Pass-0-captured class bits, kept ACROSS the capture pass (ApplyRenumber consumes
        /// PendingClassBits at pass 1; pass 1's end re-arms it from this stash for the final pass).</summary>
        internal static byte[]? StashedClassBits;

        internal static unsafe void WarmupCaptureFirstTouch(int hcCount)
        {
            int n = NodeCount;
            var order = new uint[n];
            for (int i = 0; i < n; i++) order[i] = uint.MaxValue;
            uint seq = 0;
            int clk = ClockNode;
            if (clk == EmptyNode) { PendingLocalityOrder = order; return; }

            for (int hc = 0; hc < hcCount; hc++)
            {
                // clock toggle — mirrors StepCycle's branchless flip
                ref NodeInfo cns = ref NodeInfos[clk];
                int nextS = NodeStates[clk] ^ 1;
                cns.Flags = (cns.Flags & ~(NodeFlags.SetHigh | NodeFlags.SetLow)) | (NodeFlags)(8 >> nextS);
                EnqueueNode(clk);

                // settle — mirrors ProcessQueue's double-buffered wave loop, plus the first-touch record
                while (RecalcListNextCount != 0)
                {
                    int* tmpList = RecalcList; RecalcList = RecalcListNext; RecalcListNext = tmpList;
                    byte* tmpHash = RecalcHash; RecalcHash = RecalcHashNext; RecalcHashNext = tmpHash;
                    RecalcListCount = RecalcListNextCount;
                    RecalcListNextCount = 0;
                    for (int i = 0; i < RecalcListCount; i++)
                    {
                        int nn = RecalcList[i];
                        if (RecalcHash[nn] != 0)
                        {
                            if (order[nn] == uint.MaxValue) order[nn] = seq++;   // ← the capture
                            RecalcHash[nn] = 0;
                            RecalcNode(nn);
                        }
                    }
                    RecalcListCount = 0;
                }
                InvokeCallbacks();
                Time++;
            }

            // The capture ran on the PASS-1 build (some temporary permutation, verified ranges, prunes
            // ON — the production cascade). Translate the order back to IDENTITY ids so the final pass's
            // ApplyRenumber (which runs pre-renumber) can consume it: identityOrder[orig] = order[perm[orig]].
            ushort* perm = RenumberPerm;
            int permLen = RenumberPermLen;
            var identityOrder = new uint[n];
            for (int orig = 0; orig < n; orig++)
                identityOrder[orig] = order[orig < permLen ? perm[orig] : orig];
            PendingLocalityOrder = identityOrder;
        }

        /// <summary>old→new id permutation (identity for ids ≥ RenumberPermLen — the post-renumber fake
        /// handler nodes). Null when not renumbered. Read by RecomputeAllNodes / NodeStatesChecksum (both
        /// cold: power-on / report time). UNMANAGED ushort* (node ids &lt; 65K), 29KB instead of a 59KB
        /// managed int[]; lives in the handler-lifetime pool because it must survive Reset()'s
        /// FreeUnmanagedMemory sweep (ApplyRenumber runs pre-Reset; checksum reads it for the process
        /// lifetime) — freed with the other handler arrays at the next rebuild.</summary>
        internal static ushort* RenumberPerm;
        internal static int RenumberPermLen;

        // ── Range-prune boundaries (set when the profile carries prune bits) ──────────────────────────
        // The class-major sort puts each (skip,unsafe) prune class into ONE contiguous id block:
        //   [3, A)  skip ∩ unsafe      [A, S)  skip ∩ safe      [S, B)  no-skip ∩ safe
        //   [B, ∞)  no-skip ∩ unsafe   ← LAST, so post-renumber fake handler nodes (callback targets,
        //                                always no-pull-up ⇒ unsafe, callback ⇒ no-skip) extend it.
        // Then the static prune facts become register compares (supply ids 1,2 < 3 ≤ S ride the skip test):
        //   turn-off skip  ⇔  c <  S          turn-on unsafe  ⇔  c < A  ||  c >= B
        // ClassifyTurnOffSkip verifies every node's computed mask against the ranges before enabling.
        //
        // SAFE-DEGENERATE defaults (used whenever no verified class layout exists — selftest/hand-built
        // netlists, or a verification mismatch): S=3 skips ONLY supply (ids 1,2 — the
        // mandatory guard) and A=∞ treats every node as turn-on-unsafe. That disables the P-1/2/3/4
        // prunes (more no-op re-evals, SLOWER) but stays bit-exact-correct on any numbering.
        internal const int RangeSafeA = int.MaxValue, RangeSafeS = 3, RangeSafeB = int.MaxValue;
        internal static int RangePruneA = RangeSafeA, RangePruneS = RangeSafeS, RangePruneB = RangeSafeB;
        internal static bool RangePruneActive;   // a class-major layout was applied (verify it at Reset)
        internal static bool RangePruneOk;       // ...and the freshly computed mask confirmed it

        public static string LastRenumberStats = "(no renumber)";

        internal static void ApplyRenumber()
        {
            if (PendingClassBits == null) return;
            int n = NodeArrayCount;

            // ── inputs (both captured by earlier passes, identity ids): PendingClassBits = the prune
            //    classes (the MAJOR key — contiguous class blocks → the prune masks become register
            //    range-compares; see RangePrune* above); PendingLocalityOrder = the self-captured
            //    first-touch order (the locality key; absent on the pass-1 temporary build). ──
            var sortKey = new double[n];
            var profiled = new bool[n];
            var pruneBits = new int[n];
            bool haveBits = false;
            int profiledCount = 0;
            {
                var bits = PendingClassBits!;
                int m = Math.Min(n, bits.Length);
                for (int nn = 0; nn < m; nn++) pruneBits[nn] = bits[nn] & 3;
                haveBits = true;
                PendingClassBits = null;   // consume once

                // locality key — preferred: the TRUE first-touch order captured from the production
                // (pruned) cascade during the pass-1 warm-up (see WarmupCaptureFirstTouch).
                if (PendingLocalityOrder != null)
                {
                    var ord = PendingLocalityOrder;
                    int mo = Math.Min(n, ord.Length);
                    for (int nn = 0; nn < mo; nn++)
                        if (ord[nn] != uint.MaxValue) { sortKey[nn] = ord[nn]; profiled[nn] = true; profiledCount++; }
                    PendingLocalityOrder = null;   // consume once
                }
                else
                {
                // fallback locality key — BLIND static approximation: BFS from clk along the SIGNAL-FLOW
                // edges (node → endpoints of the transistors it gates — exactly the edges SetNodeState
                // enqueues through), ignoring gate states. Unreached nodes sink to the block tail.
                int clk = LookupNode("clk");
                if (clk != EmptyNode)
                {
                    var q = new Queue<int>();
                    var seen = new bool[n];
                    int seq = 0;
                    seen[clk] = true; q.Enqueue(clk);
                    while (q.Count > 0)
                    {
                        int u = q.Dequeue();
                        sortKey[u] = ++seq; profiled[u] = true;
                        var un = _nodes[u];
                        if (un == null) continue;
                        foreach (int t in un.Gates)
                        {
                            var tr = _transistors[t];
                            int e1 = tr.C1, e2 = tr.C2;
                            if (e1 > Ngnd && e1 < n && !seen[e1]) { seen[e1] = true; q.Enqueue(e1); }
                            if (e2 > Ngnd && e2 < n && !seen[e2]) { seen[e2] = true; q.Enqueue(e2); }
                        }
                    }
                    profiledCount = seq;
                }
                }   // end blind-BFS fallback
            }

            // block index per prune class — order chosen so no-skip∩unsafe is LAST (fake handler nodes
            // created after the renumber are exactly that class and append to it).
            static int BlockOf(int bits) => bits switch { 3 => 0, 2 => 1, 0 => 2, _ => 3 };   // 1 (unsafe only) => 3

            // ── build the permutation: ids 0 (reserved) / Npwr / Ngnd fixed; the rest ordered by
            //    (pruneClass block when available, locality key, original id). Never-popped and null
            //    slots sink within their block in original relative order. ──
            var ids = new List<int>(n);
            for (int i = 3; i < n; i++) ids.Add(i);
            ids.Sort((a, b) =>
            {
                if (haveBits)
                {
                    int blkc = BlockOf(pruneBits[a]).CompareTo(BlockOf(pruneBits[b]));
                    if (blkc != 0) return blkc;
                }
                double ka = profiled[a] ? sortKey[a] : double.PositiveInfinity;
                double kb = profiled[b] ? sortKey[b] : double.PositiveInfinity;
                int c = ka.CompareTo(kb);
                return c != 0 ? c : a.CompareTo(b);
            });
            var perm = new int[n];
            perm[0] = 0; perm[Npwr] = Npwr; perm[Ngnd] = Ngnd;
            int next = 3;
            foreach (int old in ids) perm[old] = next++;

            if (haveBits)
            {
                int c0 = 0, c1 = 0, c2 = 0;
                foreach (int old in ids)
                {
                    int blk = BlockOf(pruneBits[old]);
                    if (blk == 0) c0++; else if (blk == 1) c1++; else if (blk == 2) c2++;
                }
                RangePruneA = 3 + c0;
                RangePruneS = RangePruneA + c1;
                RangePruneB = RangePruneS + c2;
                RangePruneActive = true;
            }

            // ── apply to every build-time structure that holds node ids ──
            // 1. _nodes: move shells to their new slots (Gates/C1c2s hold TRANSISTOR indices — untouched).
            var oldNodes = new Node?[n];
            for (int i = 0; i < n; i++) oldNodes[i] = _nodes[i];
            for (int i = 0; i < n; i++) { var sh = oldNodes[i]; if (sh != null) sh.Id = perm[i]; _nodes[perm[i]] = sh; }
            // 2. _transistors: remap gate/c1/c2.
            for (int t = 0; t < _transistors.Count; t++)
            {
                var tr = _transistors[t];
                tr.Gate = perm[tr.Gate]; tr.C1 = perm[tr.C1]; tr.C2 = perm[tr.C2];
                _transistors[t] = tr;
            }
            // 3. _transistorSet: rebuild (handler attach dedups against it later).
            _transistorSet.Clear();
            foreach (var tr in _transistors) _transistorSet.Add((tr.Gate, tr.C1, tr.C2));
            // 4. _forceComputeList.
            for (int i = 0; i < _forceComputeList.Count; i++) _forceComputeList[i] = perm[_forceComputeList[i]];
            // 5. name maps.
            var names = new List<KeyValuePair<string, int>>(_nodeByName);
            _nodeByName.Clear(); _nameByNode.Clear();
            foreach (var kv in names)
            {
                int nn = perm[kv.Value];
                _nodeByName[kv.Key] = nn;
                if (!_nameByNode.ContainsKey(nn)) _nameByNode[nn] = kv.Key;
            }

            // persist as unmanaged ushort* (handler-lifetime pool — survives Reset, freed at next rebuild)
            ushort* permU = AllocHandlerArray<ushort>(n);
            for (int i = 0; i < n; i++) permU[i] = (ushort)perm[i];
            RenumberPerm = permU;
            RenumberPermLen = n;
            LastRenumberStats = $"renumber: AUTO class-major permutation ({n:N0} ids, {profiledCount:N0} locality-keyed, range-prune blocks A={RangePruneA} S={RangePruneS} B={RangePruneB})";
        }
    }
}
