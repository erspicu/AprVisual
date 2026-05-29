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
- ☐ C# 待測
- ☐ Rust 待測

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
- ☐ C# 待測
- ☐ Rust 待測

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
- ☐ C# 待測(複雜,留最後)
- ☐ Rust 待測

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
