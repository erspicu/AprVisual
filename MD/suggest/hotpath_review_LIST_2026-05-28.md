# Hotpath 進階優化建議 LIST (整合版)

日期: 2026-05-28
範圍: `src/AprVisual.S1/Sim/*.cs` (S1 fork, diag 全清後的狀態)
來源:
- A. 外部 reviewer 4 點提案 (本檔內附驗證)
- B. `Sim_hotpath_efficiency_suggestions_2026-05-28.md` 詳細分析(同目錄)

## 建議清單 (合併兩來源,標明出處)

### P0 ── 在最熱路徑、收益高

- [x] **#01 移除 `GetNodeValue` 內 `ForceCompute` 冗餘 bitwise mask** (來源 A2) ── **採用**
  - 位置: `Group.cs` `GetNodeValue` (line 142-147)
  - 改動: 刪除 `if (ForceCompute && Gnd && Pwr) flags &= ~Gnd; flags &= ~Pwr;` 區塊
  - 理由: `BuildFlagsToStateTable` 啟動時對 256 個 flag 跑過 `FlagsToStateOf`,該函式逐字做了同樣的 mask。 LUT 已預先處理,這段是 dead code
  - 安全性: `_groupFlags` 之後只被 `& HasCallback` 讀,不被 Gnd/Pwr mask 影響
  - 預估收益: 0.3-0.7% (低,但 0 風險)
  - 風險: 0
  - **實測 (2026-05-28, 5-run median)**:
    - BEFORE: 55,147 hc/s
    - AFTER:  55,863 hc/s
    - Δ:      **+1.30%** (median),+1.04% (avg) ── 略高於預估
    - checksum 5/5 `0x9B103E5E206E4C37`,selftest ALL PASS

- [x] **#02 延遲讀取 `NodeConnections`** (來源 A1 + B P0) ── **採用,大進步**
  - 位置: `Group.cs` `AddNodeOrApplyDriver` (line 133-134)
  - 改動: 不在每節點訪問都讀 `NodeConnections[nn]`,改為僅在 `GetNodeValue()` 偵測到 `_groupFlags == None`(浮接)時對 `_groupBuf` 做一次線性掃描求最大電容
  - 理由: `NodeConnections` 只在浮接 tie-break 用,程式碼註解 `// cold — only tie-break` 但每訪問都讀。 浮接 group <1%
  - 兩個 reviewer 獨立給出同一建議
  - 預估收益: 1-3% (中等信心)
  - **實測 (2026-05-28)**:
    - BEFORE 5-run median: 54,816 hc/s
    - AFTER 10-run median: **61,560 hc/s**
    - Δ: **+12.30%** ── 遠超預估
    - 原因解析:NodeConnections 是 58KB 獨立陣列,被砍掉後整個 cache line 系列可以保持冷,而不是被 BFS 每 30-60M 次訪問拉熱
    - checksum 5/5 `0x9B103E5E206E4C37`,selftest ALL PASS
  - 額外: `_maxState` / `_maxConnections` static field 移除,改為 GetNodeValue 內 local 變數

- [x] **#03 BFS 內 GND/PWR scan 已找到後跳過同類** (來源 B P0) ── **revert(無效益)**
  - 位置: `Group.cs` `AddNodeToGroup` (line 111-120)
  - 改動: 在掃描 `TlistC1gnd` / `TlistC1pwr` 前先檢查 `_groupFlags` 是否已有對應 bit
  - 理由: 一個 group 內第一次找到 GND 即可,再找到第二個 GND 不會改變 `_groupFlags`(OR 是 idempotent)
  - 預估收益: 中
  - **實測 (2026-05-28, 10-run)**:
    - BEFORE 5-run median: 57,655 hc/s
    - AFTER 10-run median: 58,009 hc/s
    - Δ: +0.61% median, **-0.13% avg** ── 在雜訊內,無明確收益
    - checksum 全 `0x9B103E5E206E4C37`,行為正確
  - **分析**:預估「對大型 bus group 較明顯」,實際多數 group 只接觸一個 GND/PWR source,skip 鮮少觸發;但每訪問都多付 1 個 bit-test。 cost ≈ benefit
  - 狀態: **revert** ── 改動雖然語意正確但實證無收益,移除避免無謂複雜度

- [ ] **#04 `SetNodeState` inline enqueue,跳過 c1 supply check** (來源 B P0)
  - 位置: `Recalc.cs` `SetNodeState` (line 127-143) + `EnqueueNode` (line 45-54)
  - 改動: 在 `SetNodeState` 開頭把 `RecalcListNext`/`RecalcHashNext`/`RecalcListNextCount` 複製到 local,然後對 `c1` 直接 inline 三行 enqueue,跳過 `EnqueueNode` 內的 supply check;對 `c2` 只在 `newState == 0` 且非 supply 時 inline
  - 理由: `AddTransistor` (Module.cs:125) `if (IsPwrGnd(c1)) (c1, c2) = (c2, c1);` 已把 supply 正規化到 c2。 c1 永遠不是 Npwr/Ngnd,EnqueueNode 內的 supply check 對 c1 是 dead branch
  - `EnqueueNode` 已 `[AggressiveInlining]`,但實際 inline 後的 supply check 是否被 constant-fold 不確定 ── 顯式跳過更穩
  - 預估收益: 高(這是每節點翻轉的 fanout 擴散點)
  - 風險: 低到中 ── 要確認 c1 invariant,可加 Debug.Assert
  - 狀態: 待實驗

### P1 ── 中等熱路徑或常用路徑

- [ ] **#05 Callback 換成真正的 pending queue** (來源 B P1)
  - 位置: `Handlers.cs` `InvokeCallbacks` (line 68-82)
  - 改動: 目前 InvokeCallbacks 先 foreach 整個 `_callbacks` 確認是否有 pending,再 for-loop 執行 ── **兩次掃描**。 改成 `EnqueueCallback` 時 push 到 pending queue,InvokeCallbacks 0 時 O(1) return
  - 對比: Rust S1 的 `pending_handlers` Vec 已是這個模式;C# 端仍在用舊式 scan
  - 注意: callback 可能 re-entrant(callback 內 WriteBits → ProcessQueue → InvokeCallbacks),需小測試鎖住
  - 預估收益: 中(每 settle 後固定成本,common case = 0 pending)
  - 風險: 中(re-entrant 行為)
  - 狀態: 待實驗

- [ ] **#06 `ReadBits` / `WriteBits` 加 `int[]` / `ReadOnlySpan<int>` overload** (來源 B P1)
  - 位置: `Handlers.cs` (line 110-131, 181-188, 229-240)
  - 改動: 目前 `IReadOnlyList<int>` 介面呼叫成本(Count + indexer 走 vtable);改成接受 `int[]` 或 `ReadOnlySpan<int>`。 handler attach 時把 bus nodes 固定成 array
  - 影響: memory / video callback 每次都走這條
  - 預估收益: 中
  - 風險: 低
  - 狀態: 待實驗

- [ ] **#07 Clock handler 走直接 fast path** (來源 B P1)
  - 位置: `Handlers.cs` `AttachClockHandler` (line 145-150) + `Recalc.cs` `StepCycle` (line 193-196)
  - 改動: clock 不走 `_handlerChain` (multicast delegate),在 `AttachClockHandler` 把 clk node id 記到 `_clockNode`;`StepCycle` 直接:
    ```csharp
    if (NodeStates[clk] != 0) SetLow(clk); else SetHigh(clk);
    ```
  - 理由: 每 half-cycle 都會 toggle clock,multicast delegate invocation 是固定成本
  - 預估收益: 中
  - 風險: 低(注意 reset/LoadSystem 生命週期)
  - 狀態: 待實驗

- [ ] **#08 `RunFrame()` 不要在 loop 內呼叫 `Step(1)`** (來源 B P1)
  - 位置: `System.cs` `RunFrame` (line 186-189)
  - 改動: 直接呼叫 `StepCycle()` 而不是 `Step(1)`,省一層 for-loop
  - 預估收益: 小到中(test/RunFrame 常用)
  - 風險: 低
  - 狀態: 待實驗

### P2 / P3 ── **不採納**

- [x] **#3' RecalcNodeFast 微調** (來源 A3)
  - `!= 0` vs `== 1`: C# JIT 對 byte* 編譯到相同 `test+jne`,無差別
  - bool flag 抽出 loop: 原本 `f |= Gnd; break;` 最多一次 OR 然後跳出,沒可省空間
  - reviewer 自承「極度 Micro-optimization」
  - 結論: 跳過

- [x] **#4' Cache Line 對齊** (來源 A4)
  - 已是 `NativeMemory.AlignedAlloc(byteCount, 64)` (Native.cs:18)
  - 結論: 已完成,不用動

- [ ] **#P2' `IsPureLogic` 合進 `NodeInfo` padding** (來源 B P2)
  - 加 `FastKind` byte 利用 padding,RecalcNode 走 dispatch
  - 預估: 小到中,風險中
  - 結論: 收益不確定且 struct layout 變動,暫不做

- [ ] **#P2' 擴充 lowering: normal-to-supply short** (來源 B P2)
  - 合併 always-on 接 supply 的 normal node
  - 預估: 中,風險中到高
  - 結論: 複雜度高,先收集 lowering 統計再決定

- [ ] **#P2' `_inGroup` epoch stamp** (來源 B P2)
  - byte → ushort/int seen array
  - reviewer 自承「不確定」
  - 結論: 跳過,group 通常很小,epoch 開銷反而可能大過 clear loop

- [ ] **#P3' Headless benchmark 不 attach video handler** (來源 B P3)
  - 是 benchmark-mode 拆分,不是一般優化
  - 風險: 數字混用
  - 結論: 不採納

- [ ] **#P3' Reset flattening 直接產生 ushort buffer** (來源 B P3)
  - load-time,非熱路徑
  - 結論: 跳過

## 進入熱路徑前的警告

本 codebase 已累積 **6 個失敗的 dead-end** (見 memory):

| Dead-end | 預測 vs 實測 |
|---|---|
| Counter FastPath | 預期 -3~-10%,實測 -6% |
| dead-end-skip | 預期省 38% 工作,實測破壞 CPU bus |
| bit-parallel BFS | 算法正確,156× 慢 |
| per-chip parallel | 預期 +N 倍,實測 15× 慢 |
| Prune-Merge | 早期 +1.37×,inline cascade 後 -5% |
| LUT-TTL | 早期 0%,inline cascade 後 -2% |

共通模式:**「看起來很合理」的 hot-path 改動,經常因 JIT inline cascade 被破壞 / set_node_state 內 inner loop 被無聲影響 → 負效益**。

本次 P0 建議 (#01-#04) 性質:
- **#01** ── 純死碼移除,0 風險
- **#02 #03** ── 「**從熱路徑搬出工作**」,跟過去 dead-end 的「往熱路徑塞工作」模式相反,方向安全
- **#04** ── 跳過冗餘 supply check,屬於「條件邊界正規化」,風險中等

預測 % 仍不可信,**強制 A/B benchmark 才能採納**。

## 建議的行動順序

1. **先做 #01** ── 0 風險的死碼移除,單獨 commit 確認 checksum 不變
2. **#02 #03** ── 一發 commit 兩項 (BFS 同檔同函式),5-10 run A/B
3. **#04** ── 單獨 commit,SetNodeState 是高頻路徑要單獨量測
4. **P1 系列** ── #05 #06 #07 #08 ── 視 P0 結果決定是否值得繼續
5. 任何步驟若實測負效益:revert,寫入 memory 為新 dead-end

## 驗收條件 (每步必須)

- `--bench-hc 200000` checksum 必須維持 `0x9B103E5E206E4C37`
- `--selftest` ALL PASS
- 5-run avg hc/s 不低於改動前(若低於 ── revert,寫入 dead-end memory)

## Cross-port 同步

Rust S1 (`experiment/rust-s1/`) 結構相同,可同步套用的:
- #01: 無 (Rust 從 snapshot 載入,FlagsToState 已是預計算 LUT,且 Rust 端沒有 GetNodeValue 內的 mask code ── 已乾淨)
- #02: 是 (rust-poc 也有同樣的 NodeConnections 結構)
- #03: 是 (BFS 邏輯一致)
- #04: 是 (set_node_state 邏輯一致)
- #05: 不適用 (Rust 已是 pending_handlers Vec 模式)
- #06: 不適用 (Rust 用 `&[i32]` slice,不是 trait object)
- #07: 是 (clock 在 step_cycle 內,可直接 toggle)
- #08: 是 (run_frame 也有同樣模式)

建議:先 C# 端確認方向,確認正面後同步 Rust 端。
