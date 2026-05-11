using System;

namespace AprVisual.Sim
{
    internal static unsafe partial class WireCore
    {
        // ── Change propagation — port of ref/metalnes-main wire_module.cpp:
        //      recalcNodeList / processQueue / recalcNode / setNodeState / enqueueNode (~L1519-1928)
        //    See MD/note/01_模擬核心演算法.md §2.2-2.6.

        /// <summary>Mark a node dirty and run propagation to quiescence.</summary>
        public static void RecalcNodeList(int nn)
        {
            EnqueueNode(nn);
            ProcessQueue();
        }

        /// <summary>Mark several nodes dirty and run propagation to quiescence.</summary>
        public static void RecalcNodeList(ReadOnlySpan<int> list)
        {
            foreach (int nn in list) EnqueueNode(nn);
            ProcessQueue();
        }

        private static void ProcessQueue()
        {
            // TODO: port processQueue (~L1536-1579):
            //   int iteration = 0;
            //   while (RecalcListNextCount != 0) {
            //       if (++iteration == 100) Console.Error.WriteLine($"settle iteration warning {iteration}");
            //       (swap RecalcList <-> RecalcListNext, RecalcHash <-> RecalcHashNext, swap counts)
            //       for each nn in RecalcList: if RecalcHash[nn] != 0 { RecalcNode(nn); RecalcHash[nn] = 0; }
            //       RecalcListCount = 0;
            //   }
            //   InvokeCallbacks();   // (WireCore.Handlers.cs) — memory reads/writes after the dust settles
            //
            // NOTE (S1 vs MD/struct/01 §11.2): iterate to *real* convergence (RecalcListNext empty),
            // not a fixed 3-pass; the 100-count is only a warning, not a hard stop. S2 adds proper
            // loop/SCC handling.
            throw new NotImplementedException("WireCore.ProcessQueue — port wire_module.cpp processQueue");
        }

        private static void RecalcNode(int nn)
        {
            if (nn == Npwr || nn == Ngnd) return;
            // TODO: port recalcNode (~L1866-1908):
            //   byte newState = ComputeNodeGroup(nn);          // (WireCore.Group.cs) — also fills _groupBuf / _groupFlags
            //   for each gnn in _groupBuf[0.._groupCount): SetNodeState(gnn, newState);
            //   if (_groupFlags & HasCallback): for each gnn with a callback -> EnqueueCallback(...)
            throw new NotImplementedException("WireCore.RecalcNode — port wire_module.cpp recalcNode");
        }

        private static void SetNodeState(int nn, byte newState)
        {
            // TODO: port setNodeState (~L1597-1629):
            //   if (NodeStates[nn] != newState) {
            //       NodeStates[nn] = newState;
            //       walk TlistGates: while *p: c1=*p++, c2=*p++;
            //           EnqueueNode(c1); if (newState==0 && c2 != Npwr && c2 != Ngnd) EnqueueNode(c2);
            //   }
            throw new NotImplementedException("WireCore.SetNodeState — port wire_module.cpp setNodeState");
        }

        private static void EnqueueNode(int nn)
        {
            if (nn == Npwr || nn == Ngnd) return;
            // TODO: port enqueueNode (~L1920-1928):
            //   if (RecalcHashNext[nn] == 0) { RecalcListNext[RecalcListNextCount++] = nn; RecalcHashNext[nn] = 1; }
            throw new NotImplementedException("WireCore.EnqueueNode — port wire_module.cpp enqueueNode");
        }

        // ── External drive / float (port of setHigh/setLow/setFloat — wire_module.cpp ~L191-208) ──

        public static void SetHigh(int nn)  { /* NodeInfos[nn].Flags &= ~SetLow; |= SetHigh; */ RecalcNodeList(nn); throw new NotImplementedException(); }
        public static void SetLow(int nn)   { /* NodeInfos[nn].Flags &= ~SetHigh; |= SetLow; */ RecalcNodeList(nn); throw new NotImplementedException(); }
        public static void SetFloat(int nn) { /* NodeInfos[nn].Flags &= ~(SetLow|SetHigh);  */ RecalcNodeList(nn); throw new NotImplementedException(); }

        public static bool IsNodeHigh(int nn) => NodeStates[nn] != 0;

        // ── One half-cycle: toggle the master clock node and run the handler chain (port of step_cycle) ──

        public static int ClockNode = EmptyNode;   // resolved in WireCore.System.cs after the module loads

        public static void Step(int count)
        {
            for (int i = 0; i < count; i++) StepCycle();
        }

        private static void StepCycle()
        {
            // TODO: port step_cycle (~L730-751):
            //   toggle ClockNode (SetHigh/SetLow);  RunHandlerChain();  if (TraceLevel != 0) DumpTrace();  Time++;
            throw new NotImplementedException("WireCore.StepCycle — port wire_module.cpp step_cycle");
        }
    }
}
