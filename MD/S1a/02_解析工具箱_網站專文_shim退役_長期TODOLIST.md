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
  路徑參數,專文教讀者自備 Visual6502 系檔案。
  **⚠️ 網表出處(2026-07-18 使用者提醒,鐵律):圖與 JSON 一律用「修正版」網表
  `AprVisualBenchMark/data/system-def/{2a03,2c02}/{transdefs,segdefs,nodenames}.js`,
  不是 `ref/drive-download-*/` 原始上游版。** 原始 2A03 抽取漏了兩顆真下拉管
  (t13032b=R4015 讀解碼 a1 項、t14634b=ACLK 相位;幾何在 segdefs 存在只是抽取漏),
  **未修正網表本來就失真**(APU 暫存器讀解碼會模型錯)。修正版 2A03=10,918 顆(原始 10,916);
  2C02 無補丁(幾何稽核零漏)。id-格式的 `transdefs.js`(非 named 版)與腳本解析器相容。
  aggregate 統計幾乎不動,但分析要站在引擎真正模擬的網表上。**所有工具箱輸出已於 2026-07-18
  用修正版重生。**
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

| # | Python(WebSite/s1a/py/) | 專文 | 解析內容(偵測/參數) | 將退役的 shim | 拔除 commit | 依賴 | 狀態 |
|---|---|---|---|---|---|---|---|
| **M2** | `m2_charge_wins.py` | `m2-charge-wins.html` | **電容誰輸誰贏**:segdefs 分層多邊形面積 + transdefs gate 面積 → 每節點物理電容代理 C_phys;pass-gate 節點對的「浮接裁決」census —— 引擎連接數代理 vs 物理電容,誰翻盤、平手樂透在哪 | **io_db 衰減 shim(已退役)**、OpenBusShim、OamBlankEdgeShim(地基);浮接裁決升級(S1A) | **`689c8fd`(io_db 衰減)** | — | **✅ 機制+退役 1(2026-07-18)** |
| M1 | `m1_device_census.py` | `m1-strength.html` | transdefs geom → W/L 強度分佈、器件分類(下拉/pass/上電軌)、'+' 上拉 vs 下拉強度比(教科書 4:1 檢查)→ 強度 LUT 參數表 | LxaMagicShim、AluLatchShim(A1 部分) | — | — | **✅ 普查+專文 live(2026-07-17)** |
| M3 | `m3_elmore_binner.py` | `m3-delay.html` | per-net Elmore τ 分級器:R(層別 sheet-Ω × squares)+ C(M2 輸出)+ 驅動強度(M1 輸出)→ {<0.5hc/1-2hc/2-4hc/pad>4hc};五錨點回歸;16/18 rise-fall 奇偶(inversion parity)稽核 | dot-339、even_odd、AleReadMux、BgSerialReload 的**數字來源**(shim 機制 → 資料檔) | — | M1+M2 | **✅ 分級器+專文 live(2026-07-18)** |
| M4 | `m4_latch_scan.py` | `m4-latch.html` | P1 靜態掃描:pass-gate 閂鎖全列舉 + enable 錐 ∩ data 錐(關門賽跑指紋);P6 毛刺候選清單 | DmcLatch+AluLatch(edge-latch 原語已證可退)、DL、Dmc4015Abort、FrameIrq、Dbl2007、OamDmaPpuBus + 改判的 OpenBus/OamBlankEdge 瞬態半邊 | **`689c8fd`(DMC+ALU 原語,opt-in;預設翻轉待廣回歸)** | — | **✅ 掃描器+專文+原語(2026-07-18)** |
| M5 | `m5_board_inventory.py` | `m5-board.html` | 板級盤點:nes-001 connections 全清單(跨晶片網)、板級元件 pin 語意表('373/'139/4021)、閘級模組「結構性不可驅動」自動判定 | BoardOctalLatch 殘餘 hack、行為層手把的元件化 | — | — | **✅ 普查+專文 live(2026-07-18)** |
| M6 | `m6_interface_census.py` | `m6-phase.html` | P2 掃描:跨晶片網 → 下游 BFS 找「計數器比較器錐」(hpos/vpos 位元支撐)→ 延遲敏感介面全清單;4 相位參數盤點 | reset-hold-extra、power_up_palette、registers 上電注入 | — | M5 清單 | **✅ P2 掃描+專文 live(2026-07-18)** |
| M7 | `m7_canonical_key.py` | `m7-canonical.html` | 正準鍵 (class, layeredArea, structHash, degree) 計算;孿生偵測(u7/u8、joy 通道、db 位元)+ 同構同命一致性報告 | (D 類樂透根源;非單一 shim) | — | M2 面積 | **✅ 正準鍵+專文 live(2026-07-18)** |

**「拔除 commit」欄 = 存證欄**(2026-07-17 使用者新增):S1A 機制落地、shim 真正拔掉、
且通過完整驗證閘(§三步驟 5)之後,才把那個 commit hash 填進來;之前一律「—」。
網站工具箱表(s1a.html #toolbox)同欄同規則,兩處同步更新。

**順序原則**:工具箱(分析面)按**資料可靠度**排 —— M2(使用者指定起點)→ M1(同資料家族,純算術)
→ M3(吃 M1+M2 輸出)→ M4/M5/M6(圖演算法)→ M7。引擎機制(工程面)照 `00_總綱 §6` 的
Phase 表(M6/M7 先行),**兩序不同不衝突**:工具箱先鋪參數與證據,機制等使用者發令。

## 五、進度日誌(每階段追加)

- **2026-07-17**:s1a.html 首頁上線(2ccf20e);S1A fork 鷹架 + 金 checksum bit-exact(2da7f39)。
- **2026-07-17(本階段)**:本 TODOLIST 建檔;工具箱目錄 `WebSite/s1a/{py,img,data}/` 建立;
  **M2 第一組落地**:`m2_charge_wins.py` + 實測數字 + SVG + 專文 `m2-charge-wins.html`;
  s1a.html 增工具箱區(七組狀態表)。→ 詳見 commit。
- **2026-07-17 深夜**:**M2 引擎機制落地(S1A,env `M2_CAP`)** —— 換表設計(NodeConnections
  填量化物理電容);Gate A 過(機制關 = 金 `0x794A...` bit-exact);幾何覆蓋 14,249/14,727(96.8%);
  Lower.cs 重建漏帶 CapWeighted 的 bug 修正(合併類並聯相加);金配方 300k 機制開 checksum 竟同
  (浮接分支 <1% × 77% 一致,未踩差異點);Gate B = 孤立 ROM 集(使用者指定驗證法)釘實體核
  6/8/10/12/14 平行跑(OpenBus/OAMCorruption/StaleSpriteShiftRegs/InternalDataBus/2007Stress ×
  關/開 + ppu_open_bus/oam_read)。
- **2026-07-17 深夜**:**M1 第二組落地(普查+專文)**:`m1_device_census.py` + `m1-strength.html`。
  重點數字:27,788 顆全分類(下拉 7,147+9,265 / pass 2,869+6,825 / 接VCC 767+698);
  **半八度強度格 19/16 級、前 8 級 >93%**(MOSSIM II 小格架成立);4:1 稽核:帶載下拉中位 4.33/4.00
  vs pass 2.00/1.40(分離 2.17×/2.86×),**反推負載 S ≈ 0.58/0.95**(M1 唯一自由常數的晶粒先驗,
  終錨 = LXA $FF);**打架點 538、2× 內 194** —— 2A03=db/ab/nmi/irq/joy pad(7上/9下 S≈13 推挽),
  2C02=io_db(open-bus 老家)/ale(ALERead 訊號)/wr/ext。限制:一跳普查,LXA 的 pass 中介戰
  要動態步驟才抓得到。
- **2026-07-18**:**M3 第三組落地(分級器+專文)**:`m3_elmore_binner.py` + `m3-delay.html`。
  刻意站下游:R 吃 M1 的 W/L(20kΩ/S;rise 側用稽核反推負載 0.58/0.95)、C 吃 M2 面積公式、
  線阻 = sheet-Ω × 矩形等效方塊數(面積+周長解二次式);τ 以 gate 單位(中位受驅動網=1)。
  重點數字:**11,343 張網分級,島候選 238+362=600(~5.3%)**;最慢榜 = 2C02 的
  pclk0/1、`_rd`/`_io_ce`/`_io_rw_buf`/`_io_db*`(**純幾何把 CPU↔PPU 介面排最慢** = 24hc 家族
  物理背書)、2A03 的 apu_clk1/_res/cclk/RnWstretched/_db*;`res` 無下拉 rise 303 = 外部 RC 重置
  敘事自現;**中位 rise/fall 6.43×/3.89×**(教科書 4:1 第二條獨立路徑);**16/18 不可能錨點量化:
  6,424 雙路網僅 29(0.45%)做得到** → 奇偶反相/super-buffer 預言;`ale` 13.8/13.8 完美對稱
  (M1 推挽 13:13 呼應)、io_db0=31 最慢。pass 串鏈 401 網旗標未解。下一隻:ren_en 路徑反相
  計數走訪(可證偽測試)。
- **2026-07-18:M2 引擎機制 Gate B 收官 —— 14/14 全對照一致(clean sweep)**。
  釘實體核 6/8/10/12/14 三波跑完:AC 五顆(OpenBus/OAMCorruption/StaleSpriteShiftRegs/
  InternalDataBus=樂透金絲雀 → 關開皆 PASS;2007Stress → 關開皆 err1)+ nes 兩顆
  (ppu_open_bus 關開皆 PASS **且 hc 逐位相同 179,338,695**;oam_read 關開皆 err1)。
  兩個 err1 基線 = 孤立包裝/快速 harness 環境差異(2007Stress 套內綠、oam_read 少帶 147
  runner 逐測配方旗標),**皆被機制開完美複製** → M2 對驗證集零擾動。結論:機制安全,
  靜態 1,000 翻盤點在這些負載未被踩到或無可觀察後果;下一步 = 動態開火統計(哪些翻盤真的
  發生)→ 再做拔 shim 實驗(如 M2 開 + OpenBusShim 關 → OpenBus 還過嗎)。
- **2026-07-18:M2 動態開火統計 + OpenBusShim 試拔(f776e15 儀器)**。
  census(DEBUG #if 慣例,Release 熱路徑零位元組變動;雙鍵影子表在 ClearPostLoadBuildState
  前建):金配方 300k = 430 萬次浮接裁決、**32.6% 換贏家、state-diff 僅 2 次且全被沖掉**
  (checksum 仍金);AC OpenBus/OAMCorruption 各 **245M 次裁決、8,000 萬次換贏家、
  state-diff 僅 18/26 次**、279 個翻盤點(頂級=PPU 精靈路徑內部對,spr_d7_int 等)。
  **核心事實:浮接群壓倒性同狀態 —— 換裁決者幾乎不換答案。**
  **試拔 OpenBusShim:失敗(誠實負面)** —— 對照(拔shim無M2)FAIL(1) ≡ 實驗(拔shim+M2)
  FAIL(1);shim 承重點 = DOR precharge 毛刺經導通 pad 洩出(P6 瞬態,群根本不浮接,
  裁決層無管轄權)+ last-byte 行為重播。**分類學修正:「M2 shim」的不可約部分屬
  P6/M4×M3(瞬態×時間),M2 只覆蓋其穩態(hold-previous 本來就有)。**
- **2026-07-18:OamBlankEdgeShim 試拔 —— 四臂全 PASS 含對照組(判不了)**。
  NO_OAMEDGE_SHIM ± M2_CAP × {OAMCorruption, StaleSpriteShiftRegs} 四臂皆 PASS,且 shim
  開時零開火 → **孤立包裝版根本沒踩到此 shim 的場景(套內互動雷)**;孤立協定對這顆
  無鑑別力。要判退役需套內證據(snapshot resume 時光機),任何預設變更前必補。
  對照 OpenBus(對照組 FAIL = 場景有踩到、M2 救不了):孤立協定「能判就判得準」。
- **2026-07-18:M4 第四組落地(掃描器+專文)**:`m4_latch_scan.py` + `m4-latch.html`。
  純拓撲零先驗四指紋:純 pass 饋入 cell 1,247+1,247(單饋/多工)、帶驅動閂鎖 2,003+6,126
  (上拉=刷新源 — idl 教的)、交叉耦合 25+209 靜態、**動態(無上拉)2A03=0 vs 2C02=2,114
  = OAM 陣列被結構自己點名**;緊湊關門賽跑 654+819;pass 回授 415+268。
  **ground-truth 驗證 8/11**:idl0/7(1饋+3驅+上拉)、alua0、alub0(3饋,支撐8)、
  spr_d7_int(9饋)、/bkg_pat_out、/spr_pat_out 全中;dor*/_pcm_out4=暫存器輸出、
  oam_write_disable=控制訊號(正確拒收)。教訓入 script:錐深 3 萬物相交→深 2;
  回授判定要排除 data↔cell 直接邊。
- **2026-07-18:★ 第一顆 shim 正式退役 —— io_db 衰減 shim(M2 時戳衰減機制取代)★**。
  **M2 的另一半(時戳惰性衰減)實作**:引擎層 per-bit 時戳,節點值改變重置、持非零過 25.7M hc
  (36 幀≈600ms)以 force-low+release 衰減;掛 StepCycle 尾、每 16384 hc 掃一次(對 600ms 常數
  綽綽有餘、成本可忽略);env 預設 ON(`NO_M2DECAY` 帶回舊 shim)。**三段論證(ppu_open_bus,
  釘實體核)**:baseline(shim)PASS / **ctrl(拔 shim 無機制)FAIL(3) ← 證 shim 承重** /
  **mech(拔 shim + 機制)PASS 且 hc 逐位同 179,338,695** = 機制精確取代 shim。Gate A 金
  0x794A 不變(衰減閾值進不了 benchmark)。→ **拔除 commit 待建置驗證後填入本表與 s1a.html。**
- **2026-07-18:M4 通用 edge-capture 原語實作(取代 DMC + ALU shim 的通用化)**。
  一個機制兩種判決:data-wins(關門沿捕捉資料值,DMC pcm_latch)/ hold(關門沿恢復前緣快照,
  ALU 輸入閂鎖);標註列驅動(內建 DMC + alua/alub 兩列),env `M4_EDGE` armed 時自動取代兩顆
  shim;驗證中(7-dmc_basics + 03-immediate 各三臂)。**架構修:shim 派發菊花鏈攤平**
  (原 Dmc→Alu→Lxa 巢狀,單一 kill 開關會連殺下游全家 = 混淆對照;TestShimChainStep 攤平、
  保留原順序、7 個 Enable 各掛 ShimChainArmed)。
- **2026-07-18:攻 OpenBus/OamBlankEdge 毛刺免疫 —— M4 Transparent 判決 + 誠實邊界**。
  **M4 原語加第三判決 Transparent(a07fe8b)**:致能透明相位內 cell 追隨 settled data,
  蓋掉 mid-settle 捕捉毛刺 = 慣性延遲(「對相位的 settled 值勝過錯相位瞬態」);可帶行為
  scoping(位址窗 + MinBits 位元門檻)。Gate A 金 0x794A 不變,opt-in `M4_DL`。
  **OpenBus 依賴矩陣(AC OpenBus)**:baseline PASS / **noDL PASS**(AC OpenBus 不需 DL!)/
  noOAM PASS / **noOB FAIL(1)**(唯一承重=last-byte replay)。
  **★ 誠實邊界(可證偽負面):OpenBus err1/last-byte 不是毛刺免疫,是外部匯流排電容保持。★**
  M4 全 stack(data-wins+hold+transparent)+ NO_OB_SHIM 仍 FAIL(1)(ob_lastbyte 實證);
  加上先前 M2_CAP 也救不了 —— 因 CPU db 每 cycle 被主動重驅動(非浮接),charge/裁決/毛刺三
  機制皆無管轄權。**結論:last-byte replay 屬 L3 行為資料層(外部匯流排模型),非物理機制可退。**
  M4_DL 對 AC OpenBus 安全(ob_m4dl_safe PASS,scoping $4016/17 不干擾 err4 路徑)。
  **DL(err6)孤立不可判**:ppu_open_bus noDL PASS + M4_DL PASS(且 AC OpenBus noDL 也 PASS)
  → 兩個孤立 OpenBus 測試都不需要 DL,無 control FAIL 可證,與 OamBlankEdge 同類(孤立協定
  判不了,需套內 141/blargg 證據)。**毛刺免疫三結論:(a)Transparent 判決=原語就位可用;
  (b)OpenBus last-byte=真邊界(L3 行為資料);(c)DL/OAM=判決可機制化但孤立不可判。**
  無乾淨退役產出 —— 是 campaign 式誠實負面 + 精確邊界刻畫,不是失敗。
- **2026-07-18:M6×M3 統一相位仲裁機制(4aef1b8)+ 誠實結論**。三顆 downstream-clamp 延遲 shim
  (dot-339/even_odd/BGSerialIn)= 同一物理:跨晶片控制變更晚 16-24hc 到,計數器比較器不該提前動作。
  **一張表(trigger, gate, delay, window)取代手寫 ShimStep**:dot339=NodeRise(rendering_1)→clamp
  hpos_eq_339_and_rendering 24hc/visible;bgserial=RegWrite($2001,enable)→clamp reload gate 16hc/hpos%8≥4。
  兩 action(ClampGate/DelayTransition)、兩 trigger、三 window;force-LOW 無牆;env `M6X`。Gate A 金不變。
  **8 臂實驗全 PASS**:M6X 機制 bit-safe(d339_m6x/bgs_m6x/reg_ob/reg_oc 全過)—— **但 dot-339 與
  BGSerialIn 兩顆孤立不可判**(d339_ctrl 拔 dot-339 也 PASS、bgs_ctrl 拔 BGS 也 PASS,無失敗對照)。
  **與 DL/OAM 同類:整個跨晶片相位家族本來就在套內修好,孤立協定判不了退役。** even_odd 的
  DelayTransition 尚未建。→ 要真正驗退役需**套內(snapshot resume)跑滿 141**。
- **2026-07-18:s1a.html 新增「shim 總帳」區(shim ledger)** + 兩個工具。使用者要求:每個 shim
  說明目的、五種下場(RETIRED/PROVEN/UNDECIDABLE/CEILING/ACTIVE)、天花板與不可判用教學方式誠實記錄。
  天花板教學:OpenBus last-byte=外部匯流排電容(非晶粒內節點,M4 全 stack 仍 FAIL);不可判教學:
  跨晶片相位家族孤立無鑑別對照。**工具:`tools/estimate_frames.py`**(跑 AprNes 算完成幀→×7s/frame→
  timeout ×1.5 保險;AC 測試回報畫面穩定幀=安全上界)。**147 快照確認可用**(實測 01-basics:
  --snapshot-frames 存 f5/10/15 → --resume from f15 → 同 PASS@f19,只跑 4 幀;與 141 同一套引擎快照,
  共用測試迴圈)。⚠️ 快照對設定敏感:換機制/旗標則節點重編、舊快照失效(LoadState 拒絕)。
- **2026-07-18:可判定性邊界 —— 系統性發現 + 深入專文(decidability.html)**。DMA-abort 可判定性掃描
  (NO_ABORT_SHIM × Explicit/ImplicitAbort)**兩對照皆 PASS → Dmc4015Abort 亦孤立不可判**。至此規律清楚:
  **可判(對照 FAIL)= io_db衰減/DmcLatch/AluLatch/OpenBus(自足場景,孤立 ROM 自己就踩到);
  不可判(對照 PASS)= DL/OAM/dot-339/BGSerialIn/Dmc4015Abort(套內時序互動場景,單測試包裝重現不了)。**
  關鍵洞見:**可判性是「測試」的性質、不是 shim 的**;不可判 shim 的機制全建好+bit-safe,缺的是會失敗的
  測試 = 套內全跑。孤立協定天花板已找到:4 顆退役/證明、5 顆 bit-safe 但不可證。→ 驗證前線移到套內 141。
  寫成 `WebSite/s1a/decidability.html`(方法論深入專文,三臂協定+九 shim 對照資料+規律)+ shim 總帳連結。
- **2026-07-18 深夜:自主推進(使用者「一步一步、聰明省時」）**。當前步 = io_db 套內驗證(核 6,
  單變數 M2_DECAY,~10-15h,113+ 快照)。**快照設定指紋查證**:header 記 PowerUpState/DmcLatch/
  AluLatch/LxaMagic/FrameIrq/Dbl2007/OamDmaPpuBus/PpuAleReadFeedback/even_odd/joypad/reset,改這些
  LoadState 拒;**但 M2_DECAY/M4_EDGE/M6X/dot-339/BGSerialIn/DL/OAM/abort 不在指紋**→跨機制 resume
  不會被拒但**全域 shim 狀態已烤進快照=不乾淨**。**可靠省時法=各機制配置從零跑但跑到防守測試登記判決
  就早停(每 shim 對應哪測試已知),快照供同配置當機續跑。**
  **shim 全清單 17 顆**(前漏 2:ALERead mux=自有機制已入帳、PpuAleReadFeedback=CHR 回授 guard);
  E 類 t13032b 是網表補丁非 shim。
  **趁 in-suite 佔 exe 不能重建的空檔:M6X 補 even_odd DelayTransition**(bkg/spr_enable 在
  vpos261/hpos338-339 窗轉態→clamp 舊值側 16hc;M6X 現涵蓋 dot-339+BGSerialIn+even_odd=3 相位 shim;
  TestRunner 加 !M6xEnabled gate 讓 M6X 取代 even_odd shim)。**側目錄編譯乾淨(0 錯誤);Gate A +
  bit-safe 執行驗證待 in-suite 跑完正式 build。程式碼未 commit(等 Gate A 驗過)。**
- **2026-07-18:M5e 立案(使用者開案)—— 板級寄生匯流排保持(bus-hold)**。
  OpenBus last-byte 的物理 = 外部資料匯流排的**寄生電容**(PCB 走線 + 掛在匯流排上每顆晶片的
  接腳/封裝/閘極,~幾十 pF、保持毫秒級以上)—— **電路圖上沒有這顆零件,是板子免費送的**;
  我們的圖對板網零幾何,所以晶粒內三機制(M2/M4/M3)實驗證實全碰不到(M4 全 stack FAIL(1))。
  **M5e = M5 板級元件庫延伸:外部網 bus-hold 語意**(板級 db 匯流排掛電容標註、記住最後驅動值、
  無人驅動時供出)—— shim 邏輯幾乎不變,家從「測試補丁」搬到「有物理依據的板網模型」('373 同哲學)。
  **刻意只立案不實作**(設計待議);開放問題:連線層屬性 vs 偽元件、板衰減要不要建(可能永久保持
  即可)、P6 毛刺半邊(DOR 脈衝)另案不可吸收。**驗證可判**(AC OpenBus 對照孤立即 FAIL(1),
  建成即可三段論)。專文:`WebSite/s1a/m5e-bus-hold.html`(沒有人放的那顆電容);機制表/工具箱
  M5 列/shim 總帳 OpenBus 列均已連結。
- **2026-07-18 傍晚:剩餘 shim 可判定性掃描收官(核 8-14 四臂 × 兩波)+ 兩顆「旁觀者」定案**。
  判決:**Dbl2007**(#67 test_ppu_read_buffer,1274 幀)與 **even_odd/PpuWriteDelay**(blargg 09+10
  兩支家族測試)對照組全 PASS 且 **hc 逐位相同**(#67:910,509,287;09:52,830,383;10:98,573,519)——
  比「對照通過」更強:shim 兩臂都有 armed(stderr 查證),但**開火窗整場沒打開過 = 套外旁觀者**。
  even_odd 窗只有 2 dot 寬(vpos261/hpos338-339)、Dbl2007 窗是合併距離內的 $2007 背靠背雙讀,
  blargg 家族從未踩進。→ 兩顆歸 UNDECIDABLE(孤立不可判),證據等級「hc-identical」寫進
  decidability.html(新增證據等級說明段)+ s1a.html 總帳(even_odd 列改判、FrameIrq·Dbl2007·
  OamDmaPpuBus 合併列拆成三列)。
  **方法論自白(誠實記錄)**:第一版 OamDmaPpuBus 對照選錯 ROM —— blargg oam_read **根本沒有
  $4014 DMA**,兩臂 hc 相同的原因平凡(shim 永遠開不了火),該臂作廢;KB 明載真防守者 =
  **#67 的「DMA + PPU bus」子測**(OamDmaPpuBusShim → #14 修復紀錄)。正確對照
  (`--no-oam-dma-ppu-bus-shim` × #67,核 12,~2.7h)已在跑 —— 預期 FAIL=可判(那 shim 本來
  就是為 #67 建的)。
  **意外發現 → 歸屬定案:oam_read 抽籤翻籤,兩 fork 共同、非 S1A 回歸**。孤立 oam_read 在現
  build 上 FAIL(1) 圖樣 E03E03AD(戰役 07-09/10 時 PASS)。歸屬調查四臂收齊:**S1 出廠 exe
  (07-17 build)同圖樣同 hc=20,667,239;S1A 預設 / S1A×NO_M2DECAY / S1A×no-oamdma 全部逐位
  相同** —— S1≡S1A 在此軌跡位元級等價(fork 零漂移),翻籤來自戰役後共同的載入期圖變更
  (ALERead node-split 進 LoadSystem;Socket Pattern 已知:load-time cut 重擲全部彩票)。
  KB 預言應驗:「未來引擎變動可能再合法換邊」(blargg 真機 4 圖樣僅 1 過,抽籤=忠實行為)。
  M2_DECAY 證實中立(20.6M hc < 25.7M 閾值,機制/舊 shim 皆不可能開火)→ io_db 退役不受影響。
  **誠實推論:現 build 的 147 全量分數預期 145/2**(oam_read 合法翻籤加入 cpu_dummy_writes_oam
  的忠實偏差欄);146/1 是戰役 build 的歷史成績,下次全量回歸登記新分數。已記 KB 時間線。
- (待續)

## 六、風險與提醒(承 00/01,長線必讀)

- **M3 是全案最高風險**(IRSIM 警告:RC 追蹤拖垮熱路徑)→ 分級器只出**排名與分級**,
  引擎端 bounded 到少數島或退回「延遲原語 + 資料檔」。
- **候選 ≠ 真雷**:掃描器出候選與排序;真雷判定靠 oracle(AprNes lockstep / Dynamic Miter)。
- **探針效應**:任何載入期圖變更(含未來儀器)都重擲 D 類彩票 → M7 正準化是操作面解藥。
- **S1 金 checksum 永遠不動**;所有會動裁決的機制只進 S1A,S1A 逐 phase 重定自己基準。
