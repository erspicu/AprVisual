using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;

namespace AprVisual.Sim
{
    // ARM1 compatibility layer for the Visual 6502 ARM1 data set. Its raw netlist differs
    // materially from the 6502-family files: flat five-int transistor records, signed gate
    // polarity, simulator pad pseudo-transistors, and ffdefs-driven cross-coupled latches.
    internal static unsafe partial class WireCore
    {
        // ARM1 has fewer than 32K nodes, so the high bit in a flattened gate entry can carry
        // active-low polarity without changing the existing ushort adjacency layout.
        private const int RawArm1InvertedGateBit = 0x8000;

        // Read by Reset() and RecalcNode(). The normal engine's active-high fast paths stay intact.
        private static bool RawArm1Mode;
        private static byte* RawArm1FlipFlopNodes;

        private readonly struct RawArm1PadDef
        {
            public readonly string Name;
            public readonly bool Input;

            public RawArm1PadDef(string name, bool input)
            {
                Name = name;
                Input = input;
            }
        }

        private sealed class RawArm1Netlist
        {
            public readonly ModuleDef Def = new() { Name = "arm1" };
            public readonly List<RawArm1PadDef> Pads = new();
        }

        // Each source pad becomes two ordinary source transistors: one to VDD and one to VSS.
        // This preserves the reference simulator's electrical pad semantics while avoiding a
        // direct external-force priority change on the actual ARM1 net node.
        private readonly struct RawArm1PadDrive
        {
            public readonly bool Input;
            public readonly int HighGate;
            public readonly int LowGate;

            public RawArm1PadDrive(bool input, int highGate, int lowGate)
            {
                Input = input;
                HighGate = highGate;
                LowGate = lowGate;
            }
        }

        private static Dictionary<string, RawArm1PadDrive>? _rawArm1Pads;
        private static int* _armDataHighGates;
        private static int* _armDataLowGates;
        private static int _armPhi1Node, _armPhi1High, _armPhi1Low;
        private static int _armPhi2High, _armPhi2Low;
        private static int _armResetHigh, _armResetLow;
        private static int _armDbeHigh, _armDbeLow, _armAbeHigh, _armAbeLow;
        private static int _armAbrtHigh, _armAbrtLow, _armAleHigh, _armAleLow;
        private static int _armFirqHigh, _armFirqLow, _armIrqHigh, _armIrqLow;

        private static RawArm1Netlist ParseRawArm1Netlist(string dir)
        {
            var arm = new RawArm1Netlist();
            var nameById = new Dictionary<int, string>();

            LoadExternalArray(Path.Combine(dir, "nodenames.js"), r => r.ReadObject((idText, nr) =>
            {
                if (!int.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out int id))
                    throw new FormatException($"{dir}: ARM1 nodenames key '{idText}' is not numeric");
                string name = nr.ReadString();
                arm.Def.NodeNames[name] = id;
                nameById[id] = name;
            }), "nodenames");

            LoadExternalArray(Path.Combine(dir, "transdefs.js"), r => r.ReadArray(ar =>
            {
                int signedGate = ar.ReadInt();
                int c1 = ar.ReadInt();
                int c2 = ar.ReadInt();
                ar.ReadInt(); // layout x, unused by simulation
                ar.ReadInt(); // layout y, unused by simulation

                int gate = Math.Abs(signedGate);
                switch (gate)
                {
                    case 10000: // output pad: observable only, no external channel
                        return;

                    case 10001: // input pad: source drives c1 through a selectable VDD/VSS channel
                    case 10002: // I/O pad: same channel, but may be floated before a CPU write
                        if (!nameById.TryGetValue(c1, out string? padName))
                            throw new FormatException($"{dir}: ARM1 pad node {c1} has no name");
                        arm.Pads.Add(new RawArm1PadDef(padName, gate == 10001));
                        return;

                    case 10003: // gate tied to VSS: negative record is permanently on
                    case 10004: // gate tied to VDD: positive record is permanently on
                        bool alwaysOn = gate == 10003 ? signedGate < 0 : signedGate > 0;
                        if (alwaysOn)
                        {
                            arm.Def.Trans.Add(new TransDef
                            {
                                Gate = new NodeRef(1), // local VDD; mapped to Npwr by AddInstance
                                C1 = new NodeRef(c1),
                                C2 = new NodeRef(c2),
                            });
                        }
                        return;
                }

                arm.Def.Trans.Add(new TransDef
                {
                    Gate = new NodeRef(gate),
                    C1 = new NodeRef(c1),
                    C2 = new NodeRef(c2),
                    ActiveLow = signedGate < 0,
                });
            }), "transdefs");

            LoadExternalArray(Path.Combine(dir, "ffdefs.js"), r => r.ReadArray(ar =>
            {
                arm.Def.FlipFlopNodeIds.Add(ar.ReadInt());
            }), "ffdefs");

            return arm;
        }

        private static void BuildRawArm1Netlist(string dir)
        {
            var arm = ParseRawArm1Netlist(dir);
            if (!arm.Def.NodeNames.TryGetValue("vdd", out int vdd))
                throw new FormatException($"{dir}: ARM1 nodenames.js has no vdd node");
            if (!arm.Def.NodeNames.ContainsKey("vss"))
                throw new FormatException($"{dir}: ARM1 nodenames.js has no vss node");

            // AddInstance folds local vcc/vss onto the engine's supply nodes.
            arm.Def.NodeNames["vcc"] = vdd;
            AddInstance(arm.Def, "");

            _rawArm1Pads = new Dictionary<string, RawArm1PadDrive>(StringComparer.Ordinal);
            foreach (var pad in arm.Pads)
            {
                if (_rawArm1Pads.ContainsKey(pad.Name)) continue;
                int padNode = LookupNode(pad.Name);
                if (padNode == EmptyNode) throw new FormatException($"{dir}: ARM1 pad '{pad.Name}' was not created");

                int highGate = AddNamedNode("__arm1_drive_" + pad.Name + "_hi");
                int lowGate = AddNamedNode("__arm1_drive_" + pad.Name + "_lo");
                AddTransistor("", highGate, padNode, Npwr);
                AddTransistor("", lowGate, padNode, Ngnd);
                _rawArm1Pads.Add(pad.Name, new RawArm1PadDrive(pad.Input, highGate, lowGate));
            }

            // Lowering and range pruning were validated for active-high NMOS graphs. Keep the raw
            // ARM1 topology intact until an ARM-specific equivalence suite exists.
            LastLowerStats = "(ARM1: raw topology retained; lowering disabled)";
        }

        private static RawArm1PadDrive RequireRawArm1Pad(string name)
        {
            if (_rawArm1Pads == null || !_rawArm1Pads.TryGetValue(name, out var pad))
                throw new InvalidOperationException($"ARM1 netlist has no driveable '{name}' pad");
            return pad;
        }

        private static void FinishRawArm1Load()
        {
            if (NodeArrayCount >= RawArm1InvertedGateBit)
                throw new InvalidOperationException("ARM1 raw netlist exceeds the active-low gate encoding range");

            RawArm1Mode = true;
            Reset();

            RawArm1FlipFlopNodes = AllocArray<byte>(NodeCount);
            for (int nn = 0; nn < NodeCount; nn++)
                if (Nodes[nn]?.IsFlipFlop == true) RawArm1FlipFlopNodes[nn] = 1;

            _rawAddressBits = 26;
            _rawDataBits = 32;
            _rawMem = null; // ARM1 uses a constant 32-bit NOP word, not the 8-bit raw CPU sled.
            _abNodes = ResolveBusNodes("a", _rawAddressBits, "_pad");
            _dbNodes = ResolveBusNodes("d", _rawDataBits, "_pad");
            for (int i = 0; i < _rawAddressBits; i++) if (_abNodes[i] == EmptyNode) throw new InvalidOperationException($"ARM1 missing a{i}_pad");
            for (int i = 0; i < _rawDataBits; i++) if (_dbNodes[i] == EmptyNode) throw new InvalidOperationException($"ARM1 missing d{i}_pad");

            var phi1 = RequireRawArm1Pad("phi1_pad");
            var phi2 = RequireRawArm1Pad("phi2_pad");
            var reset = RequireRawArm1Pad("reset_pad");
            var dbe = RequireRawArm1Pad("dbe_pad");
            var abe = RequireRawArm1Pad("abe_pad");
            var abrt = RequireRawArm1Pad("abrt_pad");
            var ale = RequireRawArm1Pad("ale_pad");
            var firq = RequireRawArm1Pad("firq_pad");
            var irq = RequireRawArm1Pad("irq_pad");

            _armPhi1Node = LookupNode("phi1_pad");
            _armPhi1High = phi1.HighGate; _armPhi1Low = phi1.LowGate;
            _armPhi2High = phi2.HighGate; _armPhi2Low = phi2.LowGate;
            _armResetHigh = reset.HighGate; _armResetLow = reset.LowGate;
            _armDbeHigh = dbe.HighGate; _armDbeLow = dbe.LowGate;
            _armAbeHigh = abe.HighGate; _armAbeLow = abe.LowGate;
            _armAbrtHigh = abrt.HighGate; _armAbrtLow = abrt.LowGate;
            _armAleHigh = ale.HighGate; _armAleLow = ale.LowGate;
            _armFirqHigh = firq.HighGate; _armFirqLow = firq.LowGate;
            _armIrqHigh = irq.HighGate; _armIrqLow = irq.LowGate;
            _pRw = LookupNode("rw_pad");
            if (_armPhi1Node == EmptyNode || _pRw == EmptyNode) throw new InvalidOperationException("ARM1 required clock or rw pad is missing");

            _armDataHighGates = AllocHandlerArray<int>(_rawDataBits);
            _armDataLowGates = AllocHandlerArray<int>(_rawDataBits);
            for (int i = 0; i < _rawDataBits; i++)
            {
                var pad = RequireRawArm1Pad("d" + i + "_pad");
                _armDataHighGates[i] = pad.HighGate;
                _armDataLowGates[i] = pad.LowGate;
            }

            // The reference initializes input pads as low before its first all-node settle. I/O pads
            // remain floated until memory performs a read.
            foreach (var pad in _rawArm1Pads!.Values)
                if (pad.Input) SetRawArm1PadDefaultLow(pad);

            RecomputeAllNodes();
            _rawHalfStep = HalfStepArm1;
        }

        private static void SetRawArm1PadDefaultLow(RawArm1PadDrive pad)
        {
            ref NodeInfo high = ref NodeInfos[pad.HighGate];
            ref NodeInfo low = ref NodeInfos[pad.LowGate];
            high.Flags &= ~(NodeFlags.SetHigh | NodeFlags.SetLow);
            low.Flags = (low.Flags & ~NodeFlags.SetLow) | NodeFlags.SetHigh;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void RawArm1Drive(int highGate, int lowGate, bool high)
        {
            bool changed;
            if (high)
            {
                changed = SetHighQueued(highGate);
                changed |= SetLowQueued(lowGate);
            }
            else
            {
                changed = SetLowQueued(highGate);
                changed |= SetHighQueued(lowGate);
            }
            if (changed) RawSettle();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void RawArm1Float(int highGate, int lowGate)
        {
            bool changed = SetLowQueued(highGate);
            changed |= SetLowQueued(lowGate);
            if (changed) RawSettle();
        }

        private static void InitRawArm1()
        {
            RawArm1Drive(_armAbrtHigh, _armAbrtLow, false);
            RawArm1Drive(_armAleHigh, _armAleLow, true);
            RawArm1Drive(_armFirqHigh, _armFirqLow, true);
            RawArm1Drive(_armIrqHigh, _armIrqLow, true);
            RawArm1Drive(_armPhi1High, _armPhi1Low, false);
            RawArm1Drive(_armPhi2High, _armPhi2Low, false);
            RawArm1Drive(_armDbeHigh, _armDbeLow, true);
            RawArm1Drive(_armAbeHigh, _armAbeLow, true);
            RawArm1Drive(_armResetHigh, _armResetLow, true);
            for (int i = 0; i < 8; i++)
            {
                RawArm1Drive(_armPhi1High, _armPhi1Low, true);
                RawArm1Drive(_armPhi1High, _armPhi1Low, false);
                RawArm1Drive(_armPhi2High, _armPhi2Low, true);
                RawArm1Drive(_armPhi2High, _armPhi2Low, false);
            }
            RawArm1Drive(_armResetHigh, _armResetLow, false);
            Time = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void HalfStepArm1()
        {
            if (NodeStates[_armPhi1Node] != 0)
            {
                RawArm1Drive(_armPhi1High, _armPhi1Low, false);
                RawArm1Drive(_armPhi2High, _armPhi2Low, true);
            }
            else
            {
                RawArm1Drive(_armPhi2High, _armPhi2Low, false);
                if (!_resetHold) RawArm1BusRead();
                RawArm1Drive(_armPhi1High, _armPhi1Low, true);
                if (!_resetHold) RawArm1BusWrite();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void RawArm1BusRead()
        {
            if (NodeStates[_pRw] == 0) RawArm1WriteDataBus(0xE1A00000u); // MOV r0,r0 (ARM NOP)
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void RawArm1BusWrite()
        {
            if (NodeStates[_pRw] == 0) return;
            RawArm1FloatDataBus();
            _ = ReadBusNodes(_dbNodes, _rawDataBits); // observable write data; the NOP sled has no backing store
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void RawArm1WriteDataBus(uint value)
        {
            bool changed = false;
            for (int i = 0; i < _rawDataBits; i++)
            {
                bool high = (value & (1u << i)) != 0;
                if (high)
                {
                    changed |= SetHighQueued(_armDataHighGates[i]);
                    changed |= SetLowQueued(_armDataLowGates[i]);
                }
                else
                {
                    changed |= SetLowQueued(_armDataHighGates[i]);
                    changed |= SetHighQueued(_armDataLowGates[i]);
                }
            }
            if (changed) RawSettle();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void RawArm1FloatDataBus()
        {
            bool changed = false;
            for (int i = 0; i < _rawDataBits; i++)
            {
                changed |= SetLowQueued(_armDataHighGates[i]);
                changed |= SetLowQueued(_armDataLowGates[i]);
            }
            if (changed) RawSettle();
        }

        // ── ARM1 signed-gate event path ─────────────────────────────────────

        private static void RecalcNodeArm1(int nn)
        {
            byte newState = ComputeNodeGroupArm1(nn);
            for (int i = 0; i < _groupCount; i++) SetNodeStateArm1(_groupBuf[i], newState);
        }

        private static byte ComputeNodeGroupArm1(int nn)
        {
            for (int i = 0; i < _groupCount; i++) _inGroup[_groupBuf[i]] = 0;
            _groupFlags = NodeFlags.None;
            _groupCount = 0;
            AddNodeToGroupArm1(nn);
            return GetNodeValueArm1();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool RawArm1GateOn(ushort packedGate)
        {
            bool activeLow = (packedGate & RawArm1InvertedGateBit) != 0;
            int gate = packedGate & ~RawArm1InvertedGateBit;
            return (NodeStates[gate] != 0) != activeLow;
        }

        private static void AddNodeToGroupArm1(int seed)
        {
            byte* inGroup = _inGroup;
            ushort* groupBuf = _groupBuf;
            byte* recalcHash = RecalcHash;
            NodeInfo* nodeInfos = NodeInfos;
            int gc = _groupCount;
            NodeFlags flags = _groupFlags;

            if (inGroup[seed] == 0)
            {
                inGroup[seed] = 1;
                groupBuf[gc++] = (ushort)seed;
                flags |= nodeInfos[seed].Flags;
            }

            int read = 0;
            while (read < gc)
            {
                int nn = groupBuf[read++];
                NodeInfo* ns = nodeInfos + nn;
                if (ns->Inline != 0)
                {
                    ushort* pay = ns->InlinePayload;
                    int n2 = ns->C1c2Count << 1;
                    for (int k = 0; k < n2; k += 2)
                    {
                        if (!RawArm1GateOn(pay[k])) continue;
                        int other = pay[k + 1];
                        if (inGroup[other] != 0) continue;
                        inGroup[other] = 1;
                        groupBuf[gc++] = (ushort)other;
                        recalcHash[other] = 0;
                        flags |= nodeInfos[other].Flags;
                    }
                    int gndEnd = n2 + ns->GndCount;
                    for (int k = n2; k < gndEnd; k++) if (RawArm1GateOn(pay[k])) { flags |= NodeFlags.Gnd; break; }
                    int pwrEnd = gndEnd + ns->PwrCount;
                    for (int k = gndEnd; k < pwrEnd; k++) if (RawArm1GateOn(pay[k])) { flags |= NodeFlags.Pwr; break; }
                }
                else
                {
                    ushort* list = TransistorList;
                    if (ns->TlistC1c2s != 0)
                    {
                        ushort* p = list + ns->TlistC1c2s;
                        while (*p != 0)
                        {
                            ushort gate = *p++;
                            int other = *p++;
                            if (!RawArm1GateOn(gate) || inGroup[other] != 0) continue;
                            inGroup[other] = 1;
                            groupBuf[gc++] = (ushort)other;
                            recalcHash[other] = 0;
                            flags |= nodeInfos[other].Flags;
                        }
                    }
                    if (ns->TlistC1gnd != 0)
                    {
                        ushort* p = list + ns->TlistC1gnd;
                        while (*p != 0) if (RawArm1GateOn(*p++)) { flags |= NodeFlags.Gnd; break; }
                    }
                    if (ns->TlistC1pwr != 0)
                    {
                        ushort* p = list + ns->TlistC1pwr;
                        while (*p != 0) if (RawArm1GateOn(*p++)) { flags |= NodeFlags.Pwr; break; }
                    }
                }
            }

            _groupCount = gc;
            _groupFlags = flags;
        }

        private static byte GetNodeValueArm1()
        {
            bool hasGnd = (_groupFlags & NodeFlags.Gnd) != 0;
            bool hasPwr = (_groupFlags & NodeFlags.Pwr) != 0;
            int flipFlop = EmptyNode;
            for (int i = 0; i < _groupCount; i++)
            {
                int nn = _groupBuf[i];
                if (RawArm1FlipFlopNodes[nn] != 0) flipFlop = nn;
            }

            // This is the exact ARM1 chipsim.js ffdefs rule: a VSS/VDD short through a marked
            // cross-coupled latch toggles that latch's previous state instead of resolving to VSS.
            if (hasGnd && hasPwr && flipFlop != EmptyNode) return (byte)(NodeStates[flipFlop] ^ 1);
            if (hasGnd) return 0;
            if (hasPwr) return 1;
            if ((_groupFlags & NodeFlags.SetHigh) != 0) return 1;
            if ((_groupFlags & NodeFlags.SetLow) != 0) return 0;
            if ((_groupFlags & NodeFlags.PullUp) != 0) return 1;
            for (int i = 0; i < _groupCount; i++) if (NodeStates[_groupBuf[i]] != 0) return 1;
            return 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SetNodeStateArm1(int nn, byte newState)
        {
            if (NodeStates[nn] == newState) return;
            NodeStates[nn] = newState;

            if (newState != 0)
            {
                RawArm1TurnOn(NodeTlistGates[nn]);
                RawArm1TurnOff(NodeTlistGatesInv[nn]);
            }
            else
            {
                RawArm1TurnOff(NodeTlistGates[nn]);
                RawArm1TurnOn(NodeTlistGatesInv[nn]);
            }
        }

        private static void RawArm1TurnOn(int tlist)
        {
            if (tlist == 0) return;
            int* nextList = RecalcListNext;
            byte* nextHash = RecalcHashNext;
            int nextCount = RecalcListNextCount;
            ushort* p = TransistorList + tlist;
            while (true)
            {
                int c1 = *p++;
                if (c1 == 0) break;
                p++; // c2: the reference schedules c1 only when a channel turns on
                if (c1 != Ngnd && c1 != Npwr && nextHash[c1] == 0) { nextList[nextCount++] = c1; nextHash[c1] = 1; }
            }
            RecalcListNextCount = nextCount;
        }

        private static void RawArm1TurnOff(int tlist)
        {
            if (tlist == 0) return;
            int* nextList = RecalcListNext;
            byte* nextHash = RecalcHashNext;
            int nextCount = RecalcListNextCount;
            ushort* p = TransistorList + tlist;
            while (true)
            {
                int c1 = *p++;
                if (c1 == 0) break;
                int c2 = *p++;
                if (c1 != Ngnd && c1 != Npwr && nextHash[c1] == 0) { nextList[nextCount++] = c1; nextHash[c1] = 1; }
                if (c2 != Ngnd && c2 != Npwr && nextHash[c2] == 0) { nextList[nextCount++] = c2; nextHash[c2] = 1; }
            }
            RecalcListNextCount = nextCount;
        }
    }
}
