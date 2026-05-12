using System;

namespace AprVisual.Sim.Logic
{
    // ── S3 γ.0 — Node Aliasing / Buffer Removal ──────────────────────────────────────────────────────────
    //
    // NMOS/CMOS netlists are full of fan-out *buffer* nodes (NextExpr[a] == NodeRef(b)) and *inverter* nodes
    // (NextExpr[a] == Not(NodeRef(b))) — Visual2A03's  pcm_t5 / pcm_+t5 / pcm_/t5  triples, the  /x  mirrors,
    // …. They carry no logic, but they DO add NodeRef edges to the current-value dependency graph: a 2-node
    // cross-coupled latch  b ⇄ c  whose feedback runs through a buffer  a (= NodeRef(b))  shows up as a 3-node
    // SCC {a,b,c}, so the size-2 latch solver (γ.1) and the SCC anatomy under-report the real structure.
    //
    // This pass resolves every buffer/inverter node to its chain *root* (the first non-buffer/inverter node
    // it ultimately copies), then rewrites every NextExpr so no NodeRef points at an aliased node any more.
    // The aliased nodes survive — their own NextExpr now reads the root directly (NodeRef(root) / Not(NodeRef
    // (root))), so they're still evaluated, just as pure leaves with no incoming edges — off every cycle.
    // BuildEvalOrder's SCC detection and γ.1 then see the actual cross-coupled cells.
    //
    // Buffer/inverter *loops* (a cross-coupled-inverter pair = a latch, e.g. /q = !q, q = !/q) are detected
    // and left alone — those are γ.1's job (a 2-node SCC the size-2 solver turns into Mux(.., Prev(self))).
    //
    // Run *after* SccModel.Build (Stage A/A2 recoveries are already in NextExpr) and *before* BuildEvalOrder.
    // Hold/Prev refs are not rewritten — they read the prev-half-cycle snapshot, which is still correct for an
    // aliased node (its value tracks the root's), and they don't form dependency edges anyway.
    internal static class NodeAlias
    {
        /// <summary>Rewrite <paramref name="nextExpr"/> in place so no <see cref="NodeRefExpr"/> points at a
        /// pure buffer/inverter node. Returns the number of nodes that got aliased (= collapsed as graph edges).</summary>
        public static int Apply(Expr?[] nextExpr)
        {
            int n = nextExpr.Length;

            // direct buffer / inverter detection: dTarget[a] = b, dInv[a] = whether a == !b  (−1 ⇒ a is a root).
            int[] dTarget = new int[n]; Array.Fill(dTarget, -1);
            bool[] dInv = new bool[n];
            for (int a = 0; a < n; a++)
                switch (nextExpr[a])
                {
                    case NodeRefExpr nr                       when nr.Id  != a && nr.Id  >= 0 && nr.Id  < n: dTarget[a] = nr.Id;  dInv[a] = false; break;
                    case NotExpr { Operand: NodeRefExpr nr2 } when nr2.Id != a && nr2.Id >= 0 && nr2.Id < n: dTarget[a] = nr2.Id; dInv[a] = true;  break;
                }

            // resolve chains to roots; a buffer/inverter loop (cycle in dTarget) ⇒ leave the cycle members un-aliased.
            int[] target = new int[n]; Array.Fill(target, -1);   // resolved chain root (−1 ⇒ no alias / is a root / on a cycle)
            bool[] inv = new bool[n];                             // cumulative inversion a → root
            byte[] state = new byte[n];                           // 0 unvisited, 1 in-progress, 2 done
            void Resolve(int a)
            {
                if (state[a] != 0) return;
                if (dTarget[a] < 0) { state[a] = 2; return; }     // a is a root
                state[a] = 1;
                int b = dTarget[a];
                if (state[b] != 1)                                // not a cycle back into the current chain
                {
                    Resolve(b);
                    if (target[b] >= 0)        { target[a] = target[b]; inv[a] = dInv[a] ^ inv[b]; }   // b chains on to a root
                    else if (dTarget[b] < 0)   { target[a] = b;         inv[a] = dInv[a]; }            // b is itself the root
                    // else: b is on a cycle (un-aliased) ⇒ a stays un-aliased (can't point past it)
                }
                state[a] = 2;
            }
            for (int a = 0; a < n; a++) Resolve(a);

            int aliased = 0; for (int a = 0; a < n; a++) if (target[a] >= 0) aliased++;
            if (aliased == 0) return 0;

            // rewrite: NodeRef(a) with target[a] ≥ 0  →  NodeRef(root)  or  Not(NodeRef(root)).
            Expr Rewrite(Expr e) => e switch
            {
                NodeRefExpr nr when nr.Id >= 0 && nr.Id < n && target[nr.Id] >= 0
                                => inv[nr.Id] ? Expr.Not(Expr.Node(target[nr.Id])) : Expr.Node(target[nr.Id]),
                NodeRefExpr     => e,
                NotExpr x       => Expr.Not(Rewrite(x.Operand)),
                AndExpr a       => Expr.And(Rewrite(a.L), Rewrite(a.R)),
                OrExpr  o       => Expr.Or (Rewrite(o.L), Rewrite(o.R)),
                MuxExpr m       => Expr.Mux(Rewrite(m.Cond), Rewrite(m.A), Rewrite(m.B)),
                _               => e,                              // Const / Hold / Prev / Complex — unchanged
            };
            for (int v = 0; v < n; v++) if (nextExpr[v] is { } e) nextExpr[v] = Rewrite(e);
            return aliased;
        }
    }
}
