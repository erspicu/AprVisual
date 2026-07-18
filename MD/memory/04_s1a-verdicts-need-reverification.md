# S1a 退役判決:S1 基礎重驗完成後,全部要重新確認

**使用者裁示(2026-07-18):S1a 網站/總帳上的那些退役判決,有些是 Fable 失憶期做的,
當時可能像 AC 漏 ALEREAD_MUX 一樣「參數沒開完整」就下了結論。等 S1 驗證基礎打穩
(AC 141 + 147 全量重跑,現在進行中),S1a 的東西全部要在乾淨的地基上重新確認驗證過。**

## 為什麼(今天的三個實例證明風險是真的)
- **AC 漏 ALEREAD_MUX**:整場會掉 ALERead → 140/141。env-gated opt-in,最容易漏。
- **K=0 配方假警報**:孤立臂全跑 K=0(漏 `--reset-hold-extra 1`),跨時代比較整批無效,
  「翻籤/回歸」敘事整條收回。
- **土製工具**:自寫 watcher 漏 `--test-json`、對照選錯 ROM(oam_read 沒 $4014 DMA)。
→ 這三種錯**都可能污染 S1a 的退役判決**,因為那些判決也是 test 跑出來的、也吃這些參數。

## 關鍵區分:哪些要重驗、哪些不用
- **不用重驗 = 工具箱分析輸出(M1–M7 Python census)**:確定性、只吃網表(修正版),
  不吃 run 參數。10.3% 電容翻盤、19/16 級強度格、11,379 latch、66 相位介面…這些是
  對網表的靜態分析,網表不變就不變。(網表若動,重生即可。)
- **要重驗 = 所有「退役/可判定」判決(test 跑出來的)**,逐一在乾淨 S1/S1A build +
  完整正確參數(K=1、對應 env、全 shim 正確 arm)下重跑:
  | 判決 | 原證據(Fable 期) | 重驗方式 |
  |---|---|---|
  | io_db **RETIRED** | ppu_open_bus 三段論 + 套內 141 | M2_DECAY 三臂 + 套內(K=1)|
  | DmcLatch **PROVEN** | 7-dmc_basics shim PASS/ctrl FAIL#19/M4 PASS | M4 三臂(K=1)|
  | AluLatch **PROVEN** | 03-immediate shim PASS/ctrl FAIL(1)/M4 PASS | M4 三臂(K=1)|
  | 6 顆 **UNDECIDABLE** 對照臂 | **全 K=0**(dot-339/BGSerialIn/even_odd/DL/OamBlankEdge/Dbl2007/abort)| K=1 重確認(腳本已備 temp/m2_gateB/run_k1_*.ps1)|
  | FrameIrq/LXA 可判定 | 側建 K=0(FAIL(6)/FAIL(1))| K=1 重確認 |
  | M6X/M4/M2_CAP「bit-safe」+ Gate A | Fable 期側建 | 正式 build 重跑金 checksum + bit-safe |

## 順序(硬性)
1. **先** S1 基礎重驗完成(AC 141/141 + 147 146/147 重現、確立乾淨地基)。
2. **後** 才逐一重驗 S1a 判決;每項用 [[write-important-to-memory-immediately]] 落檔、
   對照 [[ac-test-toolchain-and-verdict-flow]] 的配方紀律,**別再憑 Fable 期的結論**。
3. 重驗前**先讀 runner 原始碼確認配方**(K、env、shim arm),別假設。

## 網站/總帳處置(在重驗完成前)
S1a shim 總帳與工具箱的退役狀態,在重新確認前應視為**暫定(Fable 期,待重驗)**;
已在 s1a.html 加註記。重驗過的才升為確證。相關:[[banked-136-of-141]]、
`MD/S1a/02_解析工具箱_網站專文_shim退役_長期TODOLIST.md`。
