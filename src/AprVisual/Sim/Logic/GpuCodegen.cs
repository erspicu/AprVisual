using System;
using System.Collections.Generic;

namespace AprVisual.Sim.Logic
{
    // ── S4.6 — GPU compute-shader codegen (the bytecode-interpreter approach) ─────────────────────────────
    //
    // HLSL has no function pointers, so we can't inline a per-node Expr inside a parallel for-loop, and one
    // giant straight-line kernel blows up the compiler. So the kernel is a FIXED stack-machine interpreter:
    // each thread evaluates one node by running that node's RPN bytecode. The schedule = EvalOrder partitioned
    // into topological levels (GroupMemoryBarrier between levels) + the residual SCCs (one thread per SCC,
    // K=FixedKScc Gauss-Seidel iterations). CompileGpuSchedule() builds the bytecode + the index;
    // EmitHlslCompute() emits the interpreter kernel HLSL (which reads the schedule from StructuredBuffers —
    // uploaded by the runtime, G.2). The bus resolver + handler chain on-GPU are added later (G.2.5+); for the
    // first --gpu-trace-cmp the GPU step is a drop-in for step-4 (the DAG eval) with S1 still doing step-5.
    internal static class GpuCodegen
    {
        // opcode set (matches IrEngine.CompileFlatProgram; OpStore dropped — the interpreter stores implicitly)
        public const int OpLoadNode = 0, OpLoadPrev = 1, OpConst0 = 2, OpConst1 = 3, OpNot = 4, OpAnd = 5, OpOr = 6, OpMux = 7;
        // bytecode word = (arg << 8) | opcode  (arg = node id for LoadNode/LoadPrev, else 0; node ids < 16M fit in the top 24 bits)

        public static uint[] Bytecode = [];          // flat RPN bytecode
        public static int[]  BcOff = [], BcCount = []; // [scheduleIdx] → (offset, count) into Bytecode
        public static int[]  NodeId = [];            // [scheduleIdx] → node id; layout = [EvalOrder grouped by topo level | residual SCCs grouped by SCC, Gauss-Seidel order]
        public static int[]  LevelStart = [];        // [topo level] → scheduleIdx where that level starts; LevelStart[NumLevels] = EvalOrder.Length (= where the SCC portion starts)
        public static int[]  SccStart = [];          // [scc] → scheduleIdx where that SCC starts; SccStart[NumSccs] = total scheduleIdx count
        public static int    MaxStackDepth = 16;
        public const int     NumThreads = 256;
        public static int NumLevels => Math.Max(0, LevelStart.Length - 1);
        public static int NumSccs => Math.Max(0, SccStart.Length - 1);
        public static bool Compiled;

        public static void CompileGpuSchedule()
        {
            IrEngine.BuildTopoLevels();
            var levels = IrEngine.EvalLevels!;
            var nextExpr = IrEngine.NextExpr;
            var sccOrders = IrEngine.SccEvalOrders;

            var bc = new List<uint>(IrEngine.EvalOrder.Length * 10);
            var off = new List<int>();
            var cnt = new List<int>();
            var ids = new List<int>();
            int maxDepth = 1;
            void Emit(int op, int arg) => bc.Add((uint)((arg << 8) | op));
            void Walk(Expr e, ref int depth)
            {
                switch (e)
                {
                    case ConstExpr c:    Emit(c.Value ? OpConst1 : OpConst0, 0); depth++; if (depth > maxDepth) maxDepth = depth; break;
                    case NodeRefExpr nr: Emit(OpLoadNode, nr.Id); depth++; if (depth > maxDepth) maxDepth = depth; break;
                    case HoldExpr h:     Emit(OpLoadPrev, h.Id); depth++; if (depth > maxDepth) maxDepth = depth; break;
                    case PrevExpr p:     Emit(OpLoadPrev, p.Id); depth++; if (depth > maxDepth) maxDepth = depth; break;
                    case NotExpr x:      Walk(x.Operand, ref depth); Emit(OpNot, 0); break;
                    case AndExpr a:      Walk(a.L, ref depth); Walk(a.R, ref depth); Emit(OpAnd, 0); depth--; break;
                    case OrExpr o:       Walk(o.L, ref depth); Walk(o.R, ref depth); Emit(OpOr, 0); depth--; break;
                    case MuxExpr m:      Walk(m.Cond, ref depth); Walk(m.A, ref depth); Walk(m.B, ref depth); Emit(OpMux, 0); depth -= 2; break;
                    default:             Emit(OpConst0, 0); depth++; if (depth > maxDepth) maxDepth = depth; break;   // ComplexExpr shouldn't appear
                }
            }
            void AddNode(int v)
            {
                ids.Add(v);
                off.Add(bc.Count);
                int d = 0;
                if (v >= 0 && v < nextExpr.Length && nextExpr[v] is { } e) Walk(e, ref d); else Emit(OpConst0, 0);
                cnt.Add(bc.Count - off[^1]);
            }
            var levelStart = new List<int>();
            for (int L = 0; L < levels.Length; L++) { levelStart.Add(ids.Count); foreach (int v in levels[L]) AddNode(v); }
            levelStart.Add(ids.Count);   // = EvalOrder.Length, where the SCC portion starts
            var sccStart = new List<int>();
            for (int s = 0; s < sccOrders.Length; s++) { sccStart.Add(ids.Count); foreach (int v in sccOrders[s]) AddNode(v); }
            sccStart.Add(ids.Count);

            Bytecode = bc.ToArray(); BcOff = off.ToArray(); BcCount = cnt.ToArray(); NodeId = ids.ToArray();
            LevelStart = levelStart.ToArray(); SccStart = sccStart.ToArray();
            MaxStackDepth = Math.Max(16, maxDepth + 4);
            Compiled = true;
        }

        public static string EmitHlslCompute()
        {
            if (!Compiled) CompileGpuSchedule();
            int sccNodes = 0; for (int s = 0; s < NumSccs; s++) sccNodes += SccStart[s + 1] - SccStart[s];
            // ASCII-only, if/else-if instead of switch (FXC is picky), no comments inside the kernel bodies.
            // step() does just the DAG eval (a drop-in for StepOneDriving's step-4); the residual SCC / bus
            // resolver / handler chain on-GPU are G.2.5+ (the runtime keeps S1's step-5 for now).
            return
$@"// auto-generated by GpuCodegen.EmitHlslCompute -- S4.6 GPU step kernel (bytecode interpreter).
// {IrEngine.EvalOrder.Length} EvalOrder nodes in {NumLevels} topo levels; {sccNodes} SCC nodes in {NumSccs} SCCs (not yet used here);
// {Bytecode.Length} RPN bytecode words; max stack {MaxStackDepth}; {NumThreads} threads/group.
#define NUM_THREADS {NumThreads}
#define NUM_LEVELS  {NumLevels}u
#define MAX_STACK   {MaxStackDepth}

RWStructuredBuffer<uint> NodeState : register(u0);
StructuredBuffer<uint>   PrevState : register(t0);
StructuredBuffer<uint>   Bytecode  : register(t1);
StructuredBuffer<uint>   BcOff     : register(t2);
StructuredBuffer<uint>   BcCount   : register(t3);
StructuredBuffer<uint>   NodeId    : register(t4);
StructuredBuffer<uint>   LevelStart: register(t5);

// per-thread evaluation stack (in groupshared so the dynamic [sp] indexing doesn't force loop unrolling)
groupshared uint GStk[NUM_THREADS * MAX_STACK];

uint EvalBytecode(uint bcoff, uint bccount, uint tid)
{{
    uint sb = tid * MAX_STACK;
    uint sp = 0u;
    [loop] for (uint k = 0u; k < bccount; k++)
    {{
        uint w = Bytecode[bcoff + k];
        uint op = w & 0xFFu;
        uint arg = w >> 8u;
        if (op == 0u)      {{ GStk[sb + sp] = NodeState[arg]; sp += 1u; }}
        else if (op == 1u) {{ GStk[sb + sp] = PrevState[arg]; sp += 1u; }}
        else if (op == 2u) {{ GStk[sb + sp] = 0u; sp += 1u; }}
        else if (op == 3u) {{ GStk[sb + sp] = 1u; sp += 1u; }}
        else if (op == 4u) {{ GStk[sb + sp - 1u] = 1u - GStk[sb + sp - 1u]; }}
        else if (op == 5u) {{ uint r = GStk[sb + sp - 1u]; sp -= 1u; GStk[sb + sp - 1u] = GStk[sb + sp - 1u] & r; }}
        else if (op == 6u) {{ uint r = GStk[sb + sp - 1u]; sp -= 1u; GStk[sb + sp - 1u] = GStk[sb + sp - 1u] | r; }}
        else               {{ uint b = GStk[sb + sp - 1u]; uint a = GStk[sb + sp - 2u]; uint c = GStk[sb + sp - 3u]; sp -= 2u; GStk[sb + sp - 1u] = (c != 0u) ? a : b; }}
    }}
    return GStk[sb];
}}

[numthreads(NUM_THREADS, 1, 1)]
void step(uint tid : SV_GroupIndex)
{{
    [loop] for (uint L = 0u; L < NUM_LEVELS; L += 1u)
    {{
        uint lo = LevelStart[L];
        uint hi = LevelStart[L + 1u];
        for (uint i = lo + tid; i < hi; i += NUM_THREADS)
        {{
            uint v = NodeId[i];
            NodeState[v] = EvalBytecode(BcOff[i], BcCount[i], tid) & 1u;
        }}
        AllMemoryBarrierWithGroupSync();
    }}
}}
";
        }
    }
}
