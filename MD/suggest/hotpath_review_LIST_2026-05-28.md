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

- [x] **#03 BFS 內 GND/PWR scan 已找到後跳過同類** (來源 B P0) ── **revert × 2 次測試都負面**
  - 位置: `Group.cs` `AddNodeToGroup` (line 111-120)
  - 改動: 在掃描 `TlistC1gnd` / `TlistC1pwr` 前先檢查 `_groupFlags` 是否已有對應 bit
  - 理由: 一個 group 內第一次找到 GND 即可,再找到第二個 GND 不會改變 `_groupFlags`(OR 是 idempotent)
  - 預估收益: 中
  - **第一次實測 (2026-05-28, 10-run)**:
    - +0.61% median, -0.13% avg ── 雜訊範圍,revert
  - **第二次實測 (2026-05-29, 30-run + top-half 方法)**:
    - BEFORE 10-run top 5 avg: 63,451 (pre-AFTER)
    - BEFORE 10-run top 5 avg: 63,089 (post-revert,當前 thermal state)
    - AFTER 20-run top 10 avg: 62,576
    - Δ: **-0.81%** ── 仍負面
    - 即使用 top-half 排除 outlier,改動仍未帶來正效益
  - **分析**:每訪問都付 1 個 bit-test 的成本,但實際多數 group 只接觸一個 GND/PWR source,skip 鮮少觸發 ── cost > benefit。 兩次獨立測試結論一致
  - 狀態: **revert(永久)** ── 7 個 dead-end 之一

- [x] **#04 `SetNodeState` inline enqueue,跳過 c1 supply check** (來源 B P0) ── **採用**
  - 位置: `Recalc.cs` `SetNodeState` (line 127-143) + `EnqueueNode` (line 45-54)
  - 改動: 在 `SetNodeState` 開頭把 `RecalcListNext`/`RecalcHashNext`/`RecalcListNextCount` 複製到 local,然後對 `c1` 直接 inline 三行 enqueue,跳過 `EnqueueNode` 內的 supply check;對 `c2` 只在 `newState == 0` 且非 supply 時 inline
  - 理由: `AddTransistor` (Module.cs:125) `if (IsPwrGnd(c1)) (c1, c2) = (c2, c1);` 已把 supply 正規化到 c2。 c1 永遠不是 Npwr/Ngnd,EnqueueNode 內的 supply check 對 c1 是 dead branch
  - 預估收益: 高
  - **實測 (2026-05-28, 10-run)**:
    - BEFORE 5-run median: 61,441 hc/s
    - AFTER 10-run median: **62,624 hc/s**
    - Δ: **+1.93% median, +3.07% avg**
    - checksum 全 `0x9B103E5E206E4C37`,selftest ALL PASS
  - 額外: hoisting `RecalcListNext`/`RecalcHashNext`/`RecalcListNextCount` 到 local,讓 JIT 更明確優化

### P1 ── 中等熱路徑或常用路徑

- [x] **#05 Callback 換成真正的 pending queue** (來源 B P1) ── **採用**
  - 位置: `Handlers.cs` `InvokeCallbacks` (line 68-82) + `EnqueueCallback`
  - 改動: 目前 InvokeCallbacks 先 foreach 整個 `_callbacks` 確認是否有 pending,再 for-loop 執行 ── **兩次掃描**。 改成 `EnqueueCallback` 時 push 到 pending queue,InvokeCallbacks 0 時 O(1) return
  - 對比: Rust S1 的 `pending_handlers` Vec 已是這個模式;C# 端原為舊式 scan
  - 處理 re-entrant: swap-and-drain 模式 ── `_pendingCallbacks` ↔ `_processingCallbacks` swap (zero-alloc snapshot),迭代 processing 時新進的 pending 落到新的 list,外層 while 下輪再處理
  - 預估收益: 中
  - **實測 (2026-05-29, 10-run)**:
    - BEFORE 5-run median: 62,584 hc/s
    - AFTER 10-run median: **63,102 hc/s**
    - Δ: **+0.83% median, +0.61% avg**
    - 雜訊範圍也較小(spread 1100 vs 2100)── fast-path O(1) return 降低 settle 後波動
    - checksum 全 `0x9B103E5E206E4C37`,selftest ALL PASS

- [x] **#06 `ReadBits` / `WriteBits` 加 `int[]` overload** (來源 B P1) ── **採用**
  - 位置: `Handlers.cs` (ReadBits/WriteBits + AttachRamLikeHandler)
  - 改動: 加 `int[]` overload + `[AggressiveInlining]`;`AttachRamLikeHandler` 內 `addr`/`dataOut` 從 `List<int>` 轉成 `int[]` 再 capture 到 lambda 內
  - 影響: memory(每次 RAM/ROM access) + video(每 pclk1)。 Video handler 已是 int[],call site 自動轉用新 overload
  - **實測 (2026-05-29, 15-run)**:
    - BEFORE 5-run median: 62,261 hc/s
    - AFTER 15-run median (含 JIT 預熱): **62,880 hc/s**
    - Δ: **+0.99% median, +1.10% avg**
    - 注意:第一輪 5-run 有 cold-start 拉低,extended 10 較穩。 改動本身對的
    - checksum 全 `0x9B103E5E206E4C37`,selftest ALL PASS

- [x] **#07 Clock handler 走直接 fast path** (來源 B P1) ── **revert(負效益)**
  - 位置: `Handlers.cs` `AttachClockHandler` + `Recalc.cs` `StepCycle`
  - 改動: clock 不走 `_handlerChain` (multicast delegate),改 `_clockNode` + `StepCycle` 內直接 toggle
  - 預估收益: 中
  - **實測 (2026-05-29, 20-run)**:
    - BEFORE 5-run median: 63,086 hc/s
    - AFTER 20-run median: 62,299 hc/s
    - 前 5 of AFTER median: 61,635 hc/s = -2.30%
    - 全 20 合計 median: -1.25%, avg -1.76%
    - 持續負效益
  - **分析**:推測 .NET 對單一 target 的 multicast delegate 已經優化成直接 indirect call(只一個分支),改動後反而:
    1. 多 `if (_clockNode != EmptyNode)` 檢查
    2. 失去原 lambda 的可能 JIT inline
    3. 仍要呼叫 RunHandlerChain (現在永遠 null check 通過)
  - 狀態: **revert** ── 第 7 個 dead-end 候選

- [ ] **#08 `RunFrame()` 不要在 loop 內呼叫 `Step(1)`** (來源 B P1) ── **暫緩**
  - 位置: `System.cs` `RunFrame` (line 186-189)
  - 改動: 直接呼叫 `StepCycle()` 而不是 `Step(1)`,省一層 for-loop
  - 預估收益: 小到中(每 RunFrame 內 ~715K hc,但 bench-hc 不經過 RunFrame)
  - 原因:此項只在 `--benchmark --frames N` 路徑生效,`--bench-hc` 走 `Step(N)` 不經過。 用戶反映「影響到測試,以後再看看」
  - 狀態: **暫緩** ── 待 frame-based bench 流程穩定後再評估

### Follow-up (來源 C: Sim_hotpath_followup_suggestions_2026-05-29.md)

- [x] **#F1 ROM memory handler 不監看 data bus** (來源 C P1) ── **revert(雜訊內)**
  - 位置: `Handlers.cs` `AttachRamLikeHandler` trigger 建構
  - 改動: `isRom == true` 時 `trigger` 不加 `dataBusL`
  - **實測 (2026-05-29, 20-run with top-half)**:
    - BEFORE 10-run top 5 avg: 63,502 hc/s
    - AFTER 20-run top 10 avg: 63,387 hc/s
    - Δ: **-0.18%** ── 雜訊內,無明顯收益
    - checksum 全 `0x9B103E5E206E4C37`,行為正確
  - **分析**:推測 ROM callback 雖頻繁觸發,但 `if (NodeStates[cs] != 0) return` 早期 filter 成本本來就低。 移除 fake transistor 對 BFS group size 也沒明顯影響
  - 狀態: **revert**

- [x] **#F2 memory callback body 依 ROM/RAM 拆開 + capture byte[] / mask** (來源 C P1) ── **採用**
  - 位置: `Handlers.cs` `AttachRamLikeHandler` closure body
  - 改動: attach-time capture `byte[] data = mem.Data`、`int mask = data.Length - 1`;`readOnly = isRom || we == EmptyNode` 判定後拆成兩個 lambda:read-only path 直接 `WriteBits(dataOut, data[address & mask])`;read/write path 只判斷 `NodeStates[we] == 0`
  - 預估收益: 小到中
  - **實測 (2026-05-29, 20-run + top-half)**:
    - BEFORE 10-run top 5 avg: 63,175 hc/s
    - AFTER 20-run top 10 avg: **63,582 hc/s**
    - Δ: **+1.29% top-half avg, +0.58% median**
    - checksum 全 `0x9B103E5E206E4C37`,selftest ALL PASS

- [ ] **#F3 `SetNodeState` 依 `newState` 拆 high/low 兩個 loop** (來源 C P2)
  - 位置: `Recalc.cs` `SetNodeState` fanout loop (#04 已 inline 後)
  - 改動: 把 `if (newState == 0 && ...)` 從 inner loop 提出,拆成兩個分支 loop
  - 預估收益: 小到中
  - 風險: 中 ── reviewer 自承「複製 hot loop 改動曾因 JIT code shape 變差而負效益」
  - 狀態: 待測(單獨)

- [ ] **#F4 callback target 改 node-id 直查表** (來源 C P2)
  - 位置: `Recalc.cs` `RecalcNode` callback branch + Reset 建表
  - 改動: Reset 建 `CallbackInfo?[] _callbackByNode`,RecalcNode 從 unmanaged 直接 array lookup,避開 `Nodes[]` managed `Node` object graph
  - 預估收益: 小
  - 風險: 低到中 ── 注意 ResetHandlers/FreeUnmanagedMemory 生命週期
  - 狀態: 待測

- [x] **#F5 tuple swap → manual temp** (來源 C P3) ── **跳過**
  - reviewer 自承「預期收益很小」,屬於 micro-opt 範圍
  - 狀態: 不採納

### P2 / P3 (原 reviewer A/B) ── **不採納**

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
