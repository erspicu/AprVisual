# AccuracyCoin 在 `$2007 Stress Test` 讓 S1 引擎無限震盪(掛住不收斂)

> **交接文件。** 給接手的人 / AI agent。內容包含:現象、可重現步驟、已確立的證據、根因分析、
> 被推翻的假設(避免重蹈覆轍)、修復方向。
>
> 建立於 2026-07-11。狀態:**未修復,已定位**。

---

## 0. 一句話

**AprVisual.S1 跑 AccuracyCoin 到 `$2007 Stress Test` 時,callback 層的 settle 迴圈永遠不收斂 —— 引擎
不崩潰、不報錯,就是單核 100% CPU 無限空轉,永遠卡在 frame 4480。**

這**不是**效能問題,是**引擎的不收斂缺陷**(non-convergence)。

---

## 1. 現象

在 `--ac-verdict` 模式跑 `AprAccuracyCoinUnattended/AccuracyCoin.nes`:

| 觀察 | 值 |
|---|---|
| 卡住的幀 | **frame 4480**(兩次獨立執行完全相同 → **確定性、非隨機**) |
| 行程狀態 | **活著**,沒有 exception、沒有 stack overflow、沒有任何錯誤輸出 |
| CPU | **單核 100%**(實測 20 秒牆鐘燒掉 19.9 秒 CPU) |
| 前進 | **零**。正常每 10 幀約 56 秒出一個 checkpoint;卡住後 19+ 分鐘沒有任何新 checkpoint |
| 記憶體 | 穩定,不成長(不是記憶體洩漏) |

**危險之處:它長得跟「跑得很慢」一模一樣。** 沒有任何錯誤訊息。如果沒有 progress checkpoint 機制
(`--progress-frames`),這個問題會被誤判成「switch-level 就是慢」而放它跑上好幾天。

---

## 2. 重現步驟

環境:Windows,.NET 11 SDK,x64。

```bash
cd C:\ai_project\AprVisual
dotnet build src/AprVisual.S1 -c Release

dotnet src/AprVisual.S1/bin/Release/net11.0/AprVisual.S1.dll \
  --test AprAccuracyCoinUnattended/AccuracyCoin.nes \
  --ac-verdict \
  --max-frames 7305 \
  --reset-hold-extra 1 \
  --progress-frames 10 --progress-dir <某個目錄> \
  --system-def-dir AprVisualBenchMark/data/system-def
```

**約 6.9 小時後**(單核,~5.56 秒/模擬幀)會停在 frame 4480 不再前進。

### 重現成本的警告

**這是最大的痛點:要撞到這個 bug 得先跑 6.9 小時。** 目前**沒有** checkpoint/resume 機制,無法跳到
frame 4480。任何要驗證修法的人都會付這個代價。

**強烈建議接手者第一件事就是想辦法降低這個成本**,例如:
- 加狀態快照/還原(在 frame ~4400 存檔,之後從那裡重跑)
- 或做一顆最小重現 ROM(只跑 `$2007 Stress` 那段邏輯)
- 或加不收斂偵測器(見 §7),讓它一撞到就吐出現場,而不是靜靜空轉

---

## 3. 禍首:`$2007 Stress Test`

用行為層模擬器 **AprNes** 當快速 oracle(它跑同一顆 ROM 完全正常、141/141 全過)反查:

AccuracyCoin 把各測試結果存在 `$0400-$04FF`,一顆測試一個 byte(定義見 `AccuracyCoin.asm`)。
以不同的主機時間點 dump RAM,看哪個 byte 何時被填:

| 主機時間 | ~frame | 已完成 |
|---|---|---|
| 73–76 s | 4387–4568 | `2004_Stress`, `2002FlagClearTiming`, `StaleSpriteShiftRegs` |
| **77 s** | **4628** | 上述 + **`2007_Stress` 完成** |

→ **`2007_Stress`(`result_2007_Stress = $048E`)從 frame ~4387 一路執行到 ~4628。**
→ **我們的引擎死在 frame 4480,正落在這個區間正中央。**

### 為什麼是這顆,說得通

- `$2007` 是 **PPU 資料埠**。這顆測試會狂敲它。
- 在 S1 裡,**VRAM(`u4` = SRAM2K)是行為層 callback**,不是電晶體級 —— 它要驅動 PPU 的 VRAM 資料匯流排。
- `$2007` **本來就是 S1 的已知痛點**:我們早就有一個 `Dbl2007Shim`(`WireCore.System.cs:639`,
  「$2007 double-read merge,文件化的類比傳播 shim」)在補這一區,因為純開關級傳播表達不出真實的類比行為。
- `AprAccuracyCoinUnattended/README.md` 裡也早就寫過:**「`$2007 Stress Test` 對 CPU/PPU 進入時序很敏感」**。

**這一區的物理,S1 的二值模型本來就吃力。現在它從「不準」升級成「不收斂」。**

---

## 4. 根因分析

### 4.1 引擎的 settle↔callback 結構

```
StepCycle()                              (WireCore.Recalc.cs:813)
  └ ProcessQueue()                       (Recalc.cs:356) 節點 settle(迭代式 wave 迴圈)
       └ InvokeCallbacks()               (Recalc.cs:417 → Handlers.cs:148)
            └ while (_pendingCallbacks.Count > 0)     (Handlers.cs:168)  ← ★ 死迴圈在這裡
                 └ DoMemReadWrite(cb) 等 callback
                      └ WriteBits(...)   (Handlers.cs:296/318/339)
                           └ if (changed) ProcessQueue();   ← 匯流排變了,重新 settle
                                └ (再度入列 callback)
```

**機制**:某個 callback 驅動匯流排 → 節點 settle → 這個變化又觸發別的 callback 入列 →
最外層的 `while (_pendingCallbacks.Count > 0)` 又撈起來跑 → 又驅動匯流排 → …**永遠不會空**。

典型形態:**兩個以上的 callback 在同一條匯流排上打架**(A 驅動成 X → 觸發 B → B 驅動成 Y → 觸發 A →
A 又驅動成 X → …),二值引擎沒有任何機制能化解,真實硬體則靠類比行為(匯流排衝突 / 電容保持 / open bus 衰減)自然定下來。

### 4.2 `dotnet-trace` 實證(不是推測)

對卡住的行程抓 CPU trace,最深堆疊只有 **2 層**:

```
RunFrame → StepCycle → InvokeCallbacks → ProcessQueue → [CPU 100% 停在這]
```

這證明兩件事:

1. **不是遞迴爆堆疊** —— 堆疊沒有長高。
2. **是 callback 層的 drain 迴圈在空轉** —— 不是節點層 settle 不收斂。

證據檔:`temp/ac/hang.speedscope.json`(可直接丟進 https://speedscope.app 看)。

### 4.3 為什麼沒有任何保護擋下來

- **節點層** settle 有不收斂保護:`MaxSettlePasses = 128`(`Recalc.cs:347`)——
  **但那是 `#if DEBUG` 才編進去的,Release 完全沒有。**
- **callback 層**的 `while (_pendingCallbacks.Count > 0)` drain 迴圈 —— **連 DEBUG 都沒有任何上限。**

→ **所以一旦震盪,Release 就是無聲無息地永遠空轉。這本身就是一個必須修的缺陷,與 AccuracyCoin 無關。**

---

## 5. ⚠️ 被推翻的假設(千萬別重蹈覆轍)

這一節比其他任何一節都重要。以下都是**實際發生過的錯誤判斷**。

### 5.1 「stack overflow 是因為遞迴太深,加大堆疊就好」→ 錯

最初的症狀是 `Stack overflow`,`InvokeCallbacks` 在堆疊裡重複 **24,021 次**。
第一個修法是把測試跑在 512MB 堆疊的執行緒上(commit `528fe1f`)。

**那是 band-aid,而且是餵飽 bug,不是修它。** 已被 `718ca6c` revert。

### 5.2 「`InvokeCallbacks` 的再進入 bug 就是根因,修好就沒事」→ **只對一半**

確實找到一個**真實的 bug**(兩次 Gemini 諮詢 + 手動追蹤共同確認):

> `_pendingCallbacks` / `_processingCallbacks` 是**共用的 static List**。巢狀呼叫 `InvokeCallbacks`
> 會在外層 `for` 迴圈**迭代到一半時把兩個 List 交換掉**,導致 callback 被重複執行、順序錯亂、
> 外層迴圈提早結束(tearing)。

修法是一個 re-entrancy guard(commit `0b6cebd`,**已 merge 進 main**:`e5396da`):
只有最外層的 `InvokeCallbacks` 會 drain,巢狀進入立即返回。

**這個 guard 是好的,應該留著**:
- 它修掉了真實的 tearing bug
- 它消滅了 24k 遞迴 → **stack overflow 不再發生**
- 它是 **bit-exact 的**(見 §6)

**但它沒有修好真正的病。** 它把**崩潰**變成了**當機**:

| | 無 guard | 有 guard |
|---|---|---|
| frame 4480 | 遞迴 24k 層 → **Stack Overflow 崩潰** | **無限迴圈空轉**(不崩、不動) |

> ⚠️ **main 上的 merge commit `e5396da` 訊息寫「fixes the AccuracyCoin stack overflow」——
> 這句話會誤導人。它修的是 overflow,不是 hang。AccuracyCoin 仍然跑不完。**

### 5.3 「這個 cascade 是有限但很深」→ **錯,它是無窮震盪**

當時無法判定 24k 深度是「有限但深」還是「無窮震盪」。Gemini 認為是震盪;
我(Claude)傾向「有限」,並據此宣稱 guard 讓遞迴深度結構性封頂、可以放心跑。

**Gemini 是對的,我是錯的。** guard 只保證了**堆疊安全**,沒保證**收斂**。
拿掉遞迴後,它就從「一路遞迴到堆疊爆掉才停」變成「純粹的死迴圈」。

**教訓:「堆疊深度封頂」≠「迴圈會終止」。這是兩件事。**

---

## 6. 已確立的事實(可以信任,不用重驗)

### guard 是 bit-exact 的

在把 guard 併入前做過完整 A/B(guard 版 vs 無 guard 版,同樣的 ROM 與參數):

| 驗證 | 結果 |
|---|---|
| 黃金 checksum @300k hc | `0x794A43ABDF169ADA` — **不變** |
| 黃金 checksum @1M hc | `0x6D4CCBCE2E9CD599` — **不變** |
| 6 顆 I/O 密集 ROM @ **2M** hc | **6/6 逐位元相同** |
| 同上 @ **20M** hc | **6/6 逐位元相同** |

(6 顆:`full_palette` / `11-special` / `oam_read` / `10-even_odd_timing` / `1-len_ctr` / `01.basics`)

→ **不需要重新定基準(re-baseline)。** guard 消掉的冗餘重跑在我們的 callback 集合
(RAM/ROM/OAM/video)上都是**等冪的** —— 舊 checksum 記錄的是白工,不是壞掉的狀態。

> 註:Gemini 曾舉「讀 `$2002` 會清 VBlank 旗標」當非等冪的例子。**那個例子不適用 S1** ——
> `$2002`/PPU 在我們這裡是**電晶體級**建模,不是行為層 callback。會被重跑的 callback 只有
> RAM/ROM/mapper/video/joypad,而那些是等冪的。

### 再進入是普遍現象,但正常 ROM 很淺

DEBUG 版有 guard telemetry(`# [guard] nested entries absorbed: total=… max-per-drain=…`)。
跑 `11-special`(11 幀就判定):

```
total = 278,515        ← 再進入非常頻繁,guard 一直在動
max-per-drain = 1      ← 但正常 ROM 的深度只有 1
```

→ 這解釋了**為什麼 147 顆回歸測試從來沒爆過堆疊、也沒被 tearing 咬到**:深度 1、單一項、等冪重跑。
**24k 的暴衝是 AccuracyCoin `$2007 Stress` 獨有的。**

### 147 顆回歸測試不受影響

guard 進 main 之後,黃金 checksum 不變,`--test` 路徑行為不變。這個 hang **只發生在 AccuracyCoin**。

---

## 7. 建議的修復方向(依優先序)

### P0. 先加「不收斂偵測器」——不管最後怎麼修,這個都該做

現在的行為是**無聲空轉**,這在任何情況下都不可接受。

在 `InvokeCallbacks` 的 drain 迴圈(`Handlers.cs:168`)加一個迭代上限(例如 10,000),超過就:
1. 印出**正在震盪的 callback**(`cb.Name`、`cb.Kind`、`cb.TargetNode`)
2. 印出它們 **watch 的節點目前的狀態**(`cb.WatchedNodes` + `NodeStates`)
3. 印出當下的 **frame / hc / PC**
4. 然後 **abort 或截斷**(見 P1 的取捨)

**這樣下次撞到就有現場證據,不用再燒 6.9 小時猜。**
順帶一提,節點層的 `MaxSettlePasses` 保護目前也只在 DEBUG(`Recalc.cs:347`),Release 沒有 —— 一併考慮。

> ⚠️ 注意:這是**引擎最熱的路徑**。加計數器要小心效能與 bit-exactness。
> 一個安全做法:只在迭代數超過某個「絕對不可能的正常值」時才做事(例如 >1000),
> 正常路徑只多一個 `int++` 和一次比較。**改完必須重驗黃金 checksum。**

### P1. 判斷這個震盪的「正確答案」是什麼,再決定怎麼收斂

需要先回答:**真實的 2C02 在這個 `$2007` 存取樣式下,匯流排到底定在什麼值?**

拿到 P0 的現場資料(是哪兩個 callback、哪條匯流排在打架)之後,可能的方向:

- **(a) 匯流排衝突 shim**:如果是 VRAM callback 與 PPU 內部驅動在搶同一條匯流排,
  補一個文件化的 test-mode shim(和既有的 `Dbl2007Shim` / `OamDmaPpuBusShim` 同一個路數),
  讓它照真機的結果定下來。**這是最可能的方向**,因為 $2007 這一區我們已經有前例。
- **(b) 截斷 + 記錄**:超過上限就停止 drain、保留當下狀態繼續跑。
  ⚠️ **但要非常小心**:專案筆記已經證實「under-settle 截斷是災難」
  (`MD/note` → `under-settle-is-catastrophic`:截掉最深的 0.58% 就會發散)。
  截斷很可能讓後續結果全錯。**不建議,除非有強證據。**
- **(c) 承認是語意極限**:如果真機是靠類比行為(電容、匯流排衝突的類比解)化解,
  而 S1 的二值模型**原理上**表達不出來,那就記錄成「忠實偏差」,
  和 `cpu_dummy_writes_oam` 同一類處理(見 `MD/testrom/` 的忠實偏差 Q&A)。

### P2. 降低重現成本

見 §2 的警告。**在做 P1 之前先做這個,否則每次驗證都要 6.9 小時。**

---

## 8. 相關程式碼位置

| 檔案:行 | 內容 |
|---|---|
| `src/AprVisual.S1/Sim/WireCore.Handlers.cs:148` | `InvokeCallbacks()` — guard 在這裡 |
| `src/AprVisual.S1/Sim/WireCore.Handlers.cs:168` | **`while (_pendingCallbacks.Count > 0)` ← 死迴圈就在這一行** |
| `src/AprVisual.S1/Sim/WireCore.Handlers.cs:296/318/339` | `WriteBits(...)` 的 `if (changed) ProcessQueue();` — 閉合迴圈的那一步 |
| `src/AprVisual.S1/Sim/WireCore.Recalc.cs:356` | `ProcessQueue()` — 節點 settle(迭代式,本身不遞迴) |
| `src/AprVisual.S1/Sim/WireCore.Recalc.cs:417` | `ProcessQueue` 尾端呼叫 `InvokeCallbacks()` |
| `src/AprVisual.S1/Sim/WireCore.Recalc.cs:347` | `MaxSettlePasses = 128`(**僅 DEBUG**,Release 無保護) |
| `src/AprVisual.S1/Sim/WireCore.System.cs:639` | `Dbl2007Shim` — 既有的 $2007 shim,修法可參考它的路數 |
| `src/AprVisual.S1/Test/TestRunner.Test.cs` | `--ac-verdict` 判定($07F0 完成區塊)+ `--progress-frames` |
| `AprAccuracyCoinUnattended/AccuracyCoin.asm:313` | `result_2007_Stress = $048E` |

### 相關 commit

```
528fe1f  wip(test): big-stack 512MB thread stopgap        ← band-aid,已 revert
0b6cebd  fix(sim): re-entrancy guard in InvokeCallbacks   ← 真的修了 tearing + overflow(該留)
718ca6c  revert(test): drop the big-stack stopgap
7895bc7  fix(test): clear stale checkpoints before a run
e5396da  merge: ...(訊息宣稱 "fixes the AccuracyCoin stack overflow" ← 只對一半,hang 仍在)
```

---

## 9. 證據檔案

**已隨本文件一起 commit 進 repo**(`MD/toDoNext2/evidence/`),clone 下來就看得到:

| 檔案 | 內容 |
|---|---|
| `evidence/hang-callback-drain.speedscope.json` | **卡住行程的 CPU trace**。丟進 <https://speedscope.app> 直接看。證明:堆疊只有 2 層(guard 有效、非遞迴)、CPU 100% 卡在 `InvokeCallbacks → ProcessQueue`。**這是不重跑 6.9 小時就拿不到的證物。** |
| `evidence/noguard-stackoverflow.txt` | 無 guard 版的 `Stack overflow` + `Repeated 24021 times` 堆疊(見下方⚠️) |
| `evidence/last-checkpoints-both-runs.txt` | 兩次獨立執行的最後 checkpoint —— **都停在 frame 4480** |

⚠️ **一個誠實的證據瑕疵**:無 guard 版的 `run.err.log` **原始檔已被我覆蓋掉**。
2026-07-11 12:30 重啟長跑時,PowerShell 的 `Rename-Item` 因目錄被佔用而失敗,但 `Start-Process`
仍然啟動了引擎,其 `RedirectStandardError` 把舊 log 截斷覆寫。
`evidence/noguard-stackoverflow.txt` 裡的堆疊是**轉錄**的(出處已在該檔註明),
但 `progress.jsonl` 因為是 append 模式所以倖存,可**獨立佐證**停在 frame 4480。
commit `0b6cebd` / `528fe1f` 的訊息也記錄了 `~24021` 這個數字。

僅存於本機(gitignored,不在 repo 裡):

| 路徑 | 內容 |
|---|---|
| `tools/testrom/out/ac_crashed_noguard/` | 無 guard 版的完整執行目錄(448 checkpoint) |
| `tools/testrom/out/ac/` | guard 版的完整執行目錄(448 checkpoint,log 裡**一個錯誤都沒有**) |
| `tools/knowledgebase/message/20260711_101810.txt` | Gemini 第一次諮詢(找出 re-entrancy tearing) |
| `tools/knowledgebase/message/20260711_103943.txt` | Gemini 第二次諮詢(guard vs trampoline 取捨) |

> 兩次執行的 checkpoint **都是 448 筆、都停在 frame 4480** —— 一次是崩潰、一次是當機。
> 這個「同一幀」本身就是**確定性、同一個病灶**最有力的證據。

---

## 10. 給接手者的建議順序

1. **讀 §5(被推翻的假設)。** 別再走一次那些死路。
2. **做 P0(不收斂偵測器)。** 沒有現場資料,後面全是猜。
3. **做 P2(降低重現成本)。** 否則每次驗證 6.9 小時。
4. 拿到現場資料後,再回頭決定 P1 的 (a)/(b)/(c)。
5. **任何動到 `WireCore` 熱路徑的改動,改完一定重驗黃金 checksum**
   (`--benchmark full_palette.nes --bench-hc 300000 --extra-ram` → 必須是 `0x794A43ABDF169ADA`)。

### 順便提醒:AccuracyCoin 的其他價值不受影響

即使這顆 `$2007 Stress` 卡住,前面 **~130 顆測試是跑得完的**(frame 4480 已經是 92% 進度)。
如果只是要拿 AccuracyCoin 的成績單,可以考慮:先讓引擎跳過/截斷這一顆,
或改 ROM 的測試順序把它排到最後 —— 但**那是繞路,不是修好**,而且會讓成績單有個星號。
