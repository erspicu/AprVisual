using System;
using System.Collections.Generic;
using System.IO;

namespace AprVisual.Sim
{
    internal static unsafe partial class WireCore
    {
        // ── Parsing the MetalNES-style `.js` module format.
        //    Reference: ref/metalnes-main/source/metalnes/wire_defs.cpp (deserialize_key<wire_defs>,
        //    the deserialize<transdef>/<segdef>/<moduledef>/... overloads, loadJsonFile which eats the
        //    leading `var module = ` / `var segdefs = ` / `var transdefs = ` / `var nodenames = `).
        //    Tokenizer/reader: JsModuleFormat.cs. Format walkthrough: MD/note/02_模組化網表系統.md.
        //
        //    What S1 preserves (per MD/struct/07 §3.1 and the S1 decisions):
        //      - segdef PullType: BOTH '+' and '-' (MetalNES only kept '+')
        //      - transdef 7th column = IsWeak (weak / depletion-load) — used later
        //      - node names with '/' '#' '~' '_' prefixes; quoted keys like "#pclp0": 1227
        //      - PPU's "same node, two names" aliases (ab7 == db7) — recorded as-is; treated as alias in Step 2

        // Parsed (pre-build) module tree. Built up by LoadModuleDef, consumed by WireCore.Module.cs (Step 2)
        // to allocate the global node-id space and populate the hot arrays.
        internal sealed class ModuleDef
        {
            public string Name = "";
            public string? Description;
            public string? Path;                                       // file it came from (diagnostics)
            public readonly List<PinDef> Pins = new();                 // [pin_no -> local node name]
            public readonly Dictionary<string, int> NodeNames = new(); // name -> local node id
            public readonly List<SegDef> Segs = new();
            public readonly List<TransDef> Trans = new();
            public readonly List<SubModuleRef> SubModules = new();     // prefix -> type
            public readonly List<(string From, string To)> Connections = new();
            public readonly List<string> Pullups = new();
            public readonly List<string> ForceCompute = new();
            public readonly Dictionary<string, int> Memories = new();  // name -> size (behavioral RAM/ROM)
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
            // polygon points omitted in S1 (not needed for simulation)
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
            public readonly int Id;        // 0 if it's a name
            public readonly string? Name;  // null if it's a numeric id
            public NodeRef(int id) { Id = id; Name = null; }
            public NodeRef(string name) { Id = 0; Name = name; }
            public bool IsName => Name != null;
            public override string ToString() => IsName ? Name! : Id.ToString();
        }

        // ── module-def cache: keyed by the file stem (== module.name). Lets nes-001's repeated
        //    types (2× SRAM2K, 2× 74LS368, 2× nes-pad) parse once; Step 2 looks them up here. ──
        private static readonly Dictionary<string, ModuleDef> _loadedDefs = new(StringComparer.Ordinal);
        internal static IReadOnlyDictionary<string, ModuleDef> LoadedDefs => _loadedDefs;

        public static void ClearLoadedDefs() => _loadedDefs.Clear();

        /// <summary>Load a module def from <paramref name="fileName"/> and register it under
        /// <paramref name="asName"/>, shadowing any later load of that name (LoadModuleDef is
        /// cache-first). Used to swap in behavioral module variants (e.g. nes-pad-behavioral).</summary>
        public static void PreloadModuleAs(string dir, string fileName, string asName)
        {
            var def = LoadModuleDef(dir, fileName);
            _loadedDefs[asName] = def;
        }

        /// <summary>
        /// Load <paramref name="dir"/>/<paramref name="name"/>.js as a module definition, recursively
        /// loading its sub-modules and external netlist files. Cached by <paramref name="name"/>.
        /// Mirrors wire_defs::Load.
        /// </summary>
        public static ModuleDef LoadModuleDef(string dir, string name)
        {
            if (_loadedDefs.TryGetValue(name, out var cached)) return cached;

            string path = Path.Combine(dir, name + ".js");
            var reader = new JsReader(File.ReadAllText(path), path);
            reader.ExpectVarHeader(out string varName);
            if (varName != "module")
                throw new FormatException($"{path}: expected 'var module = …', got 'var {varName}'");

            var def = new ModuleDef { Path = path };
            _loadedDefs[name] = def;   // register before recursion

            reader.ReadObject((key, r) =>
            {
                switch (key)
                {
                    case "name":            def.Name = r.ReadString(); break;
                    case "description":     def.Description = r.ReadString(); break;
                    case "pins":            r.ReadArray(ar => { ar.BeginArray(); int pin = ar.ReadInt(); string pn = ar.ReadString(); ar.EndArray(); def.Pins.Add(new PinDef { Pin = pin, Name = pn }); }); break;
                    case "nodenames":       r.ReadObject((nk, nr) => def.NodeNames[nk] = nr.ReadInt()); break;
                    case "transdefs":       r.ReadArray(ar => def.Trans.Add(ReadTransDef(ar))); break;
                    case "segdefs":         r.ReadArray(ar => def.Segs.Add(ReadSegDef(ar))); break;
                    case "modules":         r.ReadArray(ar => { ar.BeginArray(); string pfx = ar.ReadString(); string ty = ar.ReadString(); ar.ReadInt(); ar.EndArray(); def.SubModules.Add(new SubModuleRef { Prefix = pfx, Type = ty }); }); break;
                    case "connections":     r.ReadArray(ar => { ar.BeginArray(); string f = ar.ReadString(); string t = ar.ReadString(); ar.EndArray(); def.Connections.Add((f, t)); }); break;
                    case "pullups":         r.ReadArray(ar => def.Pullups.Add(ar.ReadString())); break;
                    case "forceCompute":    r.ReadArray(ar => def.ForceCompute.Add(ar.ReadString())); break;
                    case "memory":          r.ReadObject((mk, mr) => def.Memories[mk] = mr.ReadInt()); break;
                    case "nodenames_files": r.ReadArray(ar => def.NodeNameFiles.Add(ar.ReadString())); break;
                    case "transdefs_files": r.ReadArray(ar => def.TransDefFiles.Add(ar.ReadString())); break;
                    case "segdefs_files":   r.ReadArray(ar => def.SegDefFiles.Add(ar.ReadString())); break;
                    default:                r.SkipValue(); break;   // unknown key — tolerate (e.g. layout-only fields)
                }
            });
            if (string.IsNullOrEmpty(def.Name)) def.Name = name;

            // External netlist files, resolved relative to the module file's directory.
            string moduleDir = Path.GetDirectoryName(path) ?? dir;
            foreach (string rel in def.NodeNameFiles) LoadExternalArray(Path.Combine(moduleDir, rel), r2 => r2.ReadObject((k, nr) => def.NodeNames[k] = nr.ReadInt()), expectVar: "nodenames");
            foreach (string rel in def.TransDefFiles) LoadExternalArray(Path.Combine(moduleDir, rel), r2 => r2.ReadArray(ar => def.Trans.Add(ReadTransDef(ar))), expectVar: "transdefs");
            foreach (string rel in def.SegDefFiles)   LoadExternalArray(Path.Combine(moduleDir, rel), r2 => r2.ReadArray(ar => def.Segs.Add(ReadSegDef(ar))), expectVar: "segdefs");

            // Recursively load sub-module definitions (cached).
            foreach (var sub in def.SubModules) LoadModuleDef(dir, sub.Type);

            return def;
        }

        private static void LoadExternalArray(string path, Action<JsReader> body, string expectVar)
        {
            var r = new JsReader(File.ReadAllText(path), path);
            r.ExpectVarHeader(out string vn);
            // be lenient about the var name (some files use a slightly different name) but warn if surprising
            if (vn != expectVar)
                Console.Error.WriteLine($"{path}: expected 'var {expectVar} = …', got 'var {vn}' — continuing");
            body(r);
        }

        private static TransDef ReadTransDef(JsReader ar)
        {
            ar.BeginArray();
            var td = new TransDef
            {
                Name = ar.ReadString(),
                Gate = ReadNodeRef(ar),
                C1 = ReadNodeRef(ar),
                C2 = ReadNodeRef(ar),
            };
            if (ar.PeekKind() == JsLexer.Kind.LBracket) ar.SkipValue();   // bbox
            if (ar.PeekKind() == JsLexer.Kind.LBracket) ar.SkipValue();   // geom
            if (ar.TryReadBool(out bool weak)) td.IsWeak = weak;          // 7th column (2A03/2C02)
            ar.EndArray();
            return td;
        }

        private static SegDef ReadSegDef(JsReader ar)
        {
            ar.BeginArray();
            var sd = new SegDef { Node = ReadNodeRef(ar) };
            if (ar.PeekKind() == JsLexer.Kind.String || (ar.PeekKind() == JsLexer.Kind.Ident))
            {
                string p = ar.ReadString();
                sd.Pull = p.Length > 0 ? p[0] : '\0';                    // '+' or '-'
            }
            if (ar.PeekKind() == JsLexer.Kind.Number) sd.Layer = ar.ReadInt();
            ar.EndArray();                                                // skips the polygon coordinates
            return sd;
        }

        private static NodeRef ReadNodeRef(JsReader ar)
            => ar.PeekKind() == JsLexer.Kind.Number ? new NodeRef(ar.ReadInt()) : new NodeRef(ar.ReadString());
    }
}
