# 掛牌基準與逐字重跑配方

## 掛牌基準(這是「完成態」,重跑要重現這個)

| 測試集 | 成績 | 何時 | 說明 |
|---|---|---|---|
| **AccuracyCoin 無人版(141 測)** | **141/141,0 skipped** | 2026-07-17 **run8**(4,925 幀 / 8.1h) | 分支 `aleread-ioce-mux` 上以 shim + ALERead mux 達成後合併回 main |
| **blargg 家族(147 測)** | **146/1** | 2026-07-09/10 | 唯一 FAIL = `cpu_dummy_writes_oam`(**忠實偏差**,實機亦然,非 bug) |

演進:127 → 136(7/14 run5b:joyON + R4015 網表補丁 t13032b)→ 140(7/16 run7:ALERead node-split mux)
→ **141**(7/17 run8:BGSerialIn reload-delay shim)。來源:記憶 `banked-136-of-141`、
`WebSite/ReportAC/index.html`、`MD/testrom/00_測試修復知識庫_總綱.md`。

---

## AC 無人版:逐字掛牌配方(run8)

**CLI**(`src/AprVisual.S1/bin/Release/net11.0/AprVisual.S1.exe`):
```
--test AprAccuracyCoinUnattended/AccuracyCoin.nes
--ac-verdict                 # 讀 $07F0 完成區塊判定;同時自動關閉 cart-extraram(保住 open-bus 測試)
--joypad                     # 行為層手把;6 顆 DMC-DMA/手把家族測試需要它才過
--reset-hold-extra 1         # K=1 開機對齊(時鐘相位敏感測試需要)
--callback-drain-limit 2000
--max-frames 12000           # kill guard(判決約 4,870-4,925 幀)
--snapshot-frames 10         # 每 10 幀全狀態快照(中途讀表 + 當機續跑)
--system-def-dir AprVisualBenchMark/data/system-def   # 修正版網表
```

**環境變數(關鍵!CLI 看不到,但缺了會掉分)**:
```
ALEREAD_MUX=1                # ★必設★ 武裝 ALERead $2007 phase-mux + node-split
MUX_HC=13,13,25,44,52        # mux 時序(與碼預設同值;為逐字比對 run8 照設)
```

### ⚠️ ALEREAD_MUX 是最容易漏的一個
- **它是 env-gated opt-in**,不設 = ALERead 測試(`result_ALERead=$491`)FAIL = **只有 140/141**。
- Source:`src/AprVisual.S1/Test/TestRunner.Test.cs:130`
  `AleReadMuxShim = Environment.GetEnvironmentVariable("ALEREAD_MUX") != null;`
- 它在 **LoadSystem 前**被讀,因為要切 `ppu.io_ab[2:0] <-> cpu.ab[2:0]` 的 node-split
  (`src/AprVisual.S1/Sim/WireCore.Module.cs:352`)—— **不能中途開,快照也 resume 不了,必須從零帶 env 重發**。
- Commit **`89370de`**「feat(aleread): node-split + ale-freeze mux BREAKS ALERead -> PASS (1/1)」證明:
  mux 讓 ALERead 1/1 PASS、opt-in via ALEREAD_MUX、金 checksum 不變。
- 確認 armed:設 `MUX_DBG=1`(只加 stderr、不改模擬,`WireCore.System.cs:737`),看到
  `# [mux] armed (node-split): sw=13 rp=[13,25) fz=[44,52)` 即武裝成功。

### 其他 env(不要亂設)
- `NO_ABORT_SHIM / NO_BGS_SHIM / NO_DL_SHIM / NO_OAMEDGE_SHIM / NO_OB_SHIM`:這些是**關 shim** 的,
  **不設 = shim 全開(正確)**。掛牌要它們全開,所以一個都別設。
- `*_DEBUG / MUX_DBG / OB_DEBUG / PC_WIN`:純除錯輸出,不影響結果。
- `--ac-verdict` 會自動:關 extra-ram(`ForceExtraRam = !_acVerdict`,`TestRunner.Test.cs:144`)、
  開 PpuAleReadFeedbackShim;其餘 shim(LXA/FrameIrq/DmcLatch/AluLatch/dot-339/BGSerialIn/OpenBus/
  DL/abort/OamBlankEdge/OamDmaPpuBus/Dbl2007)預設全開(除非對應 NO_*)。

### 中途讀成績(引擎零干擾)
```
python tools/testrom/ac_snap_results.py --dir <snapshot-dir>   # 或 --snap <file.sav>
```
從最新 `.sav` 的 MEMS 段讀 `$0400-$04FF` 結果表,對 `temp/ac/t200.txt` 的 AprNes oracle 逐位比對。

---

## 147 blargg 全量:逐字配方

```
python tools/testrom/run_tests.py --rerun --jobs 6 --no-build
```
- **每個測試自動帶 `--reset-hold-extra 1`(K=1)**(`run_tests.py:154`)—— 孤立重跑若漏這個,
  跨時代比較無效(見 [03](03_lessons-and-gotchas.md) K=0 假警報)。
- **canary 先驗**金 checksum `0x794A43ABDF169ADA`(300k hc + `--extra-ram`)+ `$6000` 路徑,
  不過就拒跑(`run_tests.py:84-126`)。
- **6 lane 釘核**:邏輯 `2,6,10,14,4,12` = 實體 `1,3,5,7,2,6`(`CORES` @ `run_tests.py:44`);
  實體 0 留 OS、實體 4 留給 AC 那顆。全偶數邏輯編號 = 零 SMT 撞核。
- **extra-ram**:非 `--ac-verdict` 模式 `ForceExtraRam=true`(blargg 需要 $6000 work RAM)——
  與 AC 相反(AC 要 open-bus,故關)。
- catalog(`tools/testrom/catalog.json`,147 筆)自動套用:class B → `--screen-verdict`、
  class C(2 筆)→ `--expected-crc`、needsJoypad(7 筆)→ `--joypad`、test_buttons → `--input`、
  count_errors 兩顆 → `--pass-marker`。分布:85 A + 8 A-r + 52 B + 2 C = 147。
- 系統核避讓與 SMT 規則見記憶 `testrom-batch-scheduling`、`no-clock-lock-on-this-machine`。

---

## 成績 = 三個變數的乘積
**引擎 build × 網表 × 測試配方**。任何一個不同都不能跨時代比:
- 引擎:金 checksum `0x794A43ABDF169ADA` 鎖 bit-exactness(`src/AprVisual.S1/` 自合併 `b01a1c3` 起零 commit)。
- 網表:修正版 `AprVisualBenchMark/data/system-def/`(含 t13032b/t14634b 補管)。
- 配方:上面兩套(AC 的 CLI+env、147 的 runner)。
