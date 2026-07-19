---
name: tonight-4engine-batch
description: 2026-07-19 晚上並行 4 引擎批次(1 milestone mech 臂 + 3 ctrl 臂);記錄全隔離、判讀法、翻牌條件
metadata:
  type: project
---

# 2026-07-19 晚並行 4 引擎批次(約凌晨 2 點完成)

發射時間 ~19:15;各釘不同實體核、各自獨立目錄(記錄零污染,使用者硬性要求)。
watcher = background job(等 4 個 AprVisual.S1A.exe 全退出 → dump 全部判定 + 各 ctrl FAIL 子測試)。

## 4 個 run(engine PID 起跑時)
| run | core | 設定 | 出處目錄 | 角色 |
|---|---|---|---|---|
| MILESTONE | 8 | **全 8 機制開**(M4_EDGE M6X M4_P1 M1_LXA M4_FI M4_OE M3_ABORT PPU_ALE_FB) | `out/ac_s1a_milestone/` | **mech 臂**(一次驗全部退役) |
| ctrl dot339 | 6 | baseline + `--ppu-write-delay-global 0` | `out/ac_ctrl_dot339/` | dot-339 ctrl |
| ctrl oamedge | 10 | baseline + `NO_OAMEDGE_SHIM=1` | `out/ac_ctrl_oamedge/` | OamBlankEdge ctrl |
| ctrl ppualefb | 12 | baseline + `--no-ppu-ale-read-feedback-shim` | `out/ac_ctrl_ppualefb/` | PpuAleReadFeedback ctrl |

- baseline = 認證 141/141 recipe(`run_accuracycoin.bat`:`ALEREAD_MUX=1 MUX_HC=13,13,25,44,52`,**無**機制 env);
  ctrl = baseline 減一顆 shim。launcher = `tools/testrom/run_ac_s1a_ctrl.bat <name> <core>`、milestone = `run_ac_s1a_milestone.bat 8`。
- **關鍵:dot-339 的 default 防守 = `_ppuWriteDelayGlobalHc=24`(TestRunner.cs:40,預設就開)**,關它用
  `--ppu-write-delay-global 0`,不是 NO_*_SHIM。(TestRunner.Test.cs:204 那句「OFF unless」註解誤導。)

## 翻牌條件(明早讀 watcher 輸出)
- **MILESTONE = 141/141** → 8 機制可套內同時取代 shim → 9 顆已 PROVEN 可正式 RETIRE(default-flip);掉題=逐題點名不足機制。
- **dot339 ctrl FAIL StaleSpriteShiftRegs** → dot-339 可判定 → PROVEN(配 milestone mech 臂)。
- **oamedge ctrl FAIL Address2004_Behavior 或 StaleSprite** → OamBlankEdge 可判定 → PROVEN。
- **ppualefb ctrl FAIL 任一題** → PpuAleReadFeedback 可判定 → PROVEN。
- 若某 ctrl 仍 141/141 → 該顆維持 UNDECIDABLE(誠實,別硬翻)。dot-339/OamBlankEdge 共用 StaleSprite,注意互動。
- **DL 不在這批**(刻意延後,見 [[dl-shim-deferred-rationale]])。

## ⚠️ 事故 + 教訓(2026-07-20 01:41):別編輯執行中的 .bat
milestone 跑到 f4920 後**自己重啟**了。根因:我在 cmd 正執行該 .bat 時**編輯它**(加 auto-resume)—— Windows
cmd 逐行讀 bat、記位元組偏移,插入行使 EXE 結束後 cmd 重讀錯位 → 又開一個新 milestone。**鐵律:bat 一旦
被 cmd //c 跑起來就別再改;要改先停。** 修復:保住 `state_f004920.sav` → 殺重啟 EXE + 全部 run_ac cmd
wrapper(`Stop-Process -Id` 不帶 /T → 子 EXE 存活)→ 3 個 ctrl 變 orphaned EXE 繼續跑完、不再重啟。

## ✅ MILESTONE mech 臂 = 141/141(2026-07-20 恢復確認)
從 f4920 快照直接用 EXE(不經 bat)`--resume` 續跑 5 幀 → f4925 suite 完成 → **141/141 passed, 0 fail**。
結果在 `out/ac_s1a_milestone_recovered/`(原 `ac_s1a_milestone/` 已被重啟污染,以 recovered 為準)。
→ **全 8 機制套內同時取代 shim 成立;9 顆 PROVEN 可正式退役;4 顆新機制的 mech 臂也套內成立**(待 ctrl 臂)。
AC verdict json 格式:`status`/`resultText`("N/141 passed"),**無 per-subtest 陣列**;per-subtest PASS/FAIL 在
**stdout log**(`# [ac] result table at exit` + 每位址 `$XXX=$YY PASS/FAIL variant`)→ ctrl 判 FAIL 子測試要 grep log。

## ctrl 結果(陸續)
- **ppualefb ctrl(`--no-ppu-ale-read-feedback-shim`)= 決定性判定(2026-07-20 ~02:5x)**:引擎在 ~f4350
  丟 `InvalidOperationException: non-converging callback drain (limit=2000, batches=1000)`(Handlers.cs:304)。
  這正是 PpuAleReadFeedback 防的:拔掉 guard → CHR-ROM 讀取在 PPU 多工 AD 匯流排形成不收斂回授環。
  → **PpuAleReadFeedback load-bearing、可判(比 sub-test FAIL 更強)+ milestone mech 臂(PPU_ALE_FB→141/141)
  = PROVEN**。⚠️ 不用 resume(不收斂是決定性、每次同點崩,崩潰即答案)。本質是 inertial-delay/feedback-break
  guard,跟 DL 同家族;差別:拔 PpuAleReadFeedback→硬崩(強決定),拔 DL→錯但收斂的 glitch(孤立不可判)。
- **dot339 ctrl(`--ppu-write-delay-global 0`)= 140/141**,FAIL `$48F err3`(晚段 StaleSprite 防守題)。
  base 認證 141 / ctrl 140 / mech milestone 141 → **dot-339 PROVEN**。
- **oamedge ctrl(`NO_OAMEDGE_SHIM`)= 138/141**,FAIL `$45B err10`+`$47B err2`+`$48F err2`(Address2004/StaleSprite)。
  → **OamBlankEdge PROVEN**。($48F 在 dot339 與 oamedge 都 FAIL = 兩者獨立共防該題。)
- **★ 三顆全數 PROVEN → 累計 12 顆 cleared to retire**;只剩 DL(架構延後)+ OpenBus(CEILING)兩個結構殘留。
  s1a.html + decidability.html 已翻牌(commit 7fe5dd2 等)。⚠️ 實際拔除(default-flip + 刪碼 + 重認證)未做。

## 當機/重開恢復(已驗證可行)
- 4 個 run 都 `--snapshot-frames 10`(在 quiescent 存),快照含 **MEMS(全行為記憶體=含 AC $0400+ 結果)+
  NODE/FLAG + RUNR(runner 判定狀態)+ SHIM live 狀態**(Snapshot.cs:89-170)→ 恢復不丟進度。
- 設定指紋 + CRC:設定不符 LoadState 拒絕;torn 檔 CRC 抓出(Snapshot.cs:192,203-207)。
- **兩個 launcher 已改自動續跑**(偵測各自 %SNAP% 最新 state_*.sav → 加 `--resume`);torn newest 刪掉重跑退一格。
- **一鍵全恢復**:`tools/testrom/resume_all_4engine.bat`(4 個平行重啟、各自續跑,核不變 8/6/10/12)。
  單顆恢復:直接跑該 launcher(`run_ac_s1a_ctrl.bat dot339 6` 等)。強制全新:清該 run 的 %SNAP%。
- ⚠️ watcher(等 S1A count=0)在**當機**時也會誤觸「done」→ 屆時看 verdict 幀數不足即知是崩非完成,跑 resume_all。

關聯 [[banked-136-of-141]](認證 baseline)、[[ac-test-toolchain-and-verdict-flow]](判定鏈)、[[snapshot-resume]]
(早開火 shim 不能跨設定快照加速,故 ctrl 全從 frame 0 跑;同設定 crash-recovery 一律乾淨)。
