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

關聯 [[banked-136-of-141]](認證 baseline)、[[ac-test-toolchain-and-verdict-flow]](判定鏈)、[[snapshot-resume]]
(早開火 shim 不能跨設定快照加速,故 ctrl 全從 frame 0 跑)。
