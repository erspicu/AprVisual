# S1a 解析工具箱 × 網站專文 × shim 退役 —— 長期 TODOLIST(living doc)

> **建立**:2026-07-17 深夜,使用者定案。**性質:階段性持續工作,不一次做完;每完成一階就
> commit+push+更新本表。** 每次「機制取代 shim → 拔除 shim」都必須走完整驗證閘才能入帳。
> 本文是這條長線的**規劃與進度總帳**;設計原理見 `00_S1a類比重構設計總綱.md`(M1–M7)與
> `01_時序標註網表_物理延遲參數化_M3M6實作參考.md`(P1–P6 偵測 pattern、幾何先驗)。

---

## 一、使用者需求原文(2026-07-17,三則合併)

1. s1a 網站要有一個區域:**每隻 Python 對應 M1–M7 一種解析策略**(偵測結構、或由
   transdefs/segdefs 算出更多屬性參考參數);**每隻 Python 配一篇獨立深入專文 HTML**,
   把原理說清楚,最好搭配 SVG 圖;並說明「因為加了什麼機制,讓 shim 的設計逐步移除」。
   **先做最可靠、CP 值最高的環節。**
2. **不需要一次整個做完** —— 階段性持續工作;**每次拔除 shim 後都還要驗證過**。
3. **優先處理「電容誰輸誰贏」的裁決問題,先用 Python 當範例**;Python 放官網內一個目錄。

## 二、網站與檔案結構約定

```
WebSite/s1a.html                 S1a 首頁(hub;工具箱區 = 每組工作包的入口與狀態)
WebSite/s1a/py/mN_*.py           解析程式(官網直接可下載;stdlib-only,零相依)
WebSite/s1a/img/mN_*.svg         程式產出的數據圖(commit 進 repo,網頁直接引用)
WebSite/s1a/data/mN_*.json       程式產出的摘要參數(小檔才 commit;大檔 gitignore)
WebSite/s1a/mN-*.html            每組工作包的深入專文(雙語,S1a 紫色風格)
MD/S1a/02_...TODOLIST.md         本檔(進度總帳)
```

- **網表資料不 vendor 進網站**(CC-BY-NC-SA):script 一律吃 `--segdefs/--transdefs/--nodenames`
  路徑參數,專文教讀者自備 Visual6502 系檔案。本機跑用 `ref/drive-download-*/visual2a03-*.js`。
- Python 一律 **stdlib-only**(re/json/math),手刻 SVG 輸出,任何人裝了 Python 就能重現。
- console 輸出只用 ASCII(cp950 陷阱)。

## 三、固定節奏(每組工作包的六步 ritual)

```
(1) Python 偵測/參數萃取  → 跑真資料、產 SVG+JSON,數字先自我交叉驗證
(2) 深入專文 HTML          → 原理 + 配方 + 實測數字 + SVG;連結 .py 下載
(3) S1A 引擎機制實作       → ⚠️ 等使用者指示才動工(工程面;工具箱/專文不受此限)
(4) 拔除對應 shim          → 機制取代,不是關掉
(5) 全量驗證閘             → 金 checksum(機制 off ≡ S1)+ AC 141 + nes-test-roms 147
                              (機制 on 不退步);S1A 每 phase 重定自己的基準,S1 金不動
(6) 網站狀態 + 本表更新     → commit + push
```

> 驗證閘細節:`00_總綱 §5 驗證策略`。**「拔 shim 不驗證 = 沒發生」。**

## 四、七組工作包(M1–M7)

| # | Python(WebSite/s1a/py/) | 專文 | 解析內容(偵測/參數) | 將退役的 shim | 依賴 | 狀態 |
|---|---|---|---|---|---|---|
| **M2** | `m2_charge_wins.py` | `m2-charge-wins.html` | **電容誰輸誰贏**:segdefs 分層多邊形面積 + transdefs gate 面積 → 每節點物理電容代理 C_phys;pass-gate 節點對的「浮接裁決」census —— 引擎連接數代理 vs 物理電容,誰翻盤、平手樂透在哪 | OpenBusShim、OamBlankEdgeShim(地基);浮接裁決升級(S1A) | — | **✅ 本階段(2026-07-17)** |
| M1 | `m1_device_census.py` | `m1-strength.html` | transdefs geom → W/L 強度分佈、器件分類(下拉/pass/上電軌)、'+' 上拉 vs 下拉強度比(教科書 4:1 檢查)→ 強度 LUT 參數表 | LxaMagicShim、AluLatchShim(A1 部分) | — | 排隊 |
| M3 | `m3_elmore_binner.py` | `m3-delay.html` | per-net Elmore τ 分級器:R(層別 sheet-Ω × squares)+ C(M2 輸出)+ 驅動強度(M1 輸出)→ {<0.5hc/1-2hc/2-4hc/pad>4hc};五錨點回歸;16/18 rise-fall 奇偶(inversion parity)稽核 | dot-339、even_odd、AleReadMux、BgSerialReload 的**數字來源**(shim 機制 → 資料檔) | M1+M2 | 排隊 |
| M4 | `m4_latch_scan.py` | `m4-latch.html` | P1 靜態掃描:pass-gate 閂鎖全列舉 + enable 錐 ∩ data 錐(關門賽跑指紋);P6 毛刺候選清單 | DL、DmcLatch、Dmc4015Abort、FrameIrq、Dbl2007、OamDmaPpuBus(最大宗 6 顆) | — | 排隊 |
| M5 | `m5_board_inventory.py` | `m5-board.html` | 板級盤點:nes-001 connections 全清單(跨晶片網)、板級元件 pin 語意表('373/'139/4021)、閘級模組「結構性不可驅動」自動判定 | BoardOctalLatch 殘餘 hack、行為層手把的元件化 | — | 排隊 |
| M6 | `m6_interface_census.py` | `m6-phase.html` | P2 掃描:跨晶片網 → 下游 BFS 找「計數器比較器錐」(hpos/vpos 位元支撐)→ 延遲敏感介面全清單;4 相位參數盤點 | reset-hold-extra、power_up_palette、registers 上電注入 | M5 清單 | 排隊 |
| M7 | `m7_canonical_key.py` | `m7-canonical.html` | 正準鍵 (class, layeredArea, structHash, degree) 計算;孿生偵測(u7/u8、joy 通道、db 位元)+ 同構同命一致性報告 | (D 類樂透根源;非單一 shim) | M2 面積 | 排隊 |

**順序原則**:工具箱(分析面)按**資料可靠度**排 —— M2(使用者指定起點)→ M1(同資料家族,純算術)
→ M3(吃 M1+M2 輸出)→ M4/M5/M6(圖演算法)→ M7。引擎機制(工程面)照 `00_總綱 §6` 的
Phase 表(M6/M7 先行),**兩序不同不衝突**:工具箱先鋪參數與證據,機制等使用者發令。

## 五、進度日誌(每階段追加)

- **2026-07-17**:s1a.html 首頁上線(2ccf20e);S1A fork 鷹架 + 金 checksum bit-exact(2da7f39)。
- **2026-07-17(本階段)**:本 TODOLIST 建檔;工具箱目錄 `WebSite/s1a/{py,img,data}/` 建立;
  **M2 第一組落地**:`m2_charge_wins.py` + 實測數字 + SVG + 專文 `m2-charge-wins.html`;
  s1a.html 增工具箱區(七組狀態表)。→ 詳見 commit。
- (待續)

## 六、風險與提醒(承 00/01,長線必讀)

- **M3 是全案最高風險**(IRSIM 警告:RC 追蹤拖垮熱路徑)→ 分級器只出**排名與分級**,
  引擎端 bounded 到少數島或退回「延遲原語 + 資料檔」。
- **候選 ≠ 真雷**:掃描器出候選與排序;真雷判定靠 oracle(AprNes lockstep / Dynamic Miter)。
- **探針效應**:任何載入期圖變更(含未來儀器)都重擲 D 類彩票 → M7 正準化是操作面解藥。
- **S1 金 checksum 永遠不動**;所有會動裁決的機制只進 S1A,S1A 逐 phase 重定自己基準。
