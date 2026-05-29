# Gemini 推薦優化清單 — 2026-05-29

來源:`gemini_reply_2026-05-29.md`(對 6 個非 IR 問題的詳細回應)

實施順序(依 Gemini 推薦,風險 / ROI 排序):

1. **Q5 Hot/Cold method 分離**(零風險、+2-6% 預估)── C# 先,後 Rust
2. **Q6 lastGate scalar cache + transistor_list 排序**(中風險、+3-8% 預估)
3. **Q3 Clock-phase static wave 0 memoization**(中高風險、+15-30% 預估)

**測試流程**(每項):
- C# 5-run 200k hc bench-hc,top-3 mean(drop lower 2)
- 進步 → 採用 → commit & push → 移至 Rust 端
- 退步 → revert → 紀錄為 dead-end → 跳下一個
- checksum 必須 5/5 對 `0x9B103E5E206E4C37`
- selftest ALL PASS

**已知天花板**(Gemini 評估):
> 即便這 3 項全數實作達最佳 ROI,效能可能提升 50%-80%,達到 100-120K hc/s。 距離 42.95M hc/s 仍有 ~350× 差距,在純 event-driven BFS 框架下難以跨越。

---

## Q5. Hot/Cold method 分離(NoInlining 冷路徑)

### 設計
釋放 hot path 的 inline budget + 降低 L1i cache 壓力 + 避免 register spill。

**目標 cold path**:
- `GetNodeValue` 內 floating tie-break 線性掃描(<1% 觸發率)

### 預估
- ROI: **+2% ~ +6%**(Gemini 評)
- 風險:**極低**(現代高效能 C# / Rust 標準操作)
- 撞 dead-end 機率:0(與過度 inline 失敗剛好互補)

### C# 改動
```csharp
[MethodImpl(MethodImplOptions.AggressiveInlining)]
private static byte GetNodeValue()
{
    if (_groupFlags != NodeFlags.None) return FlagsToState[(int)_groupFlags];
    return GetNodeValueFloatingCold();
}

[MethodImpl(MethodImplOptions.NoInlining)]
private static byte GetNodeValueFloatingCold() { /* linear scan */ }
```

### Rust 改動
```rust
#[inline(always)]
fn compute_node_group(&mut self, nn: i32) -> u8 {
    // ... BFS walk ...
    let f = self.group_flags;
    if f != 0 { return *self.flags_to_state.get_unchecked(f as usize); }
    self.compute_node_group_floating_cold()
}

#[cold]
#[inline(never)]
fn compute_node_group_floating_cold(&self) -> u8 { /* linear scan */ }
```

### 狀態
- ☒ **C# 退回 -1.53%** (2026-05-29)
  - baseline top-3 mean: 64,706 hc/s
  - Q5 round 1 top-3 mean: 63,482 hc/s
  - Q5 round 2 top-3 mean: 63,950 hc/s
  - 兩輪平均: 63,716 → **Δ -1.53%(超出 noise band)**
  - checksum 5/5 `0x9B103E5E206E4C37`
  - **root cause**:.NET JIT 對 `if (_groupFlags != None)` 條件下的 cold 路徑已自動 dead-code-eliminate(當分支不取時整段內聯代碼被優化掉),強制 `NoInlining` 反而引入真實 function call overhead(args setup、stack frame、ret)。 Gemini 的「inline budget 釋放」理論在這架構上不成立,因為 cold 代碼本來就不會被生成到 hot path
  - **教訓**:**.NET JIT 已會根據 branch hint 把 cold 分支推到尾端**;Gemini 預估 +2-6% 在已最佳化的 hot path 上不適用
- ☒ **Rust 退回 -1.70%** (2026-05-29)
  - baseline top-3 mean: 69,647 hc/s
  - Rust Q5 top-3 mean: 68,466 hc/s
  - **Δ -1.70%**,checksum 5/5
  - `#[cold] #[inline(never)]` 加在 `compute_node_group_floating_cold` 上
  - LLVM 跟 .NET JIT 同樣已自動處理 cold path 佈局,強制標記反而引入真實 function call
  - **跨平台一致**:Q5「Hot/Cold split」在兩個現代編譯器上都是 -1.5~1.7% 退步

---

## Q6. lastGate scalar cache + transistor_list 排序

### 設計
不改 transistor_list 結構(維持 0-terminated `ushort` 序列),但:
1. **離線**:對 `transistor_list` segments 依 `gate_id` 排序(同 gate 的 transistor 相鄰)
2. **Runtime**:在 SetNodeState / BFS fanout loop 加 `lastGateId` scalar 快取,連續同 gate 直接命中暫存器,跳過 `NodeStates[gate]` array load

### 預估
- ROI: **+3% ~ +8%**(Gemini 評)
- 風險:中(排序前提:需確認排序不改 BFS 結果;`lastGateId` cmp 分支可能 wash)
- 撞 dead-end:**注意** ── 過去 cache 相關改動(RCM、generation counter)失敗,但此項只動排序而非 layout,且 `lastGateId` 是純 scalar 暫存器操作

### 候選位置
1. `SetNodeState` 的 `tlist_gates` fanout loop(per gate-driven node)── 最熱
2. `AddNodeToGroup` 的 `TlistC1c2s` BFS walk ── 次熱
3. `RecalcNodeFast` 的 `TlistC1gnd` / `TlistC1pwr` scan ── 較冷

### 狀態
- ☒ **C# 退回 -1.68%** (2026-05-29)
  - baseline top-3 mean: 64,706 hc/s
  - Q6 round 1 top-3 mean: 63,310 hc/s
  - Q6 round 2 top-3 mean: 63,931 hc/s
  - 兩輪平均: 63,621 → **Δ -1.68%**
  - checksum 5/5 `0x9B103E5E206E4C37`
  - 實作:
    1. 離線:對 `c1c2Pairs` 依 `gate` 排序後再 flatten
    2. Runtime:`AddNodeToGroup` BFS walk 內加 `lastGate` / `lastState` scalar cache
  - **root cause**:NMOS 網絡上**同一 gate 控制多 transistor 連到同一 node 的情況罕見**
    - `TlistC1c2s` 每 entry 是 (gate, other),gate 是其他節點
    - 為了 hit cache,需要同 gate 在同 nn 的 c1c2 list 中重複出現多次
    - 但 NMOS 通常 1 transistor / 1 (gate, other) 對,重複率低
    - lastGate cmp 變成純 overhead,不能 amortize
  - 教訓:**Gemini 的「同 gate 重複」假設**對 NES 2A03/2C02 NMOS 拓樸**不成立**。 需先量化 gate 重複分佈再評估
- ☒ **Rust 退回 -5.36%(更大幅退步)** (2026-05-29)
  - baseline top-3 mean: 69,647 hc/s
  - Rust Q6 top-3 mean: 65,917 hc/s
  - **Δ -5.36%**,checksum 5/5
  - 同 C# 實作:from_snapshot 時對每個 TlistC1c2s segment 依 gate id 排序 + BFS walk 加 lastGate cache
  - Rust 退步比 C# 更大可能因 `if gate == last_gate` 的 conditional 在 LLVM 上產生更差的 codegen(branch + select 都需 setup)
  - **跨平台確認**:Q6 lastGate cache 在兩個編譯器上都失敗,主因 NMOS 同 gate 重複罕見

---

## Q3. Clock-phase static wave 0 memoization

### 設計
NES clock 每 12 master cycle toggle 一次。 toggle 後**第一波**(只依 clock fanout subnet)的 BFS propagation 是**確定性的**。

**離線**:
1. Reset() 後手動 toggle `clk`,記錄前 1-2 個 wave 內所有「只依 clk」改變的 node ID + final state
2. 識別第 2 wave 的 dirty boundary(那些被 wave 1 改變後 fanout 到的 node)
3. 存成查表:`ClkRise_Updates[(nodeId, state)..]` + `ClkRise_DirtyBoundary[nodeId..]`(下降同)

**Runtime**:
- Master half-cycle 觸發 clock 時,跳過 BFS 第一波
- 直接寫 `Clk_Updates` 到 NodeStates
- 將 dirty boundary 塞進 Queue 作為 wave 1 起點

### 預估
- ROI: **+15% ~ +30%**(Gemini 評,模擬器中 30-40% 時間花在 clock 推動)
- 風險:**中高** ── 邊界定義錯誤即 checksum 破壞
- 撞 dead-end:**重要校驗** ── 必須證明被 memoize 的子圖**絕對沒有**其他非 clock 動態輸入(internal latch 回授等)

### 安全條件
- 取樣多種 ROM 狀態驗證 memoize 集穩定
- 不只 power-on,test 中間狀態的 clock toggle
- 對比有 / 無 memoize 的 checksum 必須完全相同

### 狀態
- ☒ **C# 不實作** (2026-05-29 ── 用 instrumentation 量出 ROI 上限 < 1%)
  - 加 `ClkInProgress` 標記 + RecalcNode 分類計數 + 第一波 size 計數,跑 200k hc bench
  - 結果(full_palette.nes, 200k hc):
    - clk toggles: **200,192**(每 hc 一次)
    - clk-induced RecalcNode: **121,042,834**
    - 總 RecalcNode: 121,077,589
    - **Clock 工作量佔比: 100.0%**(non-clk 只有 5 calls 來自 memory handler)
    - avg first-wave size: **1.1 nodes**(只是 clk 本身)
    - avg settle passes per clk toggle: **12.2**
    - avg nodes per clk-induced ProcessQueue: ~605
  - **ROI 上限計算**:Wave 0 memoize 只能省 1/605 ≈ **0.17% per toggle**,即 <1% bench
  - **Gemini 預估 +15-30% 完全不可能**(off by 2 orders of magnitude)
  - 要達到 +15-30% 需 memoize 整個 settle 12 個 pass,但這需要證明「clock 推動的完整結算 data-independent」,跟 Direction C 同樣失敗模式(latch/bus/register 狀態會影響)
  - 教訓:**「第一波 / Wave 0」在這個 settle 結構下是 outermost 1-2 node,不是 Gemini 想像的「大量 clock 樹節點」**。 NES 的 clock 樹很淺(剛 toggle 完 clk node 還沒擴散開),BFS 主要工作在後面 11 個 pass
- ☐ Rust 不實作(C# 已證明 Wave 0 work share <1%)

---

---

## Direction C: Iso-state culling 安全變體(round 2 試) ── **退回 checksum 破壞**

### 設計
在 `AddNodeToGroup` BFS walk 加 safeguard:
```csharp
if (NodeStates[gate] != 0) {
    ref NodeInfo otherNs = ref NodeInfos[other];
    if (NodeStates[other] == nnState
        && otherNs.Flags == NodeFlags.None
        && otherNs.TlistC1gnd == 0
        && otherNs.TlistC1pwr == 0) continue;
    AddNodeOrApplyDriver(other);
}
```

### 結果 (2026-05-29)
- baseline checksum: `0x9B103E5E206E4C37`
- Direction C checksum: **`0x475163D804CF3824`** ── **模擬發散**!
- 速度 +4-5%(66-68K vs 64.9K),但**結果不正確**

### Root cause
即使 `other` 自己沒有 GND/PWR 通道且 Flags=None,**`other` 的 TlistC1c2s 走訪可能拉入更下游的 GND/PWR**。 跳過 `other` 等同跳過整個 transitive sub-walk,group resolve 用了錯誤的 group_flags。

要安全的話需要證明 `other` 的整個下游 sub-tree 都不貢獻 GND/PWR/Flags ── 需要昂貴的 transitive closure 預計算 + runtime 檢查。

### 教訓
**Gemini Direction C「iso-state culling」如其所述是不安全的**。 即使加 leaf-like safeguard(Flags=None + 無 GND/PWR 直接通道),仍因 transitive walk 而破壞 BFS 結果。 加入 dead-end 紀錄:**「在 BFS 走訪中 skip 任一節點都必須證明其整個下游 sub-tree 不影響 group_flags」── 此條件運行時檢查成本高於 skip 收益**。

7 個 dead-end 中第 7 個:**culling-style skip 共通失敗模式**(類似 `dead-end-skip-dead-end` memory)。

---

---

## L8 ── Lowering 階段 stub 移除嘗試(2026-05-29 post-batch) ── **失敗:CPU 翻車**

### 設計
在 `LowerNetlist` **之後 + handler attach 之後 + ResolveCachedNodes 之前** 插入新 pass:
- 找符合條件 stub:`C1c2s.Count == 1` AND `Gates.Count == 0` AND `Callback == null` AND `Pullups == 0` AND 該 transistor 對端非 Vcc/Vss
- Cascade fixpoint:移除一輪後 reset alive count,再掃,直到無變化
- Rebuild node table + transistor list + name maps + forceCompute + LastLowerRemap

### 量測結果

**第一次嘗試(L8 in LowerNetlist,handler attach 之前)── 直接失敗**:
- 57 nodes 被移除,但其中包含 `ppu.cs`、`cart.chr.cs` 等 handler 將會 lookup 的 node
- handler attach 報錯:`memory handler 'u4.': missing cs/a[]/_d[7:0]`
- 修正:把 L8 移到 handler attach 之後跑(這時 watched node 因為 AddCallback 的 fake transistor 有 Gates fanout,L8 會跳過它們)

**第二次嘗試(L8 in LoadSystem,handler attach 之後)── 跑得通但破壞行為**:
- 49 stub nodes 移除,2 個 cascade pass 收斂
- handler attach 成功(沒報錯)
- **selftest 5 個單元電路 ALL PASS**(誤導性 ── 單元電路不會觸發 floating tie-break)
- 跑 10 frame screenshot 後 CPU state:

| | L8 ON | L8 OFF (baseline) |
|---|---|---|
| frame 10 PC | 3638 | **8052** |
| S(stack ptr) | 3E | **FF** |
| IR | 92(illegal) | **AD** (LDA absolute) |
| AB | FFFF(未初始化) | **8052** (有效 PRG) |
| PNG md5 | `9f33...` | `5c1d...` |

CPU 跑進完全錯誤的指令流(IR=92 是非法 opcode,PC=3638 不是 reset vector ~ PRG 區)── 7M half-cycle 累積發散到 CPU bus state 翻車。

### Root cause
`GetNodeValue` 內 floating tie-break(`_groupFlags == None` 時):
```csharp
for (int i = 0; i < _groupCount; i++) {
    int conn = NodeConnections[nn];
    if (conn > maxConn) { maxState = NodeStates[nn]; maxConn = conn; }
}
```
- 走 group 內所有 node,挑 `NodeConnections` 最高的拿 state
- 移除 stub 改變了 group 成員集合 → tie-break 結果在「兩個 node 都是 conn=1」的競爭中改變
- 雖然 floating 是 <1% cold path,但 NES 內 CPU bus state 對這類細微差異敏感
- 200K hc 內累積發散到 CPU 完全翻車

### 教訓 ── 加入第 8 個 dead-end pattern

**「stub 看似 unobservable 但實際參與 floating tie-break,移除會在 cold path 製造 silent divergence」**:
- group_flags=None 時 GetNodeValue 走 floating tie-break(`<1%` 頻率但 NES CPU bus 對此敏感)
- 任何改變 group 成員 NodeConnections 集合的 lowering 都會破壞 tie-break
- **跟 memory `dead-end-skip-dead-end` 同類失敗模式**(skip 後 BFS 結果改變,累積放大成 bug)

### 修復條件(若日後想再試)
- L8 必須額外保證:對任何包含 stub 的 group walk,floating tie-break 結果不變
- 等價:stub 在其潛在 group 內 NodeConnections **絕不會是最大值**
- 證明此條件需要 transitive 分析 group 可能組合,實作工序 > 收益

### 狀態
- ☒ **C# 完全失敗 + 刪除 dead code** (2026-05-29)
- ☐ Rust 不嘗試(C# 已證明設計失敗)
- 確認測試參數 `EnableL8Stubs` + `--no-l8` CLI 已從 source 刪除(沒留下 hot path 開關)

---

## A/B 驗證 checklist(每項)

```
[ ] C# 5-run sequential bench-hc 200000 (top-3 mean)
[ ] checksum 5/5 == 0x9B103E5E206E4C37
[ ] selftest ALL PASS
[ ] 進步 ≥ 1%(超過雜訊地板)→ 採用,commit & push
[ ] 進步 < 0.5%(雜訊內)→ 暫不採用,記入 noise-level
[ ] 退步 → revert,記入 dead-end
[ ] Rust 同改動 5-run + checksum 驗證
[ ] 兩端記入 LIST 此檔
```
