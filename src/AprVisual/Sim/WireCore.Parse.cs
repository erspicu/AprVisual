using System;
using System.Collections.Generic;

namespace AprVisual.Sim
{
    internal static unsafe partial class WireCore
    {
        // ── Parsing the MetalNES-style `.js` module format.
        //    Reference: ref/metalnes-main/source/metalnes/wire_defs.cpp (deserialize_key<wire_defs>,
        //    the deserialize<transdef>/<segdef>/<moduledef>/... overloads, and loadJsonFile which
        //    eats the leading `var module =` / `var segdefs =` / `var transdefs =` / `var nodenames =`).
        //    Also see ref/AprNes/NesCore/* for the iNES side (handled in Rom/NesRom.cs).
        //    See MD/note/02_模組化網表系統.md for the format walkthrough.
        //
        //    What S1 must preserve (per MD/struct/07 §3.1 and the S1 decisions):
        //      - segdef PullType: BOTH '+' and '-' (MetalNES only kept '+')
        //      - transdef 7th column = IsWeak (weak / depletion-load) — actually use it later
        //      - node names with '/' '#' '~' '_' prefixes; quoted keys like "#pclp0": 1227
        //      - PPU's "same node, two names" aliases (ab7 == db7) — treat as alias, don't error

        // Parsed (pre-build) module tree. Built up by Parse*, consumed by WireCore.Module.cs to
        // allocate the global node-id space and populate the hot arrays.
        internal sealed class ModuleDef
        {
            public string Name = "";
            public string? Description;
            public string? Path;                          // file it came from (for diagnostics)
            public readonly List<PinDef> Pins = new();    // [pin_no -> local node name]
            public readonly Dictionary<string, int> NodeNames = new(); // name -> local node id
            public readonly List<SegDef> Segs = new();
            public readonly List<TransDef> Trans = new();
            public readonly List<SubModuleRef> SubModules = new();     // prefix -> type
            public readonly List<(string From, string To)> Connections = new();
            public readonly List<string> Pullups = new();
            public readonly List<string> ForceCompute = new();
            public readonly Dictionary<string, int> Memories = new(); // name -> size (behavioral RAM/ROM)
            // External file references (large chips put their netlist in separate .js):
            public readonly List<string> NodeNameFiles = new();
            public readonly List<string> TransDefFiles = new();
            public readonly List<string> SegDefFiles = new();
        }

        internal struct PinDef { public int Pin; public string Name; }

        internal struct SegDef
        {
            public NodeRef Node;
            public char Pull;     // '+' = pull-up, '-' = pull-down, '\0' = none. KEEP BOTH.
            public int Layer;
            // polygon points omitted in S1 (not needed for simulation; add back if we do the layout view)
        }

        internal struct TransDef
        {
            public string Name;
            public NodeRef Gate, C1, C2;
            public bool IsWeak;   // 7th column boolean (2A03/2C02). Default false.
            // bbox / geom omitted in S1.
        }

        internal struct SubModuleRef { public string Prefix; public string Type; }

        // A node reference in the source files is *either* a numeric id *or* a name string.
        internal readonly struct NodeRef
        {
            public readonly int Id;        // 0 / EmptyNode if it's a name
            public readonly string? Name;   // null if it's a numeric id
            public NodeRef(int id) { Id = id; Name = null; }
            public NodeRef(string name) { Id = 0; Name = name; }
            public bool IsName => Name != null;
        }

        /// <summary>
        /// Load a module definition (and recursively its sub-modules and external netlist files)
        /// from <paramref name="dir"/>/<paramref name="name"/>.js.
        /// TODO: port wire_defs::Load + loadJsonFile + loadExternalFiles + the deserialize overloads.
        /// A small hand-rolled tokenizer is enough (the files are JSON-ish arrays/objects with
        /// JS-style comments and a `var X =` prefix).
        /// </summary>
        public static ModuleDef LoadModuleDef(string dir, string name)
        {
            throw new NotImplementedException("WireCore.LoadModuleDef — port wire_defs::Load");
        }
    }
}
