# 實作路線圖與 TODO

整理自 `GPU_Project_Roadmap_TODO_en.md` 與 `14_Day_Action_Plan_en.md`。

---

## Part 1：完整 Phase 0–10 路線圖

### Phase 0：資料準備

- [ ] 釐清 `segdefs.js` / `transdefs.js` 的欄位結構
- [ ] 定義 parser 輸出格式
- [ ] 保留 pull-up / pull-down 標記（`+` / `-`）
- [ ] 找出關鍵 pin：`clk0`, `res`, `rw`, `ab0–ab15`, `db0–db7`
- [ ] 建立可重複使用的清洗後快取檔

### Phase 1：圖模型

- [ ] 實作 `NetNode`
- [ ] 實作 `NetTransistor`
- [ ] 實作 `NetlistGraph`
- [ ] 建立 gate / control 鄰接
- [ ] 建立 conduction 鄰接
- [ ] 確認 node / transistor 數量與原始資料相符

### Phase 2：CPU 參考求值器

- [ ] 定義節點狀態儲存（`Current[]` / `Next[]`）
- [ ] 定義電晶體 on / off 規則（先用 `gate.Current`）
- [ ] 為單一 phase 建構通電圖
- [ ] 實作「連通到 GND」偵測
- [ ] 實作「連通到 high」偵測
- [ ] 實作浮島（floating island）偵測
- [ ] 加入「保留前一狀態」行為
- [ ] 加入「迭代到收斂」的 settle
- [ ] 設定 settle 上限與超出警告

### Phase 3：小型測試資料

- [ ] inverter
- [ ] NAND
- [ ] NOR
- [ ] pass transistor
- [ ] dynamic latch
- [ ] 共享 bus 節點
- [ ] 每個案例都附上期望 trace

### Phase 4：回授與狀態分類

- [ ] 三色 DFS
- [ ] SCC / Tarjan
- [ ] 把回授群縮成超節點
- [ ] 區分 static latch / dynamic storage / bus loop
- [ ] 驗證分類後不破壞通電語意

### Phase 5：IR 與邏輯抽取

- [ ] 定義 IR：`Const`、`NodeRef`、`Not`、`And`、`Or`、`Mux`、`Hold`
- [ ] 實作基礎化簡（`A && true ⇒ A` 等）
- [ ] 重複子式去重

### Phase 6：輸出後端

- [ ] emit debug JSON
- [ ] emit debug structural Verilog
- [ ] emit synthesis-oriented Verilog
- [ ] emit CPU evaluator trace
- [ ] emit CUDA codegen

### Phase 7：CUDA MVP

- [ ] 採用 SoA layout
- [ ] 避免 AoS layout
- [ ] 用 `uint32_t` / `uint64_t` bit-slicing
- [ ] CPU 與 CUDA 共用同一份 IR
- [ ] 先做 batch evaluator（不做常駐 kernel）
- [ ] CPU / CUDA 節點對節點等價驗證

### Phase 8：2A03 Bus 整合

- [ ] 實作 reset vector 行為
- [ ] 停止使用 `NOP on reset` 作為正式模型
- [ ] 整合 NROM PRG ROM
- [ ] 整合 RAM mirroring
- [ ] 驗證前幾個 fetch cycle
- [ ] 驗證 `R/W` 與資料 bus 方向

### Phase 9：真實局部驗證

- [ ] reset chain
- [ ] 單一 register bit
- [ ] ALU carry path 一段
- [ ] 與參考模型比對
- [ ] 記錄無法收斂的案例

### Phase 10：PPU 與整機

- [ ] 確認 CPU pipeline 已穩定
- [ ] 加入 2C02 parser
- [ ] 驗證 CHR bus 與 shift register 行為
- [ ] 驗證 sprite evaluation
- [ ] 才考慮整顆 GPU 常駐架構

### 不該太早做的事

- [ ] 一開始就同時跑 10,000 台 NES on GPU
- [ ] 一開始就把 CPU / PPU / APU 拆給多個 thread
- [ ] 一開始就支援複雜 mapper
- [ ] 把 `+` 簡化成「永遠是 1」
- [ ] 把「跑固定次數 settle」當成正確性的證明

---

## Part 2：14 天起步行動計畫

**目標不是 14 天做完專案**，而是 14 天**建立一個可信的起點**：

- 把範圍縮到最小
- 開始建知識基礎
- 做出最小可執行骨架
- 留下可驗證的產出

### Day 1：定義範圍

- 寫下第一階段目標（限制為「2A03 局部區域 parser + graph + evaluator」）
- 寫一份明確的「現在還不做」清單：PPU / mapper / 整機 GPU / 效能調校
- **產出**：一頁 scope 文件

### Day 2：理解資料格式

- 細讀 Visual6502 / Visual2A03 資料結構
- 確認 `segdefs.js` 與 `transdefs.js` 各自包含什麼
- 列出真正需要保留的欄位
- **產出**：parser 欄位筆記

### Day 3：定義資料模型

- 定義 `NetNode` / `NetTransistor` / `NetlistGraph`
- 寫最小的 C# class skeleton
- **不追求功能**，只先鎖定資料模型
- **產出**：基本圖模型 class

### Day 4：parser prototype

- 把少量資料轉成 nodes / transistors
- 驗證計數與基本連結正確
- **產出**：parser prototype

### Day 5：手刻測試網表

- inverter / NAND / pass transistor 三個迷你電路
- 還不依賴真實資料
- **產出**：手刻測試案例

### Day 6：通電分析器

- 只做最基本的 source / drain 連通性
- 實作連通分量搜尋
- **產出**：第一版 conduction analyzer

### Day 7：最小 CPU evaluator

- 簡化規則：`連 GND → 0`、`連 high → 1`、`否則保留前狀態`
- **產出**：第一版 evaluator

### Day 8：settle loop

- 支援「迭代到收斂」
- 記錄迭代次數
- **產出**：第一版 settle engine

### Day 9：手刻案例的 trace

- 為手刻電路產生 trace
- 與期望行為手動比對
- 修最明顯的 graph / conduction 錯誤
- **產出**：第一組 trace

### Day 10：真實資料

- 選一個**很小**的 2A03 區域，**不是整顆 CPU**
- 先只完成 parse 與 graph dump
- **產出**：真實局部區域的 graph 輸出

### Day 11：對真實區域跑 evaluator

- 不要求完美，只要能跑、能 trace、能檢視
- **產出**：第一份真實區域 trace

### Day 12：迴路 / SCC 分析

- 加入最小的 loop detection
- 至少先標出明顯的回授區域
- 記下哪些節點可能要設成 `current_state`
- **產出**：第一份 loop detection 結果

### Day 13：總結回顧

- 整理兩週成果，分成三類：
  - 已經被驗證
  - 仍只是假設
  - 接下來該做
- **產出**：兩週 review

### Day 14：選定下個月唯一主軸

- 三選一：
  - 強化 parser
  - 強化 evaluator 正確性
  - 強化真實局部區域驗證
- **產出**：明確的下月方向

### 14 天後你應該有什麼

不會有完整系統，但會有：

- 範圍明確縮窄的 scope
- 基本圖模型
- 最小 parser
- 最小 evaluator
- 一組手刻測試
- 至少一個真實局部區域的 graph 或 trace
- 對「下一步要往哪走」有明確判斷

### 14 天計畫背後最重要的原則

> 不要把這 14 天花在追求最終架構。
> 真正的任務是建立基礎、證明你能持續推進、把專案從抽象想法變成有 artifact、有 trace、有邏輯的東西。
