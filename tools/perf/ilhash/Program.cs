using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

// ilhash <AprVisual.S1.dll>
//
// Prints a 12-hex-char fingerprint of the WireCore engine's compiled IL.
// Used by the perf workflow to decide which released versions share the SAME C# engine
// (a repackage / docs / tooling release whose hot-path code never changed) so the
// /version/ chart can collapse them to one representative point.
//
// Why IL (not native code size, not file size):
//   - comments / whitespace / doc changes never reach IL                -> immune
//   - DEBUG-only diag tools (#if DEBUG) are stripped from Release IL    -> immune
//   - build metadata / MVID / timestamps are NOT hashed (we hash only
//     method names + their IL byte streams)                            -> immune
//   - platform-independent: the SAME Release DLL on x64 and arm64 gives
//     the same hash (native code size does NOT — that was the arm64
//     false-positive that motivated this).
//
// Method: collect every method on a type whose name contains "WireCore",
// sort by "Type.Method" ordinal, concat (utf8 name)\0(il bytes)\0, SHA256, first 12 hex.
static class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 1) { Console.Error.WriteLine("usage: ilhash <dll>"); return 1; }
        if (!File.Exists(args[0])) { Console.Error.WriteLine("not found: " + args[0]); return 1; }

        var methods = new List<(string name, byte[] il)>();
        using (var fs = File.OpenRead(args[0]))
        using (var pe = new PEReader(fs))
        {
            var mr = pe.GetMetadataReader();
            foreach (var th in mr.TypeDefinitions)
            {
                var td = mr.GetTypeDefinition(th);
                var typeName = mr.GetString(td.Name);
                if (typeName.IndexOf("WireCore", StringComparison.Ordinal) < 0) continue;
                foreach (var mh in td.GetMethods())
                {
                    var md = mr.GetMethodDefinition(mh);
                    var name = typeName + "." + mr.GetString(md.Name);
                    byte[] il = Array.Empty<byte>();
                    if (md.RelativeVirtualAddress != 0)
                    {
                        var body = pe.GetMethodBody(md.RelativeVirtualAddress);
                        il = body.GetILBytes() ?? Array.Empty<byte>();
                    }
                    methods.Add((name, il));
                }
            }
        }

        methods.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        using var ms = new MemoryStream();
        foreach (var (name, il) in methods)
        {
            var nb = Encoding.UTF8.GetBytes(name);
            ms.Write(nb, 0, nb.Length);
            ms.WriteByte(0);
            ms.Write(il, 0, il.Length);
            ms.WriteByte(0);
        }
        var hash = SHA256.HashData(ms.ToArray());
        Console.WriteLine(Convert.ToHexString(hash).Substring(0, 12).ToLowerInvariant());
        return 0;
    }
}
