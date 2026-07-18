# 血淚教訓與陷阱(2026-07-18 一天內全部踩過)

背景:當天 session 在 Fable 5 上多次壓縮失憶,連續踩了下面的坑,由使用者逐一糾正。
記在這裡是為了**下個 session / 換模型後不要重犯**。

## 1. ALEREAD_MUX 是 env-gated opt-in,重跑 AC 極易漏設
- 漏設 → ALERead 測試 FAIL → **140/141**(而非掛牌的 141/141)。
- 它必須在**進程啟動前**設好(LoadSystem 前讀,要切 node-split),快照 resume 不了,得從零重發。
- Source `TestRunner.Test.cs:130`;commit `89370de`;完整配方見 [00](00_baseline-and-run-recipes.md)。
- **鐵律:重跑 AC 前對照 `banked-136-of-141` 記憶 / 本目錄 00,把 env 抄齊,並用 MUX_DBG 確認 armed。**

## 2. K=0 配方假警報(整條「翻籤/回歸」敘事被收回)
- 症狀:孤立重跑 `oam_read`/`oam_stress`/`05-nmi` 呈 FAIL,一度誤判成「合法翻籤、預期 145/2」。
- 真因:**campaign runner 對每個測試都帶 `--reset-hold-extra 1`(K=1)**(`run_tests.py:154`),
  而我的孤立臂全用 plain(K=0)→ 跨時代比較整批無效(`05-nmi` 本就記載 K∈{0,5} 會倒)。
- 收回過程:先誤怪 t13032b 補管(拔掉取證 hc 逐位相同 → 否證),再誤怪 ALERead node-split
  (是 opt-in、plain 不切 → 否證),最後定位是 K=0 配方錯。**146/1 從未動搖。**
- **鐵律:任何孤立重驗前,先讀 runner 原始碼確認它當時用的配方(K、旗標、核表),再設計臂。**

## 3. 別土製工具(正牌的都在磁碟上)
- 當天兩度重造輪子:自寫 AC 哨兵(漏開 `--test-json` → 判決檔不生)、用錯 ROM 當對照
  (oam_read 根本沒有 $4014 DMA,拿來驗 OamDmaPpuBus 是空臂)。
- 正牌工具:`ac_watch.py`、`ac_snap_results.py`、`run_tests.py`;教學:`MD/testrom_workflow/`
  (標題自述「防遺忘」)、`AprAccuracyCoinUnattended/README.md`。
- **鐵律:動手重跑前,先 `ls tools/testrom/` + 讀 `MD/testrom_workflow/`,別憑記憶造工具。**

## 4. SHA/SHS 3 格「與 oracle 不同」不是 FAIL
- `$446/$447/$448` = $05(S1 變體 1),oracle = $09(AprNes 變體 2),**兩個都是奇數 = 都通過**。
- 不穩定指令的多個合法變體;run8 亦是 $05。詳見 [01](01_ac-verdict-mechanism.md)。
- **鐵律:看到 oracle DIFFERS,先查 README_org Success Codes 是不是多變體、再用奇偶規則判,別喊回歸。**

## 5. 抽籤測試(OAM 家族)本來就會合法換邊
- `oam_read`/`oam_stress`:blargg 真機開機 4 圖樣、僅 1 過,抽籤本身才是忠實行為。
- 載入期圖變更(Socket Pattern)會重擲彩票 → 可能合法換邊;這**不是引擎退步**。
- 但 2026-07-18 那次「翻籤」其實是 §2 的 K=0 假警報,不是真換邊——**先排除配方錯,再談換邊**。

## 6. 量測環境鐵律(其他記憶檔已載,提醒)
- **這台 Zen2 不鎖頻**(鎖頻恢復後電腦會怪);一次只跑一個 benchmark;釘核用偶數邏輯編號避 SMT 撞核。
- 網表一律用修正版 `AprVisualBenchMark/data/system-def/`;缺陷(補丁前)網表只准法醫取證、用完即刪。
- 相關記憶:`no-clock-lock-on-this-machine`、`testrom-batch-scheduling`、`netlist-use-corrected-not-raw`、
  `ac-test-toolchain-and-verdict-flow`。

## 元教訓:為什麼會失憶
Claude 的工作記憶 = 對話 context;壓縮或換模型會丟沒寫下來的操作細節。**唯一跨 session/跨模型
活下來的是磁碟上的檔案**(本目錄、`MEMORY.md`、`CLAUDE.md`、`MD/`、git)。所以重要操作事實要
落地成檔案,不能只留在對話裡。
