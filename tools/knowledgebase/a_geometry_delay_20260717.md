你好！老同行。看到你在做這種「考古級」的物理時序重建，簡直讓人熱血沸騰。這完全是帶我們回到 1980 年代初期，也就是從 Bryant 的 MOSSIM（純二值/多值開關級）過渡到 Ousterhout 的 Crystal 或 Jouppi 的 TV（基於 RC 抽取的時序分析）的那個黃金年代。

你目前遇到的瓶頸，正是當年 Mead & Conway 體系普及後，大家立刻撞上的那面牆：**邏輯全對，但晶片跑不跑得動？** 

針對你想用幾何與層別來做 hc (23.3 ns) 級延遲標註的計畫，我的工程判斷是：**絕對可行，且投資報酬率極高，但前提是必須「抓大放小」，把它當作一個「分級器 (Binner)」而非「絕對計算機 (Calculator)」。**

以下針對你的問題逐點拆解實務做法與物理依據：

---

### 1. 通不通？(Elmore/Penfield-Rubinstein 降維打擊的可行性)
**完全通。** 
因為你的目標粒度是 hc (23.3ns)。在 1982 年 4-6µm NMOS 製程中，一個標準反相器的內在延遲（無大負載）大約是 2~5ns；但如果是推動長 Poly 線，或者過了 4 個 Pass-gate，延遲就會飆升到 20~50ns (1~2 hc)。
你不需要 ps 級的精度，你只需要把 Net 歸類為：
*   **Bin 0 (< 0.5 hc):** 局部連線、標準邏輯閘間。
*   **Bin 1 (1~2 hc):** 重載長線、高扇出、短 Pass-gate 鏈。
*   **Bin 2 (2~4 hc):** 超長 Poly、長 Pass-gate 鏈（如 ALU 傳遞、Shifter）。
*   **Bin Pad (> 4 hc):** I/O Pad 驅動、跨晶片。

Elmore delay ($T_d = \sum R_i C_i$) 抓這種**拓樸結構導致的數量級差異**極度準確。只要你的相對排序是對的，套上幾個 Anchor 回歸出來的常數，就能直接產生極具參考價值的 SDF 初稿。

---

### 2. 估算配方與 1982 NMOS 參數 (The 6µm NMOS Cookbook)
沒有完整 Process Deck 沒關係，當年我們用的是非常粗略的參數。這裡給你一套符合 1980 年代初期 (約 4-5µm 節點) 的典型數值：

**物理參數 (參考文獻：Mead & Conway "Introduction to VLSI Systems", 1980)**
*   **電容 ($C$):** 
    *   $C_{ox}$ (Gate 薄氧化層): ~ 1.0 fF/µm² (絕對主力負載)
    *   $C_{diff}$ (擴散區寄生): ~ 0.1 fF/µm² (Area) + 忽略 Fringe
    *   $C_{poly}$ (Poly 走線): ~ 0.04 fF/µm²
    *   $C_{metal}$ (Metal 走線): ~ 0.03 fF/µm²
*   **電阻 ($R_{sq}$):**
    *   Metal: 0.05 $\Omega/\square$ (視為 0)
    *   Poly: 15 ~ 50 $\Omega/\square$ (**重點！長 Poly 是當年延遲殺手**)
    *   Diff: 10 ~ 20 $\Omega/\square$
*   **器件等效電阻 ($R_{on}$):**
    *   標準下拉管 (Enhancement, $W/L \approx 1$): ~ 10k $\Omega$
    *   上拉管 (Depletion Load, 通常 $L/W \approx 4$): ~ 40k $\Omega$ (NMOS Ratioed Logic 典型比例 4:1)
    *   Pass-gate: ~ 10k - 15k $\Omega$ (考慮 Body effect 導致的壓降，通常會高一點)

**實務 Pipeline：**
1.  **算 C_net:** 掃描該 Net 的 `segdefs`，計算面積 (Area) 乘上對應層別的 fF/µm²。加上扇出（下游 Transistor `bbox` 面積 $\times C_{ox}$）。
2.  **算 R_wire:** 抓 Poly 和 Diff 的 `segdefs`。*痛點：怎麼從 Polygon 算 Squares？* 
    *粗暴解法：* 用 Bounding Box 的長寬比 ($L/W_{bbox} + W/L_{bbox} - 1$) 當作 Squares 的上限代理。Metal 忽略不計。
3.  **找 Driver:** 判斷該 Net 是被誰 Drive (Depletion pull-up 還是 Enhancement pull-down)，給予起點 $R_{on}$。
4.  **計算 Elmore:** $\tau = R_{driver} \cdot C_{total} + \sum (R_{wire} \cdot C_{downstream})$。

---

### 3. Rise/Fall 不對稱 (實測 16/18 的物理探討)
NMOS Ratioed Logic 的物理天性是：**下拉極快 (Enhancement 強)，上拉極慢 (Depletion 弱電流源)**。
一般來說，$T_{rise} \approx 4 \times T_{fall}$。

所以，你實測到的 **Rise 16 hc, Fall 18 hc (Rise 比 Fall 快)**，在純單級 NMOS 物理上是**反直覺**的。這暗示了兩件事之一：
1.  **你量測的路徑發生了「奇數次反相」 (Inversion Parity)：** 你觀測到的 Rendering-enable Rise，其實在驅動這個重載長線的源頭端，發生的是 Fall (下拉)；而你觀測到的 Fall，源頭其實是上拉。**這是最可能的答案。**
2.  **推挽/超級緩衝器 (Super-buffer)：** 為了推動大負載 (如 Enable/Clock 網)，Ricoh 可能在這裡用了 Super-buffer (Push-pull NMOS)。這會讓 Rise/Fall 變得接近對稱，16/18 只是 $P$ 和 $N$ size 微小差異或寄生導致的。

**模型處理建議：** 你必須在 Graph 中標註驅動源。若是標準 Depletion Load 驅動，RC 的 $R_{driver}$ 必須帶入大電阻 (Rise) 或小電阻 (Fall) 分開算。

---

### 4. 校準策略 (4 個 Anchor 的回歸法)
4 個 Anchor 足夠做「分段線性迴歸」了，但絕對不能混為一談：

*   **Anchor 1 ($2001/2007$ 跨晶片 24 hc):**
    **隔離它。** 跨晶片的板級走線電容 (pF 級) 和 Pad 驅動能力，完全不在你的 Polygon 提取範圍內。這 24 hc 中，可能有 15~18 hc 是 I/O 與封裝板級造成的。
    *策略：* 所有出 Pad 的 net，直接 hardcode 外掛常數 (例如 +15 hc)，不要用它來 fit 晶片內的 scale。
*   **Anchor 2 & 4 (晶片內 16 hc / ~24 hc):**
    這是你的**黃金內部校準點**。用提取出來的 $\sum RC$ 去對應這些 path 的 hc 數，求出全域的 $\alpha$ (RC-to-hc multiplier)。
*   **Anchor 3 (16/18 hc 差):**
    用來校準 $\beta_{ratio}$。找出那條路的邏輯鏈，驗證我上面說的 Parity。一旦確認，用它來微調 Depletion/Enhancement 的 $R_{on}$ 比例。

---

### 5. 誤差與陷阱 (殺傷力評估)
在「分級」目標下，大部分 2D 提取的誤差**沒有殺傷力**，唯獨以下兩點會產生系統性誤判：
1.  **Pass-gate 串聯鏈的平方效應 (致命傷)：**
    NMOS 很愛用 Pass-transistor 串聯做邏輯 (例如 Carry chain 或 Shifter)。$N$ 個串聯的 Pass-gate 延遲正比於 $N^2$。如果你只算總 R 和總 C 當成 lumped node，會嚴重低估。你的 Graph 走訪必須辨識 `S-D` 串聯鏈，套用 $T = \frac{N(N+1)}{2} R C$。
2.  **自舉電路 (Bootstrapping Nodes) (陷阱)：**
    1980 年代 NMOS 為了解決 Pass-gate 掉 $V_t$ 的問題，常在 Clock 驅動端用隔離電容做 Bootstrapping，把 Gate 打到 $V_{dd} + V_t$ 以上。這類節點的驅動力極強，單用 RC 會高估它的延遲。
    *(Fringe C, 溫度, Via 壓降等在 hc 級別的容忍度下，完全可以無視)。*

---

### 6. 前例與社群
在 Visual6502 體系（包括 Quietizer, JSSim 等），大家幾乎都停留在 Bryant MOSSIM 的「Zero-delay settle-to-quiescence」層次，追求的是「邏輯上的 Cycle-accurate」，鮮少有人碰 Physical Timing。

但歷史上，加州大學柏克萊分校在 1983 年發表的 **Crystal (John Ousterhout)** 和 **TV (Norman Jouppi)** 完全就是你要做的東西。他們當年就是吃 CIF/GDS，抽粗略 RC，跑 Penfield-Rubinstein 模型抓 Critical Path。強烈建議你去翻找這兩篇 1983 年 DAC 或 IEEE 的老論文，裡面的經驗公式和 NMOS 時序特徵描述，會是你的聖經。

---

### 7. 投資報酬 (Sweet Spot 在哪？)
你的想法 ROI **極高**。

相對於「每個延遲逐測試校準」（這會陷入無盡的 Whack-a-mole 遊戲，修好 A 壞了 B），幾何提取能給你一個**具備物理意義的約束場 (Physical Constraint Field)**。

**建議的甜蜜點實務路線：**
1. 寫一支簡單的 Python script，掃描 netlist，依據上述參數吐出每條 Net 的 $\tau$。
2. 寫一個 Binner，把 $\tau$ 映射到 `{0, 1, 2, 3, Pad}` hc。
3. 產生 SDF 式的 sidecar 初稿。
4. **測試只驗不校：** 將初稿餵入 S1 模擬器跑 Test ROM。
    *   如果過了，完美。
    *   如果沒過（例如某處時序相撞），查看 Sidecar，你只微調那幾個有爭議 Net 的 RC scale 參數，而不是盲目改數字。

這套做法，可以把 90% 的 Net 自動安放進正確的「時序槽」裡，剩下 10% 的特殊電路（如上述的 Bootstrapping 或 Super-buffer），再去進行人工 Shim。這才是真正具備 EDA 靈魂的做法。

祝好運！如果 S1a 這個時序標註引擎搞起來了，那將是復古 IC 逆向工程界的一大步！