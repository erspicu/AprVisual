using System;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.D3DCompiler;

namespace AprVisual.Sim.Logic
{
    // ── S4.6 — GPU runtime (D3D11 compute) ───────────────────────────────────────────────────────────────
    //
    // Compiles GpuCodegen.EmitHlslCompute() — two entry points:
    //   step()   — the DAG eval only; a drop-in for StepOneDriving's step-4 (the CPU sets up PrevW; S1 still
    //              does step-5). RunGpuStep() drives it (per-step CPU↔GPU round-trip — equivalence-verified but
    //              slow).
    //   gpuRun() — NumHc half-cycles all on the GPU (each: snapshot PrevW<-NodeState; DAG eval), no per-step
    //              round-trip. RunGpuRunBench(numHc) drives it — the "speed-only" benchmark (G.a). It does NOT
    //              do the SCC fixed-K / bus resolver / memory handlers, so the simulation state goes garbage
    //              after the first half-cycle — but the per-half-cycle compute (~11680-node DAG eval, ~80% of
    //              the work) is roughly right, which is what the speed number needs.
    public static unsafe class GpuRunner
    {
        static ID3D11Device? _device;
        static ID3D11DeviceContext? _ctx;
        static ID3D11ComputeShader? _csStep, _csRun;
        static ID3D11Buffer? _nodeBuf, _prevBuf, _staging, _cbuf;
        static ID3D11Buffer? _bcBuf, _bcOffBuf, _bcCountBuf, _nodeIdBuf, _levelStartBuf;
        static ID3D11UnorderedAccessView? _nodeUav, _prevUav;
        static ID3D11ShaderResourceView? _bcSrv, _bcOffSrv, _bcCountSrv, _nodeIdSrv, _levelStartSrv;
        static uint[] _uploadCur = [], _uploadPrev = [], _resultCur = [];
        static int _n;
        public static bool Initialized;
        public static string AdapterName = "?";
        public static string? InitError;

        static ID3D11Buffer MakeImmutableStructured(uint[] data) => _device!.CreateBuffer(data, new BufferDescription
        {
            ByteWidth = (uint)(data.Length * 4), BindFlags = BindFlags.ShaderResource, Usage = ResourceUsage.Immutable,
            MiscFlags = ResourceOptionFlags.BufferStructured, StructureByteStride = 4u,
        });
        static ID3D11ShaderResourceView MakeSrv(ID3D11Buffer buf, int numElements) => _device!.CreateShaderResourceView(buf, new ShaderResourceViewDescription
        {
            Format = Format.Unknown, ViewDimension = ShaderResourceViewDimension.Buffer,
            Buffer = new BufferShaderResourceView { FirstElement = 0u, NumElements = (uint)numElements },
        });
        static (ID3D11Buffer, ID3D11UnorderedAccessView) MakeRwStructured(uint[] init)
        {
            var b = _device!.CreateBuffer(init, new BufferDescription
            {
                ByteWidth = (uint)(init.Length * 4), BindFlags = BindFlags.UnorderedAccess, Usage = ResourceUsage.Default,
                MiscFlags = ResourceOptionFlags.BufferStructured, StructureByteStride = 4u,
            });
            var u = _device.CreateUnorderedAccessView(b, new UnorderedAccessViewDescription
            {
                Format = Format.Unknown, ViewDimension = UnorderedAccessViewDimension.Buffer,
                Buffer = new BufferUnorderedAccessView { FirstElement = 0u, NumElements = (uint)init.Length },
            });
            return (b, u);
        }

        public static bool Init()
        {
            if (Initialized) return true;
            try
            {
                if (!GpuCodegen.Compiled) GpuCodegen.CompileGpuSchedule();
                _n = Math.Max(IrEngine.NextExpr.Length, WireCore.NodeCount);

                var hr = D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.None, null, out _device, out _ctx);
                if (hr.Failure || _device is null || _ctx is null) { InitError = $"D3D11CreateDevice: {hr}"; return false; }
                try { using var dxgi = _device.QueryInterfaceOrNull<IDXGIDevice>(); if (dxgi != null) { using var ad = dxgi.GetAdapter(); AdapterName = ad.Description.Description; } } catch { }

                var hlsl = GpuCodegen.EmitHlslCompute();
                ID3D11ComputeShader CompileEntry(string entry)
                {
                    var c = Compiler.Compile(hlsl, entry, "ir_step.hlsl", "cs_5_0", out Blob? bl, out Blob? eb);
                    if (c.Failure || bl is null) throw new InvalidOperationException($"HLSL compile [{entry}]: {eb?.AsString() ?? c.ToString()}");
                    var s = _device.CreateComputeShader(bl.AsBytes()); bl.Dispose(); eb?.Dispose(); return s;
                }
                _csStep = CompileEntry("step");
                _csRun  = CompileEntry("gpuRun");

                _bcBuf = MakeImmutableStructured(GpuCodegen.Bytecode);                                       _bcSrv = MakeSrv(_bcBuf, GpuCodegen.Bytecode.Length);
                _bcOffBuf = MakeImmutableStructured(Array.ConvertAll(GpuCodegen.BcOff, x => (uint)x));        _bcOffSrv = MakeSrv(_bcOffBuf, GpuCodegen.BcOff.Length);
                _bcCountBuf = MakeImmutableStructured(Array.ConvertAll(GpuCodegen.BcCount, x => (uint)x));    _bcCountSrv = MakeSrv(_bcCountBuf, GpuCodegen.BcCount.Length);
                _nodeIdBuf = MakeImmutableStructured(Array.ConvertAll(GpuCodegen.NodeId, x => (uint)x));      _nodeIdSrv = MakeSrv(_nodeIdBuf, GpuCodegen.NodeId.Length);
                _levelStartBuf = MakeImmutableStructured(Array.ConvertAll(GpuCodegen.LevelStart, x => (uint)x)); _levelStartSrv = MakeSrv(_levelStartBuf, GpuCodegen.LevelStart.Length);

                _uploadCur = new uint[_n]; _uploadPrev = new uint[_n]; _resultCur = new uint[_n];
                (_nodeBuf, _nodeUav) = MakeRwStructured(_uploadCur);
                (_prevBuf, _prevUav) = MakeRwStructured(_uploadPrev);
                _staging = _device.CreateBuffer(new BufferDescription { ByteWidth = (uint)(_n * 4), Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read });
                _cbuf = _device.CreateBuffer(new uint[4], new BufferDescription { ByteWidth = 16u, BindFlags = BindFlags.ConstantBuffer, Usage = ResourceUsage.Default });

                Initialized = true;
                return true;
            }
            catch (Exception ex) { InitError = $"{ex.GetType().Name}: {ex.Message}"; return false; }
        }

        static void SnapshotInputs()
        {
            int n = WireCore.NodeCount;
            for (int i = 0; i < n; i++) _uploadCur[i] = WireCore.NodeStates[i];
            for (int i = 0; i < n; i++) _uploadPrev[i] = i < IrEngine.PrevStates.Length ? IrEngine.PrevStates[i] : 0u;
            for (int i = n; i < _n; i++) { _uploadCur[i] = 0u; _uploadPrev[i] = 0u; }
        }
        static ID3D11ShaderResourceView[] Srvs() => new[] { _bcSrv!, _bcOffSrv!, _bcCountSrv!, _nodeIdSrv!, _levelStartSrv! };

        /// <summary>Step-4 drop-in (the DAG eval): upload NodeStates + PrevStates -> dispatch step() -> readback
        /// -> write back the changed EvalOrder nodes. Same contract as RunCompiledStep / RunLlvmStep. Per-step
        /// CPU↔GPU round-trip (slow — for the equivalence check, not the benchmark).</summary>
        public static void RunGpuStep()
        {
            if (!Initialized && !Init()) throw new InvalidOperationException($"GPU init failed: {InitError}");
            SnapshotInputs();
            var ctx = _ctx!;
            ctx.UpdateSubresource(_uploadCur, _nodeBuf!);
            ctx.UpdateSubresource(_uploadPrev, _prevBuf!);
            ctx.CSSetShader(_csStep!);
            ctx.CSSetUnorderedAccessViews(0, new[] { _nodeUav!, _prevUav! });
            ctx.CSSetShaderResources(0, Srvs());
            ctx.Dispatch(1, 1, 1);
            ctx.CopyResource(_staging!, _nodeBuf!);
            var mapped = ctx.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            uint* p = (uint*)mapped.DataPointer;
            for (int i = 0; i < _n; i++) _resultCur[i] = p[i];
            ctx.Unmap(_staging!, 0);
            ctx.CSSetUnorderedAccessViews(0, new ID3D11UnorderedAccessView?[] { null, null });
            var order = IrEngine.EvalOrder;
            for (int i = 0; i < order.Length; i++)
            {
                int v = order[i]; byte nv = (byte)(_resultCur[v] & 1u);
                if (WireCore.NodeStates[v] != nv) { WireCore.SetNodeState(v, nv); WireCore.EnqueueNode(v); }
            }
        }

        /// <summary>G.a — the "speed-only" benchmark: upload the current state once, dispatch gpuRun() once
        /// (which loops <paramref name="numHc"/> half-cycles entirely on the GPU — each: snapshot + DAG eval, no
        /// CPU round-trip), force completion, return the elapsed seconds. (The state goes garbage — no SCC / bus
        /// / memory handlers in gpuRun — but the per-half-cycle DAG-eval compute is roughly the real work.)</summary>
        public static double RunGpuRunBench(int numHc)
        {
            if (!Initialized && !Init()) throw new InvalidOperationException($"GPU init failed: {InitError}");
            SnapshotInputs();
            var ctx = _ctx!;
            ctx.UpdateSubresource(_uploadCur, _nodeBuf!);
            ctx.UpdateSubresource(_uploadPrev, _prevBuf!);
            ctx.UpdateSubresource(new uint[] { (uint)numHc, (uint)WireCore.NodeCount, 0u, 0u }, _cbuf!);
            ctx.CSSetShader(_csRun!);
            ctx.CSSetUnorderedAccessViews(0, new[] { _nodeUav!, _prevUav! });
            ctx.CSSetShaderResources(0, Srvs());
            ctx.CSSetConstantBuffers(0, new[] { _cbuf! });
            ctx.Flush();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ctx.Dispatch(1, 1, 1);
            ctx.CopyResource(_staging!, _nodeBuf!);
            var mapped = ctx.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);   // blocks until the GPU finishes the gpuRun dispatch
            ctx.Unmap(_staging!, 0);
            sw.Stop();
            ctx.CSSetUnorderedAccessViews(0, new ID3D11UnorderedAccessView?[] { null, null });
            return sw.Elapsed.TotalSeconds;
        }
    }
}
