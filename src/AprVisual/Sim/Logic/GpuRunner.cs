using System;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.D3DCompiler;

namespace AprVisual.Sim.Logic
{
    // ── S4.6 G.2 — GPU runtime (D3D11 compute) ───────────────────────────────────────────────────────────
    //
    // Compiles GpuCodegen.EmitHlslCompute() into a D3D11 compute shader; uploads the schedule (Bytecode /
    // BcOff / BcCount / NodeId / LevelStart) as immutable structured buffers; runs the kernel as a drop-in for
    // StepOneDriving's step-4 (the DAG eval — the residual SCCs / buses / handlers stay with S1's step-5, same
    // contract as RunLlvmStep). Per-step: upload NodeStates + PrevStates → Dispatch(1,1,1) → readback → write
    // back the changed EvalOrder nodes. The per-step round-trip is SLOW (a full CPU↔GPU sync each half-cycle) —
    // fine for the equivalence check (--trace-cmp --engine ir --gpu-step); the fast on-GPU version (state stays
    // on GPU, N half-cycles per dispatch) is G.3.
    public static unsafe class GpuRunner
    {
        static ID3D11Device? _device;
        static ID3D11DeviceContext? _ctx;
        static ID3D11ComputeShader? _cs;
        static ID3D11Buffer? _nodeBuf, _prevBuf, _staging;
        static ID3D11Buffer? _bcBuf, _bcOffBuf, _bcCountBuf, _nodeIdBuf, _levelStartBuf;
        static ID3D11UnorderedAccessView? _nodeUav;
        static ID3D11ShaderResourceView? _prevSrv, _bcSrv, _bcOffSrv, _bcCountSrv, _nodeIdSrv, _levelStartSrv;
        static uint[] _uploadCur = [], _uploadPrev = [], _resultCur = [];
        static int _n;
        public static bool Initialized;
        public static string AdapterName = "?";
        public static string? InitError;

        static ID3D11Buffer MakeImmutableStructured(uint[] data)
        {
            var d = _device!;
            return d.CreateBuffer(data, new BufferDescription
            {
                ByteWidth = (uint)(data.Length * 4),
                BindFlags = BindFlags.ShaderResource,
                Usage = ResourceUsage.Immutable,
                MiscFlags = ResourceOptionFlags.BufferStructured,
                StructureByteStride = 4u,
            });
        }
        static ID3D11ShaderResourceView MakeSrv(ID3D11Buffer buf, int numElements)
            => _device!.CreateShaderResourceView(buf, new ShaderResourceViewDescription
            {
                Format = Format.Unknown,
                ViewDimension = ShaderResourceViewDimension.Buffer,
                Buffer = new BufferShaderResourceView { FirstElement = 0u, NumElements = (uint)numElements },
            });

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
                var chr = Compiler.Compile(hlsl, "step", "ir_step.hlsl", "cs_5_0", out Blob? blob, out Blob? errBlob);
                if (chr.Failure || blob is null) { InitError = $"HLSL compile: {errBlob?.AsString() ?? chr.ToString()}"; return false; }
                _cs = _device.CreateComputeShader(blob.AsBytes());
                blob.Dispose(); errBlob?.Dispose();

                // schedule buffers (immutable)
                _bcBuf = MakeImmutableStructured(GpuCodegen.Bytecode);                    _bcSrv = MakeSrv(_bcBuf, GpuCodegen.Bytecode.Length);
                _bcOffBuf = MakeImmutableStructured(Array.ConvertAll(GpuCodegen.BcOff, x => (uint)x));         _bcOffSrv = MakeSrv(_bcOffBuf, GpuCodegen.BcOff.Length);
                _bcCountBuf = MakeImmutableStructured(Array.ConvertAll(GpuCodegen.BcCount, x => (uint)x));     _bcCountSrv = MakeSrv(_bcCountBuf, GpuCodegen.BcCount.Length);
                _nodeIdBuf = MakeImmutableStructured(Array.ConvertAll(GpuCodegen.NodeId, x => (uint)x));       _nodeIdSrv = MakeSrv(_nodeIdBuf, GpuCodegen.NodeId.Length);
                _levelStartBuf = MakeImmutableStructured(Array.ConvertAll(GpuCodegen.LevelStart, x => (uint)x)); _levelStartSrv = MakeSrv(_levelStartBuf, GpuCodegen.LevelStart.Length);

                // state buffers (Default — updated each step via UpdateSubresource)
                _uploadCur = new uint[_n]; _uploadPrev = new uint[_n]; _resultCur = new uint[_n];
                _nodeBuf = _device.CreateBuffer(_uploadCur, new BufferDescription { ByteWidth = (uint)(_n * 4), BindFlags = BindFlags.UnorderedAccess, Usage = ResourceUsage.Default, MiscFlags = ResourceOptionFlags.BufferStructured, StructureByteStride = 4u });
                _nodeUav = _device.CreateUnorderedAccessView(_nodeBuf, new UnorderedAccessViewDescription { Format = Format.Unknown, ViewDimension = UnorderedAccessViewDimension.Buffer, Buffer = new BufferUnorderedAccessView { FirstElement = 0u, NumElements = (uint)_n } });
                _prevBuf = _device.CreateBuffer(_uploadPrev, new BufferDescription { ByteWidth = (uint)(_n * 4), BindFlags = BindFlags.ShaderResource, Usage = ResourceUsage.Default, MiscFlags = ResourceOptionFlags.BufferStructured, StructureByteStride = 4u });
                _prevSrv = MakeSrv(_prevBuf, _n);
                _staging = _device.CreateBuffer(new BufferDescription { ByteWidth = (uint)(_n * 4), Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read });

                Initialized = true;
                return true;
            }
            catch (Exception ex) { InitError = $"{ex.GetType().Name}: {ex.Message}"; return false; }
        }

        /// <summary>Step-4 drop-in (the DAG eval): upload NodeStates + PrevStates → dispatch the kernel →
        /// readback → write back the changed EvalOrder nodes. Same contract as RunCompiledStep / RunLlvmStep.</summary>
        public static void RunGpuStep()
        {
            if (!Initialized && !Init()) throw new InvalidOperationException($"GPU init failed: {InitError}");
            int n = WireCore.NodeCount;
            for (int i = 0; i < n; i++) _uploadCur[i] = WireCore.NodeStates[i];
            for (int i = 0; i < n; i++) _uploadPrev[i] = i < IrEngine.PrevStates.Length ? IrEngine.PrevStates[i] : 0u;
            for (int i = n; i < _n; i++) { _uploadCur[i] = 0u; _uploadPrev[i] = 0u; }

            var ctx = _ctx!;
            ctx.UpdateSubresource(_uploadCur, _nodeBuf!);
            ctx.UpdateSubresource(_uploadPrev, _prevBuf!);
            ctx.CSSetShader(_cs!);
            ctx.CSSetUnorderedAccessView(0, _nodeUav!);
            ctx.CSSetShaderResources(0, new[] { _prevSrv!, _bcSrv!, _bcOffSrv!, _bcCountSrv!, _nodeIdSrv!, _levelStartSrv! });
            ctx.Dispatch(1, 1, 1);
            ctx.CopyResource(_staging!, _nodeBuf!);
            var mapped = ctx.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            uint* p = (uint*)mapped.DataPointer;
            for (int i = 0; i < _n; i++) _resultCur[i] = p[i];
            ctx.Unmap(_staging!, 0);
            // unbind the UAV (D3D11 complains if it stays bound while we re-write the buffer)
            ctx.CSSetUnorderedAccessView(0, null);

            var order = IrEngine.EvalOrder;
            for (int i = 0; i < order.Length; i++)
            {
                int v = order[i]; byte nv = (byte)(_resultCur[v] & 1u);
                if (WireCore.NodeStates[v] != nv) { WireCore.SetNodeState(v, nv); WireCore.EnqueueNode(v); }
            }
        }
    }
}
