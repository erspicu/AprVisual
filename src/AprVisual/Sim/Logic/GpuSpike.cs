using System;
using System.Linq;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.D3DCompiler;

namespace AprVisual.Sim.Logic
{
    // ── S4.6 G.0 — D3D11 compute-shader spike ────────────────────────────────────────────────────────────
    //
    // Verifies the GPU compute toolchain works on this machine: create a D3D11 hardware device, compile a
    // trivial HLSL compute shader at runtime (Data[i] = Data[i]*2 + 1), dispatch it headless (no swap chain /
    // no window), read the result back via a staging buffer, check it. Same role as S4.5's LlvmSpike. --gpu-spike.
    // The real GPU IR step (G.2+) is built on this (runtime-codegen'd HLSL from the netlist → compute dispatch).
    public static unsafe class GpuSpike
    {
        const string Hlsl = @"
RWStructuredBuffer<uint> Data : register(u0);
[numthreads(64,1,1)]
void main(uint3 id : SV_DispatchThreadID) { Data[id.x] = Data[id.x] * 2u + 1u; }
";

        public static (bool ok, string msg) Run()
        {
            const int N = 256;   // multiple of 64 ⇒ no bounds check needed in the trivial kernel
            ID3D11Device? device = null;
            ID3D11DeviceContext? ctx = null;
            ID3D11ComputeShader? cs = null;
            ID3D11Buffer? buf = null, staging = null;
            ID3D11UnorderedAccessView? uav = null;
            try
            {
                var hr = D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.None, null, out device, out ctx);
                if (hr.Failure || device is null || ctx is null) return (false, $"D3D11CreateDevice failed: {hr}");

                Blob? blob;
                var chr = Compiler.Compile(Hlsl, "main", "gpu_spike.hlsl", "cs_5_0", out blob, out Blob? errBlob);
                if (chr.Failure || blob is null) return (false, $"HLSL compile failed: {errBlob?.AsString() ?? chr.ToString()}");
                cs = device.CreateComputeShader(blob.AsBytes());
                blob.Dispose(); errBlob?.Dispose();

                var data = Enumerable.Range(0, N).Select(i => (uint)i).ToArray();
                buf = device.CreateBuffer(data, new BufferDescription
                {
                    ByteWidth = N * 4,
                    BindFlags = BindFlags.UnorderedAccess,
                    Usage = ResourceUsage.Default,
                    MiscFlags = ResourceOptionFlags.BufferStructured,
                    StructureByteStride = 4,
                });
                uav = device.CreateUnorderedAccessView(buf, new UnorderedAccessViewDescription
                {
                    Format = Format.Unknown,
                    ViewDimension = UnorderedAccessViewDimension.Buffer,
                    Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = N },
                });
                staging = device.CreateBuffer(new BufferDescription
                {
                    ByteWidth = N * 4,
                    Usage = ResourceUsage.Staging,
                    CPUAccessFlags = CpuAccessFlags.Read,
                });

                ctx.CSSetShader(cs);
                ctx.CSSetUnorderedAccessView(0, uav);
                ctx.Dispatch(N / 64, 1, 1);
                ctx.Flush();

                ctx.CopyResource(staging, buf);
                var mapped = ctx.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                bool ok = true; int bad = -1; uint got = 0;
                uint* p = (uint*)mapped.DataPointer;
                for (int i = 0; i < N; i++) if (p[i] != (uint)i * 2 + 1) { ok = false; bad = i; got = p[i]; break; }
                ctx.Unmap(staging, 0);

                string adapter = "?";
                try { using var dxgi = device.QueryInterfaceOrNull<IDXGIDevice>(); if (dxgi != null) { using var ad = dxgi.GetAdapter(); adapter = ad.Description.Description; } } catch { }

                return ok
                    ? (true, $"GPU compute OK — Data[i]=Data[i]*2+1 over {N} elements verified  [adapter: {adapter}]")
                    : (false, $"GPU compute MISMATCH at i={bad}: got {got}, expected {(uint)bad * 2 + 1}");
            }
            catch (Exception ex) { return (false, $"{ex.GetType().Name}: {ex.Message}"); }
            finally { uav?.Dispose(); staging?.Dispose(); buf?.Dispose(); cs?.Dispose(); ctx?.Dispose(); device?.Dispose(); }
        }
    }
}
