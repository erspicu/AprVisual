# GPU Project MVP Skeleton: C# Parser + Evaluator

## Purpose

This skeleton is not a full implementation. It defines the structure of a first working C# project. The goal is to establish:

- parser
- graph model
- CPU reference evaluator
- small tests
- an IR that later CUDA codegen can share

## Suggested Project Layout

```text
GpuNetlistProject/
  src/
    NetlistModel/
      NetNode.cs
      NetTransistor.cs
      NetlistGraph.cs
    Parsing/
      SegdefsParser.cs
      TransdefsParser.cs
      NetlistLoader.cs
    Analysis/
      ConductionAnalyzer.cs
      FloatingIslandAnalyzer.cs
      LoopAnalyzer.cs
      StateClassifier.cs
    Eval/
      NodeState.cs
      CpuEvaluator.cs
      SettleEngine.cs
    IR/
      Expr.cs
      ExprSimplifier.cs
    CodeGen/
      VerilogEmitter.cs
      CudaEmitter.cs
    Tests/
      HandcraftedNetlists.cs
      EvaluatorTests.cs
  data/
  output/
```

## Core Model

```csharp
public sealed class NetNode
{
    public int Id { get; init; }
    public string? Name { get; set; }
    public char? PullType { get; set; }
    public bool IsPower { get; set; }
    public bool IsGround { get; set; }
    public bool IsExternalPin { get; set; }

    public List<NetTransistor> GatesControlled { get; } = new();
    public List<NetTransistor> ConnectedChannels { get; } = new();
}

public sealed class NetTransistor
{
    public int Id { get; init; }
    public NetNode Gate { get; init; } = null!;
    public NetNode Source { get; init; } = null!;
    public NetNode Drain { get; init; } = null!;
}

public sealed class NetlistGraph
{
    public Dictionary<int, NetNode> Nodes { get; } = new();
    public List<NetTransistor> Transistors { get; } = new();
}
```

## Parser Interfaces

```csharp
public interface ISegmentParser
{
    IReadOnlyList<ParsedNode> ParseNodes(string text);
}

public interface ITransistorParser
{
    IReadOnlyList<ParsedTransistor> ParseTransistors(string text);
}

public sealed record ParsedNode(int Id, string? Name, char? PullType);
public sealed record ParsedTransistor(int Id, int GateId, int SourceId, int DrainId);
```

## Loader

```csharp
public sealed class NetlistLoader
{
    public NetlistGraph Load(string segdefsPath, string transdefsPath)
    {
        // 1. parse raw files
        // 2. create nodes
        // 3. create transistors
        // 4. link references
        // 5. classify obvious power/pin nodes
        throw new NotImplementedException();
    }
}
```

## Node State

It is better not to hard-lock the state model to a single Boolean value too early. At minimum, keep both current and next state.

```csharp
public sealed class NodeState
{
    public bool[] Current { get; }
    public bool[] Next { get; }

    public NodeState(int nodeCount)
    {
        Current = new bool[nodeCount];
        Next = new bool[nodeCount];
    }

    public void Swap()
    {
        Array.Copy(Next, Current, Current.Length);
    }
}
```

If a more detailed model is needed later, this can become an enum or a bit-flag structure.

## Conduction Analyzer

The core of the CPU evaluator is not "generate Boolean expressions first." It is to answer questions like:

- which transistors are currently ON
- which nodes are connected right now
- whether a node is connected to GND
- whether a node is connected to a high source
- whether a node sits inside a floating island

```csharp
public sealed class ConductionAnalyzer
{
    public bool IsTransistorOn(NetTransistor tx, NodeState state)
    {
        return state.Current[tx.Gate.Id];
    }

    public HashSet<int> FindConnectedComponent(NetlistGraph graph, int startNodeId, NodeState state)
    {
        throw new NotImplementedException();
    }

    public bool IsConnectedToGround(NetlistGraph graph, int nodeId, NodeState state)
    {
        throw new NotImplementedException();
    }

    public bool IsConnectedToHigh(NetlistGraph graph, int nodeId, NodeState state)
    {
        throw new NotImplementedException();
    }
}
```

## Floating Islands and State Classification

```csharp
public sealed class FloatingIslandAnalyzer
{
    public bool IsFloatingIsland(NetlistGraph graph, HashSet<int> component)
    {
        throw new NotImplementedException();
    }
}

public sealed class StateClassifier
{
    public NodeBehavior Classify(NetNode node, NetlistGraph graph, NodeState state)
    {
        throw new NotImplementedException();
    }
}

public enum NodeBehavior
{
    ForcedLow,
    ForcedHigh,
    HoldPrevious,
    Unknown
}
```

## CPU Evaluator

The first evaluator can use the simplified rule:

```text
if connected_to_gnd => false
else if connected_to_high => true
else => current
```

But the code comments should explicitly state that this is a first engineering approximation.

```csharp
public sealed class CpuEvaluator
{
    private readonly ConductionAnalyzer _conduction;

    public CpuEvaluator(ConductionAnalyzer conduction)
    {
        _conduction = conduction;
    }

    public void EvaluateOnePass(NetlistGraph graph, NodeState state)
    {
        foreach (var pair in graph.Nodes)
        {
            var node = pair.Value;

            if (node.IsGround)
            {
                state.Next[node.Id] = false;
                continue;
            }

            if (node.IsPower)
            {
                state.Next[node.Id] = true;
                continue;
            }

            bool toGnd = _conduction.IsConnectedToGround(graph, node.Id, state);
            bool toHigh = _conduction.IsConnectedToHigh(graph, node.Id, state);

            state.Next[node.Id] = toGnd
                ? false
                : toHigh
                    ? true
                    : state.Current[node.Id];
        }
    }
}
```

## Settle Engine

Do not hard-code a fixed three-pass settle loop as the only strategy. It should be configurable.

```csharp
public sealed class SettleEngine
{
    public int MaxIterations { get; init; } = 16;

    public int RunUntilStable(NetlistGraph graph, NodeState state, CpuEvaluator evaluator)
    {
        for (int i = 0; i < MaxIterations; i++)
        {
            evaluator.EvaluateOnePass(graph, state);

            if (IsStable(state))
            {
                state.Swap();
                return i + 1;
            }

            state.Swap();
        }

        throw new InvalidOperationException("Settle did not converge within limit.");
    }

    private static bool IsStable(NodeState state)
    {
        for (int i = 0; i < state.Current.Length; i++)
        {
            if (state.Current[i] != state.Next[i])
            {
                return false;
            }
        }

        return true;
    }
}
```

## IR Skeleton

If the project is expected to emit CUDA or Verilog later, an intermediate representation should come first.

```csharp
public abstract record Expr;
public record ConstExpr(bool Value) : Expr;
public record NodeRefExpr(int NodeId) : Expr;
public record NotExpr(Expr Inner) : Expr;
public record AndExpr(IReadOnlyList<Expr> Terms) : Expr;
public record OrExpr(IReadOnlyList<Expr> Terms) : Expr;
public record HoldExpr(int NodeId) : Expr;
```

## Minimum Test Set

The first version should include at least:

- inverter
- NAND
- NOR
- pass transistor
- dynamic latch

For example:

```csharp
public static class HandcraftedNetlists
{
    public static NetlistGraph BuildInverter()
    {
        throw new NotImplementedException();
    }

    public static NetlistGraph BuildNand()
    {
        throw new NotImplementedException();
    }
}
```

## Things the First Version Should Not Do

- do not start with a giant CUDA kernel
- do not start with the PPU
- do not start with complex mappers
- do not treat `NOP on reset` as formal behavior
- do not reduce `PullType == '+'` to "always high"

## MVP Completion Criteria

The first acceptable milestone for this skeleton is:

1. successfully parse one real netlist
2. build the graph
3. run small handcrafted tests on the CPU
4. settle a real partial region
5. emit traces for manual comparison

Only after that is it worth writing CUDA codegen.
