# S1 ——「我們做了什麼」vs MetalNES（貢獻分析）

> 2026-05-13。S1 = `src/AprVisual/Sim/WireCore.*`，一個 switch-level NES 模擬引擎。它**是 MetalNES 的 C# 重寫/移植**，而 MetalNES（`ref/metalnes-main`）那塊核心又是 Visual6502 `chipsim.js` 的最佳化 port。所以「S1 裡我們獨有的貢獻」這個問題，誠實答案是 **modest** —— 這個專案真正 novel 的東西在 **S2→S4**（netlist → boolean IR extraction → 最佳化 → 多 backend codegen）+ cpu-opt β 那個探索，S1 是「讓那些成為可能的、且永遠正確的參照底座」。
>
> 這份文件把「S1 裡哪些是 ported、哪些是我們加的」釐清，免得日後混淆。相關：`MD/note/01-07`（MetalNES 的研究筆記）、`MD/struct/09`（S1 實作風格決定，引用 `ref/AprNes`）、`MD/impl/S1_移植任務清單.md`（移植步驟）、`MD/RETROSPECTIVE.md`（專案回顧）。

---

## 1. 不是我們的（誠實先講）

- **核心演算法** —— `wire_compute` = `recalcNodeList` / `processQueue` / `recalcNode` / `computeNodeGroup` / `addNodeToGroup` / `getNodeValue` + 256-entry `flags → state` LUT（NMOS 解析優先序：ForceCompute 特例 → GND 贏 → VCC → SetHigh → SetLow → PullUp → 保值 → 0；純浮島 = 最大電容 node 保值）。這是 `ref/metalnes-main/source/metalnes/wire_module.cpp` 的，而它又是 Visual6502 `chipsim.js` 的最佳化 port。我們忠實地再 port 到 C#。
- **系統整合的設計** —— 「2A03 + 2C02 + board TTL chip + 卡帶」這個組裝方式、handler / callback 機制的概念（callback = fake transistor：gate=watched、c1=fakeTarget、c2=Ngnd，讓正常的 transistor 傳播機器免費幫你監看）、behavioral RAM/ROM 不當電晶體模擬（module 宣告 `memory:{name:size}`、handler 監看 cs/`/we`/addr/data 做陣列讀寫、用 SetHigh/SetLow 驅動 data bus）—— 都是 MetalNES 的設計，我們在 C# 裡重新實作。
- **資料格式** —— `.js` 模組格式（segdefs/transdefs/nodenames + pins/modules/connections/pullups/forceCompute/memory/`*_files`）是 Visual6502 / MetalNES 的；`nes-001.js` 等系統定義檔「from `ref/metalnes-main` or 寫新的」—— 部分是他們的。

---

## 2. 我們加的 —— 三類

### 2.1 對 MetalNES 的 fidelity refinement（對下游 IR 工作 matter）

| refinement | MetalNES 怎樣 | 我們怎樣 | 為什麼 matter |
|---|---|---|---|
| **保留 segdef 的 `'+'` / `'-'` 兩種 pull 極性** | 只留 `'+'`（pull-up） | 兩種都留（pull-up / pull-down） | 區分上拉 vs 下拉 → group 解析更接近物理 |
| **用 2A03/2C02 transdef 第 7 欄的 `weak`/depletion-load flag** | 忽略它（Visual6502 也沒這欄） | 用它分「強 pull-down vs 弱 pull-up」 | S2 的 `DriveAnalysis` 靠這個分類 node（哪些 transistor 是 depletion load）；用了才能正確抽 `NextExpr` |
| **顯式的 lowering pass**（`WireCore.Lower.cs`） | —（部分隱含） | 折掉 always-on transistor / dead `gate==vss` / dup（典型 ~441 node 合併、~530 transistor 砍掉） | 餵 S2 一個比原始 netlist 乾淨的 node-id 空間（aliasing / SCC 分析在上面跑） |
| **group 解析保留「被驅動 vs 保值」可分辨** | flags→bool（丟掉「為什麼是這個值」） | flags 保留、能說「這 node 在 hold 它的值」vs「被 VCC/SetHigh 驅動」 | 是 S2 的 `Hold(self)` / `Prev` IR 的基礎 —— 沒這個就抽不出 latch 的 `Mux(clk, data, Hold(self))` |

### 2.2 從零的 C# 實作（不是演算法新，但是實打實的工程）

- **`.js` 模組格式 parser**（`WireCore.Parse.cs`）—— `var x = ...` 前綴、JSON5-ish（`//` `/* */` 註解、單/雙引號、負數、bare-identifier key、trailing comma、`:`/`=` 旁不需空白）、`*_files` 外部 netlist 引用、遞迴 sub-module、loaded-def cache（`nes-001` 重複型別只 parse 一次）。
- **module instancing + name resolution**（`WireCore.Module.cs`）—— instance node-id 分配、`connection = always-on transistor`、`a[7:0]` / `a[]` / `x|y|z` / `*wildcard` 的展開、`/` `#` `~` `_` 前綴的名字。
- **handler / callback 機制 + behavioral RAM/ROM**（`WireCore.Handlers.cs`）—— per-cycle handler chain（multicast `Action`）、callback = fake transistor、`Memory` 類（`byte[]` + power-of-2 mask）、`AttachClockHandler` / `AttachMemoryHandlers` / `AttachFlatRamHandler`（bare-2A03 rig）/ `AttachVideoHandler`（pixel-clock 上升沿讀 palette RAM → ARGB framebuffer）、`ReadBits`/`WriteBits` bit-vector helper。
- **系統載入 + 真 reset**（`WireCore.System.cs`）—— 載 `nes-001` + 卡帶、attach handler、`setHigh res; step(192); setLow res` 的真實 reset 序列。
- **trace + blargg 測試**（`WireCore.Trace.cs` + `Test/TestRunner.cs`）—— trace 欄位、blargg `$6000` signature 偵測、`--test` / `--test-dir` 測試 runner（PASS/FAIL + exit code）。
- **效能化的 unmanaged 熱路徑** —— `byte* NodeStates` / `int* TransistorList` / `NodeInfo* NodeInfos` / `int* RecalcList…` / `byte* FlagsToState`（`NativeMemory.AlignedAlloc`、一次性 free）、zero-bounds-check 內迴圈、steady-state 零配置、no string（採 `ref/AprNes` 的風格）。MetalNES 是 C++「天生快」；我們的貢獻是在 managed runtime 裡做到 ~MetalNES 級的速度（~47K hc/s）。
- **O(1) group-dedup**（`_inGroup` flag 陣列）取代 MetalNES `addNodeToGroup` 裡的 linear scan。
- **GDI `SetDIBitsToDevice` / `StretchDIBits` blit**（`Render/NativeGDI.cs` + `Render/NativeApi.cs`）—— 把 unmanaged ARGB framebuffer 直接 blit 到控制項 HDC（lifted from `ref/AprNes/tool/NativeRendering.cs`）；無 PictureBox / Bitmap.Image。

### 2.3 結構性貢獻 —— 把 S1 設計成「S2–S4 的可驗證底座」（這是 S1 跟「單純又 port 一次 MetalNES」的真正差別）

- `NetlistGraph.BuildFrom()` 能從 S1 的結構讀出一張乾淨的依賴圖；S1 攜帶夠的 drive / weak 資訊讓 `DriveAnalysis` 能分類每個 node。
- `DeferRecalc` / `ProcessQueueOneLevel` / `SkipRecalcOf` 這些 hook —— 讓 IR engine 在 driving mode 能「借用」S1 的機器處理硬的部分（殘餘 SCC + hybrid pass-transistor bus + behavioral memory 的 bridge）。
- `--trace-cmp` / `--trace-cmp --engine ir` 等價 gate —— S1 跟 IR 並跑、逐 node 逐半週期比對。這是整個 S1→S2→S3→S4 pipeline 的命脈（每階段都拿 S1 當 golden reference）。
- 整體的 co-design：「把 switch-level sim 蓋成『可 extract、可驗證、可 codegen 出 logic IR』的樣子」 —— S1 是這個 co-design 的一半（另一半是 S2 的 IR）。

---

## 3. 一句話

S1 的*演算法*是 Visual6502 → MetalNES 的，我們忠實 port；我們在 S1 裡加的是「**幾個 fidelity refinement**（雙 pull 極性 / weak-depletion flag / 顯式 lowering / driven-vs-held 可分辨）+ **一份從零的 C# 實作**（含 `.js` parser / module-name resolution / handler-callback-behavioral-memory 層 / 系統 bring-up / 達到 ~MetalNES 級速度的 unmanaged 熱路徑 / O(1) group dedup / GDI blit）+ **把它結構化成 S2–S4 的可驗證底座**」。專案真正的智力貢獻在 **S2→S4 那條 translate-the-silicon → verifiable logic IR → multi-backend codegen pipeline**（+ cpu-opt β 那個探索）—— S1 是讓那些成為可能的、且永遠正確的參照。
