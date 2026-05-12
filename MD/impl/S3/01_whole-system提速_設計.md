# S3 主線設計 — whole-system 提速：event-driven IR eval + precise bridge + 救回 InScc

> 接 `00_S3_效率與優化.md`。user 重定方向後 S3 = **整片 NES 跑快**（每顆晶片都拉高 IR coverage + bridge 提速），不是 bare-2A03 的「CPU proof」。本檔 = S3 主線的設計（① 寫設計 → ② 丟 Gemini → ③ 評估採用 → ④ 實作 → ⑤ 驗證）。

---

## 1. 現況（量到的數字）

- **driving 模式比 S1 慢 ~5.4×**：branch_timing/1 兩 frame（1,369,455 master 半週期 = 57,061 個 6502 cycle，Release）—— S1 = 29.7s；IR-driving + flat eval = 159.6s；IR-driving + flat + skip-184 ≈ 165s（無差）。recursive EvalExpr 更慢（232.7s, ~8×）。
- **IR coverage（全系統 `nes-001`，14723 node）**：12523 IR-covered（85.1%，= `NextExpr[v]!=null`）、6818 driving-evaluated（46.3%，= `NextExpr!=null && !InScc`、放進 `EvalOrder`）、**5705 個 InScc node → driving 模式交給 S1 `ProcessQueue` 處理**。flat program = 34,893 條指令。`OkToSkipInRecalc` 只 184 個夠格。
- **InScc 結構**：2254 個 SCC，size 703 / 79 / 54 / 38 / 26 / 20 / 14 / 13 / 11 / 11 / 11 / 9 / 8 / 7×… / 6×… / 其餘大量 size-1（self-loop）。
  - **PPU 的 703-node SCC** —— 含 `ppu.clk0_int` / `ppu./clk0_int` / `ppu.pclk0` / `ppu.pclk1`（PPU 的 master→pixel 時鐘除頻）+ 一大堆 sprite-eval / OAM / shift-register 的 pass-transistor 網路（DriveAnalysis 沒能把它們化成乾淨的 `Mux` tree → 它們的 `NextExpr` 互相引用對方的 current value，繞成一個 703-長的環）。
  - **APU 的 505-node SCC**（bare-2A03 = 5525 node、1071 InScc、105 SCC、最大 505，落在 node 4000-4200 範圍 = die 上的 APU 區）—— frame counter / length counters / sweep units / 四個 channel 的 timer，全是 cross-coupled counter。
  - 其餘 size ≤ 80 的小 SCC + 大量 self-loop（T-flip-flop：`q_next = !q` 或 `Mux(en, !q, q)`）。

## 2. 為什麼慢（三個成本）

1. **(a) flat program 是 brute-force**：每個半週期無條件評估全部 ~6818 個 driving-covered node 的 `NextExpr`。S1 是 event-driven（只 `RecalcNode` 真的變了的那幾個 node）。一個 6502 cycle 裡實際 toggle 的 node 可能只有幾十~幾百個 → IR eval 做了 ~10-100× 的冗餘 node-work。**這是主要瓶頸**（34893 條/半週期 × 1.37M 半週期 ≈ 478 億條指令）。
2. **(b) bridge 冗餘 re-derive**：`RunFlatProgram` 的 `OpStore` 對每個變了的 IR node `EnqueueNode(v)` → 接著 `ProcessQueue` `RecalcNode(v)`（重算 group → 同一個值）+ fan-out 到別的 IR node（又被重算）。`OkToSkipInRecalc` 只能 skip 184 個（要求 channel-component 全 IR + dep-closure 全 IR，太嚴）。
3. **(c) 5705 個 InScc node 仍走完整 S1 `ProcessQueue`**（~39% 的 node）—— 這部分本來就跟 S1 一樣快，不是問題；但「(a) brute-force IR eval + (c) S1 settle InScc + (b) 冗餘 re-derive」加起來 = 5.4× S1。

> 註：把 IR eval 做成 event-driven 後，**就算單實例 CPU 只跟 S1 打平**，最終的提速來自 **GPU bit-slicing**（一個 32-bit word = 32 個 NES 實例並行）—— 但前提是 IR 是個 **完整（高 coverage）、可評估** 的模型；user 要的「整片 NES 跑快」也是看整體吞吐，所以 coverage（救回 InScc）比 bare-2A03 的單實例 benchmark 更重要。

## 3. 提議的分階段計畫

### Step α — precise-enqueue bridge（correctness-neutral，打成本 (b)）
- `OpStore` 對 IR-covered `v` **不 `EnqueueNode(v)`**；改成：`RunFlatProgram` 跑完後，只把「真的變了的 IR node」的 **非-IR fanout**（gate-consumer + channel-neighbour，過濾掉 IR-covered 的）丟進 queue。`ProcessQueue` 於是只 settle InScc 子圖 + bus bridge（hybrid bus），不再重算 IR 那 46%。
- 需要預算：每個 driving-covered node 的「非-IR fanout 集合」（一次性，`Build()` 時建）。
- 風險：低（不改「算什麼」，只避免重複算）。預期：driving 從 ~5.4× 降到 ~2-3× S1。

### Step β — event-driven flat eval（correctness-neutral，打成本 (a)）—— **主要的 win**
- 只在「某個 input 這個半週期變了」時才重評估一個 IR node 的 `NextExpr`。維護一個 dirty queue；dep graph（`BuildEvalOrder` 已經建了「node w → 引用 `Node(w)` 的 IR node 們」）告訴我們髒污怎麼傳。流程：半週期開始 snapshot；handler + bridge 把外部變化餵進來 → 沿 IR sub-DAG 在 `EvalOrder` 順序傳 dirty → 只重評估 dirty 的 node → 改了就把它的 consumer 標 dirty。
- 風險：中（要小心 IR↔InScc 跨邊：InScc node 變了要能標到引用它的 IR node 為 dirty；IR node 變了要能 enqueue 它的 InScc consumer）。
- **替代設計（問 Gemini）**：與其「IR flat eval（event-driven）+ S1 ProcessQueue（event-driven）+ bridge」三層，不如**把兩個 event loop 合成一個** —— ProcessQueue 的 `RecalcNode(v)` 改成 `if (IsIr(v)) EvalAndSet(NextExpr[v]); else RecalcNode(v)`。難點：ProcessQueue 是 BFS-by-level、不是 topo order，但 `NextExpr[v]` 會引用「比 v 早 evaluate 的 node 的 `Node(w)`（current value）」—— 在非-topo 的 event loop 裡 w 可能還沒被處理。解法之一：IR node 之間用 `EvalOrder` 排（在同一個 BFS level 內先處理 EvalOrder 較前的）；或 IR node 一律只引用 `Prev(w)`（把所有 forward 邊也改 Prev → 但那會引入一個半週期的延遲、可能要多跑幾輪 settle）。

### Step γ — 救回 InScc node（打成本 (c) + 提升 coverage，要新的 IR 建模）
1. **self-loop SCC（2254 個裡的絕大多數）**：`q` 的 `NextExpr[q]` 引用 `Node(q)` → 改寫成 `Prev(q)`，self-edge 就斷了、`q` 變可 evaluate（`Prev` 永遠有值）。**前提**：`NextExpr[q]` 真的是「`Prev`-值 + handler input」的函數（一個 register/flip-flop），不是「碰巧 self-reference 的組合邏輯 glitch」。需要一個 classifier。T-FF（`q_next = !q` / `Mux(en, !q, q)`）就是這類。
2. **大 SCC（PPU 703、APU 505）**：counter / divider / shift-register tangle。兩種子方案：
   - **(i) 通用 feedback-edge-set cut + Prev 替換**（= 之前 revert 掉的 Stage D，但要加 classifier 只切「真的是 clocked-register 邊界」的邊；而且除頻鏈的 `NextExpr` 抽出來本身就是錯的、可能要重抽）。
   - **(ii) pattern-match 已知結構**（T-FF 鏈 → /N counter；modulo-N counter with reload；LFSR shift register）→ 為它們發手寫 IR。較費工但較可靠。
3. **之前 Stage D 為什麼壞掉**（要避免重蹈）：盲目切 back-edge + `Node→Prev` —— (1) 破 checking 模式（`clk0_int` 每半週期變 → `Prev(n160) ≠ Node(n160)`，下游讀到舊值 → 0→2091 mismatch）；(2) 除頻鏈抽出來的 `NextExpr` 本身就錯（driving 模式也壞）。→ 結論：除頻/counter 需要**特別處理**（pattern-match 或正確的 multi-stage 抽取），不能只靠盲目 edge-cut；要先有「這個 SCC 是不是 pure pipeline / clocked-register chain」的分類器。

## 4. 想問 Gemini 的

1. **優先順序**：α → β → γ？還是 γ 先（先把 coverage 補滿、再讓更多 node 受惠於 event-driven）？還是 β 先（直接打最大的瓶頸）？
2. **β 的「合成一個 event loop」vs「兩層 + bridge」**：哪個對？合成的話，IR node 的 `Node(w)` forward 引用怎麼在非-topo event loop 裡保證 w 先處理？「全部改 Prev」會不會引入太多延遲、要 settle 多輪？
3. **γ 的子方案 (i) 通用 edge-cut + classifier vs (ii) pattern-match**：哪個務實？classifier「這條邊是不是安全的 register 邊界」怎麼建（看 gate 是不是某個 clock-like node？看兩端是不是 cross-coupled？看 transistor 的 weak flag？）？
4. **更聰明的表示**：`NextExpr` 編成 C# delegate / 動態 IL / 更 cache-friendly 的 bytecode？S4 本來就是 codegen —— 要不要把一部分提前？bit-slicing 友善的 IR 長什麼樣（per-node 一個 boolean 函數、輸入是 32-bit word）？
5. **整體目標的詮釋**：「整片 NES 跑快」的合理 milestone 是什麼？單實例 CPU IR ≈ S1 就夠（剩下靠 GPU 並行）、還是 CPU 也得明顯贏 S1？coverage 拉到多少算「夠」（90%? 95%? 100%）？

## 6. Gemini 評估（log `tools/knowledgebase/message/20260512_151100.txt`）+ 採用的計畫

Gemini 點出一個**戰略矛盾**：CPU 模擬器要快靠 event-driven（跳過不活躍 node），但 **GPU 極度討厭 event-driven（queue 操作 + branch divergence 殺效能），GPU 最擅長的正是我嫌棄的 brute-force flat program**。所以 S3（CPU 優化）只是為了「加速驗證迴圈」、不是最終產物；**真正的 blocker 是 IR coverage 不足 → 必須 fallback 到 S1，而 GPU 跑不了 S1 的 switch-level BFS**。逐題回答（全採用）：

1. **優先序 = α → γ →（S4 C++ codegen 提前）→ β**：
   - **α（precise-enqueue bridge）先做**：架構 bug 修復、成本極低（靜態建表）、立刻消除冗餘 (b)，ROI 最高。
   - **γ（救回 InScc）是專案生死線**：GPU bit-slicing 跑不了 S1 ProcessQueue → PPU 703 + APU 505 沒變成 boolean IR 的話，GPU 管線就斷了。**coverage 才是真正的 blocker，不是 CPU 的 event-driven 效能。**
   - **S4（提前進 C++ codegen 實驗）優先於 β**：γ 解掉後直接把 flat program 轉 C++ bit-slicing（32-bit `& | ^ ~`，`-O3`）—— 比任何 C# 直譯器快上百倍，也是無痛過渡到 CUDA 的跳板。
   - **β（event-driven flat eval）降為「可選/延後」**：除非 C# 驗證跑到無法忍受。有了 α，C# 直譯器應該就回到 ~S1 速度，對開發驗證夠了。寫 β = 寫「最終 GPU 絕對用不到的程式碼」。
2. **β 若做：絕對「兩層 + bridge」、不要合併 event loop**：IR 是 zero-delay（topo sort、一個 pass settle）vs S1 是 propagation-delay（levelized event queue）—— 硬塞非-topo event loop → forward 引用 `B = A & C` 時 C 還沒進 queue → 算出錯誤過渡態 → 傳播、甚至無限震盪；「全部改 Prev」= 把組合邏輯強行變成半週期 pipeline register（5 級 Mux tree → 要 5 個半週期才 settle）→ 徹底破壞 cycle-accurate，不可行。**正確的 β**：維持 IR topo array + `BitArray is_dirty` + `min_dirty_index`；input 變了設 dirty、更新 min；從 min_dirty_index 往下遍歷 EvalOrder，`if(is_dirty[i]){ val=Eval(); if(val!=old){ set children dirty; update bridge queue; } }`。保留 GPU flat-program 雛形 + 加 CPU 友善 skip。
3. **γ = pattern-match 為主 + 針對性 edge-cut，不要盲目 feedback-edge cut**：
   - **classifier 核心 = 定義 clock domain**：NMOS 的 register 邊界 100% 由 pass-transistor 控制 → 寫個分析器找出所有受 `clk0`/`pclk0`/`pclk1` 等全域/區域時脈驅動的 pass-transistor 的寫入埠 → **只有這些 node 的輸入端才有資格標 `Prev()`**。
   - **self-loop（size-1 SCC）**：確定它是 dynamic latch（parasitic cap 保持、clock 更新）或 static latch（明確的 inverter 回饋）、且更新條件由 clock 決定 → 安全地把迴路上的 self-reference 轉 `Prev()`。
   - **PPU/APU 大 SCC**：703 通常是因為 (a) bi-directional pass transistor 被誤認成 combinational loop、(b) clock divider（T-FF 串聯）。pattern-match：shift-register/LFSR = 認 `A = Mux(clk, Prev(B), Hold(A))` 鏈；counter = 認 ripple-carry 特徵。**關鍵手段：手動把 PPU 內部 clock-generator 的那幾個分頻 node 強制升級成「pseudo-input」—— 在分析 NextExpr 前先切斷它們、當獨立子圖處理 → 打斷 clock-gen → 後端邏輯的 cycle → 703 大 SCC 瞬間崩解成一堆幾十個 node 的小 SCC**。
4. **更聰明表示 = 不要 C# IL/delegate；直接寫 bit-sliced C++ codegen**：把 34,893 條 flat program 輸出成一個巨大的 C++ 函數（`uint32_t node_X = (node_Y & node_Z) | (~node_Y & node_W);` —— Mux→`(c&a)|(~c&b)`、Hold→跟時脈狀態 bitwise 結合），Clang/GCC `-O3` 編譯（現代編譯器對這種 static-SSA 巨大 block 優化極強：自動配暫存器 + instruction scheduling）。**這既是驗證邏輯的最速解、也是無痛過渡到 CUDA kernel 的跳板**。
5. **「夠好」的定義**：(i) **IR coverage 及格線 = 99-100%**（哪怕 1% 活躍邏輯留在 S1，GPU 夢就死了 —— thread divergence 塞不下 S1 BFS；只有「永遠不會動」的外部 pin config 可豁免）；(ii) 單實例 CPU IR **只要慢 S1 兩倍以內就算大獲全勝**（flat-program 成本不隨並行度線性增加 —— CPU 上可 SIMD AVX2/512 一次算 8/16 台、GPU 一次算 32/64/萬台）；(iii) milestone：**M1 = α**（C# 直譯器驗證速度回到 ~2× S1）；**M2（關鍵）= γ**（clock-domain 切割 + 手動斷 PPU/APU clock-tree 回饋邊 → coverage 衝到 98%+、消滅 >20 node 的 SCC）；**M3 = 跳過 β，寫陽春 C++ codegen 輸出 bit-sliced C++ → 塞 32 個 test vector 跑、驗證 vs S1 等價**；**M4 = C++ codegen 改 CUDA/HLSL codegen → 宣告勝利**。

> **總結（Gemini）**：別在 CPU 上過度優化一個未來 GPU 不會用的 event-driven 引擎。精力全投在解開 PPU/APU 的 SCC、coverage 衝滿、然後提早進 C++ bit-slicing 實驗。

## 7. 本檔狀態

firing 22（① 階段）：寫了本設計 + 丟 Gemini + 採用上面的計畫（α → γ → S4-codegen-提前 → β?）。
**下個 firing（② 階段）= 實作 Step α（precise-enqueue bridge）**：`OpStore` 對 IR-covered v 不 `EnqueueNode(v)`；`Build()` 時預算每個 driving-covered node 的「非-IR fanout 集合」；`RunFlatProgram` 跑完後只把「變了的 IR node 的非-IR fanout」丟 queue → `ProcessQueue` 只 settle InScc 子圖 + hybrid bus。驗收：等價（`--trace-cmp --engine ir` 0 mismatch）+ benchmark（看 driving 從 ~5.4× 降到多少）+ `--selftest` 全 PASS + 不破壞 S1。

(④⑤ 實作筆記 + 進度日誌 → 接在 `00_S3_效率與優化.md`。)
