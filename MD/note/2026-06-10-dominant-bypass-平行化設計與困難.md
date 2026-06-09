# Dominant-Bypass 平行化(雙核切割)設計與困難摘要

> 分支:`dominant-bypass` · 日期:2026-06-10 · 狀態:**設計探討(尚未實作)**
>
> 前情:P-5 dominant-driver turn-off 旁路在單執行緒下「正確但仍輸」——旁路本身 +3.4%,
> 但維護 `DominantGate` 的成本 −15.4% ⇒ 淨 −12~−18%(本分支重測:separate-helper −18%,
> fused 進 `RecalcNodeFast` 掃描後 +2.37%、仍淨負)。維護成本的大頭經量測**不在供電 gate
> 的重掃**(第二次掃是 L1-hot、近乎免費),而在 **`DominantGate` 的隨機寫入 stream + BFS
> 逐成員維護**。本文探討使用者提出的下一步:**把維護外包到另一顆核心**。

---

## 1. 目標(設計意圖)

把 P-5 拆成兩條釘在**不同實體核心**的執行緒,讓兩份 cost 分攤掉:

- **主執行緒(Main / core A)**:跑事件驅動模擬(`ProcessQueue` / `RecalcNode` /
  `SetNodeState`),並**讀** `DominantGate` 來套用 +3.4% 的 turn-off skip——但**不付**維護的
  寫入成本。
- **Worker 執行緒(Dominant / core B)**:負責**計算並寫入** `DominantGate`。

期望:主執行緒接近 baseline 速度 + 拿到 +3.4% 的 skip 紅利 ⇒ 把單核時「維護吃掉收益」的
結構,改成「收益留在主核、維護丟去閒置核」⇒ 翻成淨正。

實作面用既有的 `--pin` 機制(thread affinity)各釘一顆 P-core,避免兩條熱執行緒互搶同一核
心、互踢 L1/L2。

```
        core A (Main)                         core B (Worker)
  ┌───────────────────────┐            ┌───────────────────────────┐
  │ ProcessQueue 結算波      │            │  計算 DominantGate[c]        │
  │  RecalcNode/Fast        │  ──────▶   │   (掃供電 gate、count+capture)│
  │  SetNodeState           │   讀/寫?    │                            │
  │   └ 讀 DominantGate skip │  ◀──────   │   寫 DominantGate[]          │
  └───────────────────────┘            └───────────────────────────┘
```

---

## 2. 使用者已點出的困難點

1. **陣列的跨核存取型態決定成敗**
   - **讀-讀**:兩核同時讀同一份資料 → 各自快取一份、**不互相失效**,沒有 lock 成本。OK。
   - **寫-寫 / 讀-寫**:同一條 cache line 被一核寫、另一核讀(或都寫)→ 觸發快取一致性
     (MESI)失效與搬移 → 等同隱性 lock、效能掉。**這正是 `DominantGate`(worker 寫、main
     讀)的型態。**
2. **開 thread 的 context-switch 成本**:建立、喚醒、跨核排程都有開銷。
3. **需要 wait 機制**:主執行緒走到「要判斷某端點能不能 skip」時,如果 worker **還沒把該
   節點的 `DominantGate` 算好**,主執行緒就得 **wait** ——否則讀到舊值會不正確。

---

## 3. 我補上的更深層困難(這幾點才是真正的牆)

### 3.1 ⚠️ 資料相依:Dominant 不是「可獨立預先算」的工作(最致命)

`DominantGate[c]` 是 **c 剛解析出的值 + c 當下供電 gate 狀態** 的函數,而「c 的值」**正是
主執行緒在 `RecalcNode` 當下才算出來的**。也就是:

> Worker 要算 `DominantGate[c]`,必須先吃到主執行緒對 c 的解析結果。
> 這是**逐節點粒度的 producer → consumer 串接**,不是兩份獨立工作。

更糟的是:主執行緒**在同一個結算波(settle wave)裡**就需要用 `DominantGate` 來決定
turn-off 要不要 skip。換句話說,**消費點(skip 判斷)和生產點(節點解析)在同一條關鍵路徑
上、相隔極近**。Worker 永遠落後 main 一步,而 main 立刻就要那筆結果。

這不是「把一塊獨立計算搬去別核」,而是「把一條緊耦合的相依鏈硬切兩半」——切點兩邊都要等對方。

### 3.2 wait 機制會把平行化退化回序列化

承 3.1:主執行緒到 skip 判斷點時,worker 大概率**還沒算好那個節點**(它落後)。於是只剩兩條路:
- **(a) wait**:主執行緒停下來等 worker → 兩核變成「你做我等、我做你等」的乒乓 →
  **退化成序列、外加同步開銷**,比單執行緒還慢。
- **(b) 讀舊值往前衝**:**破壞 bit-exact**(skip 判斷用到過期的 `DominantGate` → 可能漏算 →
  checksum 不符)。本專案硬性規定 bit-exact,**(b) 直接出局**。

兩條都不通,是這個設計的核心矛盾。

### 3.3 快取一致性流量會比它想省下的還貴(在 memory-latency-bound 引擎上尤其致命)

引擎本來就**卡在記憶體延遲**(每次節點重算是隨機 gather)。再加上跨核一致性流量:

- **`DominantGate`(worker 寫、main 讀)**:read-write sharing → main 每次讀都可能要把 cache
  line 從 core B 搬過來 → 這筆「協調延遲」本身就可能**超過**它想外包掉的 −15.4% 維護。
- **`NodeStates`(main 寫、worker 讀)**:這是**最熱、L1 常駐(14.7KB)**的陣列。Worker 要讀它
  來算 Dominant,但 main 一直在 `SetNodeState` **寫**它 → read-write sharing 把**主執行緒的
  L1 熱資料**打成跨核乒乓 → 直接拖垮 main 自己。
- **False sharing**:就算 main / worker 邏輯上碰不同節點,只要落在同一條 64-byte cache line
  就會互相失效。`DominantGate`(2 bytes/節點 ⇒ 一條 line 擠 32 個節點)特別容易中。

### 3.4 被外包的紅利只有 +3.4%,外包機制的開銷大概率更大

就算平行化「完美」,天花板也只是那 +3.4% 的 skip 紅利(還要扣掉 main 仍要付的讀取/同步)。
而 3.2、3.3 的同步 + 一致性 + wait 成本,單看就很可能吃掉 +3.4% 還倒貼。**期望值是淨負。**

### 3.5 專案前例:平行化在這裡已經敗過兩次,同一個根因

- **Per-chip parallel(rayon::join)**:15× 慢。
- **Bit-parallel BFS(Ligra dense scan)**:156× 慢。
- 共同根因:**每波/每次走訪的工作量太小,攤不掉同步開銷;引擎 memory-latency-bound,加核
  心只會加一致性流量、不會加吞吐**。本設計撞同一道牆,而且**多了 3.1 的緊耦合相依**,比前
  兩次更難。

---

## 4. 若真要嘗試,可探索的緩解方向(列出以求平衡;成功機率仍低)

- **雙緩衝 + 落後一拍**:worker 用上一個 half-cycle 的**快照**算 `DominantGate`,main 只在
  「該節點本拍未被改動」時才信任它套 skip。問題:要證明「未改動」本身又要追蹤狀態,且落後
  一拍的正確性界線很難守(稍有不慎即非 bit-exact)。
- **波邊界(wave-boundary)粗粒度同步**:worker 以整個結算波為單位處理,main 只對 worker
  已完成的節點套 skip。需要把節點空間切成 main / worker **不共用 cache line** 的兩塊——但兩
  者都得碰 `NodeStates`,難以真正切開。
- **改變寫入目標的接觸頻率**:worker 把結果寫到一塊「main 只在波邊界讀一次」的區域,降低
  read-write 的頻率。仍要付邊界同步的一致性成本。
- **(出局)近似 skip + 週期性全解析校正**:會破壞 bit-exact,違反專案硬規,不採。

---

## 5. 風險判定與建議

**判定:在目前的引擎結構下,雙核切割 dominant-bypass 大概率淨負**,理由是 3.1 的緊耦合相依
讓「切開」必然引入 3.2 的等待/序列化,加上 3.3 在 latency-bound 引擎上的跨核一致性流量,
三者合計很可能超過被外包的 −15.4%、更遠超 +3.4% 的天花板;且與專案兩次平行化死路同根。

**建議的最便宜驗證(投入前先做,別直接實作整套)**:
寫一個**極小的微基準**——兩條各釘一核的執行緒,worker 反覆**寫** `DominantGate[]`、main 反覆
**讀** 同一塊(都不做別的事),量「純跨核 read-write sharing」的延遲懲罰。若這個**下限成本**就
已經 ≥ +3.4%,整個方向即可判死,無須實作 wait/雙緩衝。這符合本專案
「minimal prototype 先證明能贏、再投入」的鐵則(IR 當年就是沒先驗證才白做)。

> 一句話總結:這不是「把一塊閒置計算丟去別核」,而是「把一條**逐節點、同波就要用**的相依鏈
> 硬切兩半」;切點兩邊互等,加上 latency-bound 引擎最怕的跨核一致性流量,讓它在物理上難以
> 贏過單核——除非先用微基準證明跨核共享的下限成本低於 +3.4%。
