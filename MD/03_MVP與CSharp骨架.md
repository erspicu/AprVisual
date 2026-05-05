# MVP 範圍與 C# 專案骨架

整理自 `Most_Valuable_MVP_to_Prioritize_en.md` 與 `GPU_Project_MVP_Skeleton_CSharp_en.md`。

---

## Part 1：什麼是「最有價值的 MVP」

### 核心原則

> 最有價值的 MVP **不是**「在 GPU 上同時跑一萬台 NES」。
> 最有價值的 MVP 是「最小但**值得信任**的 switch-level 邏輯抽取與驗證核心」。

MVP 的價值標準不是「炫」，而是：

- 它有沒有驗證核心假設？
- 它有沒有證明這條路真的走得通？
- 它有沒有為後續 CPU / CUDA / PPU 擴展打下穩固基礎？

### 推薦的 MVP 目標

> **一個對 2A03 真實小區域的 CPU 參考求值器**

範圍要嚴格控制：

- 只做 **2A03**，不做 2C02
- 只驗證**局部子區域**，不做整顆 CPU
- 不加 mapper 複雜度
- 不做整顆 GPU 常駐架構
- 不挑戰整台 NES

MVP 至少要包含：

1. parser
2. graph 模型
3. 通電分析
4. floating / hold 行為
5. 迴路 / 回授偵測
6. 收斂式 settle
7. CPU evaluator
8. trace 輸出

### 為什麼這就是最有價值的 MVP

因為它直接驗證專案的靈魂：

> **switch-level 網表能不能被翻譯成值得信任的可執行邏輯模型？**

如果這一步不行，後面 GPU 加速、bit-slicing、PPU 支援、整機架構通通沒有根基。
如果這一步行，後面就只是擴張與優化，不再是空想。

### 推薦的 MVP 三階段

#### Stage 1：手刻迷你網表

先做：

- inverter
- NAND
- NOR
- pass transistor
- dynamic latch

用來驗證 parser / graph / evaluator 的基本邏輯。

#### Stage 2：真實 2A03 局部區域

挑一個小但有意義的真實區域，例如：

- reset chain
- 一個 register bit
- ALU carry path 一段

驗證：

- 真實資料載得進來
- 通電 / hold / settle 行為都對
- 能輸出穩定的 trace

#### Stage 3：CPU evaluator + 簡單 codegen

- emit trace
- emit 小型 IR
- 可選：emit 簡單 C++ 表達式

到這個階段，往 CUDA 走才開始有意義。

### MVP 完成標準

當系統能做到下面六件事，MVP 就算完成：

1. 載入真實 2A03 局部網表
2. 建出 graph
3. 對給定 phase 做通電與狀態更新
4. 收斂式 settle
5. 輸出值得信任的 trace
6. 在手刻測試與真實局部區域之間互相對照

達到這一點就已經非常有價值，可以可信地宣稱「switch-level → 可執行邏輯」這條路在這個專案內已經被示範過。

### 不該被當成第一個 MVP 的東西

- 整台 NES 系統
- 整顆 PPU
- 複雜 mapper
- 整顆 GPU 常駐架構
- AI 訓練大規模吞吐

這些都屬於 MVP 之**後**。

---

## Part 2：C# 專案骨架

### 建議的目錄結構

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

### 核心資料模型

```csharp
public sealed class NetNode
{
    public int Id { get; init; }
    public string? Name { get; set; }
    public char? PullType { get; set; }
    public bool IsPower { get; set; }
    public bool IsGround { get; set; }
    public bool IsExternalPin { get; set; }

    public List<NetTransistor> GatesControlled  { get; } = new();
    public List<NetTransistor> ConnectedChannels { get; } = new();
}

public sealed class NetTransistor
{
    public int Id { get; init; }
    public NetNode Gate   { get; init; } = null!;
    public NetNode Source { get; init; } = null!;
    public NetNode Drain  { get; init; } = null!;
}

public sealed class NetlistGraph
{
    public Dictionary<int, NetNode> Nodes { get; } = new();
    public List<NetTransistor>      Transistors { get; } = new();
}
```

### Parser 介面

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

### Loader

```csharp
public sealed class NetlistLoader
{
    public NetlistGraph Load(string segdefsPath, string transdefsPath)
    {
        // 1. parse 原始檔案
        // 2. 建立 nodes
        // 3. 建立 transistors
        // 4. 連結引用
        // 5. 標記明顯的 power / pin
        throw new NotImplementedException();
    }
}
```

### 節點狀態

不要太早把 state 鎖死成單一 bool。至少分 Current / Next：

```csharp
public sealed class NodeState
{
    public bool[] Current { get; }
    public bool[] Next    { get; }

    public NodeState(int nodeCount)
    {
        Current = new bool[nodeCount];
        Next    = new bool[nodeCount];
    }

    public void Swap()
    {
        Array.Copy(Next, Current, Current.Length);
    }
}
```

未來若需要更細的模型（unknown / floating / strong-low / weak-high…），這裡可以升級成 enum 或 bit-flag。

### 通電分析器

CPU evaluator 的核心**不是**「先生 boolean 表達式」，而是回答這幾個問題：

- 哪些電晶體目前 ON？
- 哪些節點目前連通？
- 某節點是否連到 GND？
- 某節點是否連到高源？
- 某節點是否在浮島中？

```csharp
public sealed class ConductionAnalyzer
{
    public bool IsTransistorOn(NetTransistor tx, NodeState state)
        => state.Current[tx.Gate.Id];

    public HashSet<int> FindConnectedComponent(
        NetlistGraph graph, int startNodeId, NodeState state) => throw new NotImplementedException();

    public bool IsConnectedToGround(NetlistGraph g, int nodeId, NodeState s) => throw new NotImplementedException();
    public bool IsConnectedToHigh  (NetlistGraph g, int nodeId, NodeState s) => throw new NotImplementedException();
}
```

### 浮島與狀態分類

```csharp
public sealed class FloatingIslandAnalyzer
{
    public bool IsFloatingIsland(NetlistGraph g, HashSet<int> component)
        => throw new NotImplementedException();
}

public enum NodeBehavior { ForcedLow, ForcedHigh, HoldPrevious, Unknown }

public sealed class StateClassifier
{
    public NodeBehavior Classify(NetNode node, NetlistGraph g, NodeState s)
        => throw new NotImplementedException();
}
```

### CPU 求值器（第一版）

第一版用簡化規則，**註解上明寫這是工程近似**：

```csharp
public sealed class CpuEvaluator
{
    private readonly ConductionAnalyzer _conduction;
    public CpuEvaluator(ConductionAnalyzer c) { _conduction = c; }

    public void EvaluateOnePass(NetlistGraph graph, NodeState state)
    {
        foreach (var pair in graph.Nodes)
        {
            var node = pair.Value;
            if (node.IsGround) { state.Next[node.Id] = false; continue; }
            if (node.IsPower)  { state.Next[node.Id] = true;  continue; }

            bool toGnd  = _conduction.IsConnectedToGround(graph, node.Id, state);
            bool toHigh = _conduction.IsConnectedToHigh  (graph, node.Id, state);

            // 第一版規則：GND 優先 → high → 否則保留前狀態
            state.Next[node.Id] = toGnd ? false
                                : toHigh ? true
                                : state.Current[node.Id];
        }
    }
}
```

### Settle Engine

不要把「跑 3 次」寫死，要可設定：

```csharp
public sealed class SettleEngine
{
    public int MaxIterations { get; init; } = 16;

    public int RunUntilStable(NetlistGraph g, NodeState s, CpuEvaluator e)
    {
        for (int i = 0; i < MaxIterations; i++)
        {
            e.EvaluateOnePass(g, s);
            if (IsStable(s)) { s.Swap(); return i + 1; }
            s.Swap();
        }
        throw new InvalidOperationException("Settle did not converge.");
    }

    private static bool IsStable(NodeState s)
    {
        for (int i = 0; i < s.Current.Length; i++)
            if (s.Current[i] != s.Next[i]) return false;
        return true;
    }
}
```

### IR 骨架

未來要 emit CUDA / Verilog 之前，IR 一定要先存在：

```csharp
public abstract record Expr;
public record ConstExpr  (bool Value)             : Expr;
public record NodeRefExpr(int NodeId)             : Expr;
public record NotExpr    (Expr Inner)             : Expr;
public record AndExpr    (IReadOnlyList<Expr> T)  : Expr;
public record OrExpr     (IReadOnlyList<Expr> T)  : Expr;
public record HoldExpr   (int NodeId)             : Expr;
```

### 最少測試集

第一版至少要有：

- inverter
- NAND
- NOR
- pass transistor
- dynamic latch

```csharp
public static class HandcraftedNetlists
{
    public static NetlistGraph BuildInverter() => throw new NotImplementedException();
    public static NetlistGraph BuildNand    () => throw new NotImplementedException();
    // ...
}
```

### 第一版**不該**做的事

- 不要一開始就寫巨型 CUDA kernel
- 不要從 PPU 開始
- 不要碰複雜 mapper
- 不要把「`NOP on reset`」當成正式行為
- 不要把「`PullType == '+'`」簡化成「永遠是 1」

### 骨架的 MVP 完成標準

第一個可接受里程碑：

1. 成功 parse 一份真實網表
2. 建出 graph
3. 在 CPU 上跑通手刻測試
4. 對真實局部區域做 settle
5. emit trace 供人工對照

**做到這一步之後，CUDA codegen 才值得寫。**
