# Dominant-Bypass(P-5)1-bit pinned 完整調查與結論

> 分支:`dominant-bypass`(程式碼留在那)· 日期:2026-06-10 · 狀態:**負結果,已走到完全見底**
> 公開寫作:`WebSite/dominant-bypass.html`(2026-06-10 更新段)。平行化那條另見
> `2026-06-10-dominant-bypass-平行化設計與困難.md`(Gemini 已證雙核切割物理上不可行)。

## 一句話

P-5 turn-off 旁路是**真實的大勝(skip +13.76%)**,但維護它需要的「每次解析的執行期事實」構成 **~7% 的地板**(維護 + 儲存),所以最佳淨值 **−6.84%**,仍是虧。正確、獎賞也真,但在 memory-latency-bound 引擎上,**一個每次解析都要維護的事實,不可能比它刪掉的那些便宜工還便宜**——與 frontier / active-driver-count 同一道牆。

## 機制(最終形)

- 每個節點一個 **1-bit「pinned = 被自己的供電拉住」**:`pinned ⇔ (state==0 && 自身 ≥1 個 ON gnd) || (state==1 && (自身 ≥1 個 ON pwr || PullUp))`。比原版「支配閘 id / 恰好一個驅動源」更便宜也更健全,且 pin 更多節點。
- **turn-off skip**:gate `nn` 關斷時,對 **pass-transistor 端點** c,若 `IsPinned[c]` 就跳過 enqueue(supply 端點不跳——那個 gate 可能就是 c 的 pin;同一輪的 supply entry 會 re-enqueue,故健全)。c1 端點加 `c2 != supply` 守衛;c2 端點不需(supply 正規化在 c2,c2 的另一端 c1 必為一般節點)。
- 只對 **skip-candidate**(有 pass + 自身供電通道、非 FC/callback/Gnd/Pwr)維護(Escape B,~54.8% 節點;砍掉它 −3.75%,故保留)。

## 成本拆解(full_palette 300k,interleaved-paired,全 bit-exact)

| 項目 | vs P-4 | 說明 |
|---|---|---|
| **skip 收益** | **+13.76%** | 「維護照留、只關 skip」對「full」量出;抑制 ~40% 節點重算 |
| 維護 | −15.6% | 每次解析算+寫 pinned(殺手) |
| 儲存稅 | −4.98%(option i)/ 寫入 stream(option ii) | 見下 |

## 試過的版本與數字(全 bit-exact:golden 0x794A43ABDF169ADA @300k / 0x6D4CCBCE2E9CD599 @1M)

| 版本 | vs P-4 | 備註 |
|---|---|---|
| DominantGate(16-bit gate id,獨立陣列) | −13% | 隨機寫入 stream |
| + Escape A(把 id 併進 PruneMask 同 cache line) | −13% | mask 1B→4B 撐爆 L1、抵消讀取合併紅利 |
| 1-bit pinned 塞 NodeStates(option i,hardcode + Escape B) | −8.36% | 寫入免費(搭 state store),但**每次熱導通讀取要 `& StateBit` = −4.98% 遮罩稅地板** |
| **獨立 IsPinned 陣列(option ii)** | **−6.84%** ← 最佳 | 無遮罩稅,但 pinned 成獨立寫入 stream(~50M 次/run) |
| Stage 2:把維護融進 AddNodeToGroup | −7.08%(中性)| group walk 走訪成員 >> 受益候選,逐成員 capture 的開銷 ≈ 省下的針對性重掃,零和 → 已退回 |

**i 對 ii 的本質取捨**:option (i) 寫入免費但付讀側遮罩稅(−4.98%);option (ii) 無遮罩稅但付寫側 stream。兩者差幾個百分點——那個「每次解析的執行期事實」總得有人付帳。

## 為什麼平行化也不行(摘要)

把維護外包到第二顆核心:Gemini 證明是死路——資料相依是逐節點、同波(worker 消費主執行緒當下才算出的值),切開只能 wait(序列化)或讀 stale(破壞 bit-exact);加上 latency-bound 引擎上跨核 MESI(NodeStates main 寫/worker 讀)的協調延遲(~40–80ns/line vs sub-ns/node)本身就 > +3.4%。SMT 兄弟核解決 MESI 但解決不了相依 + 同步原語成本。

## 收穫(可重用的規則)

1. **「維護執行期狀態」在這個引擎上有 ~7% 地板**:不管編碼多便宜(16-bit / 1-bit 塞 / 1-bit 獨立;掃描分開或融合),維護 + 儲存(遮罩稅或寫入 stream)合計擋死收益。
2. **fusion 不是萬靈丹**:把維護融進既有掃描,只有當「受益集合 ≈ 走訪集合」才划算;這裡走訪成員遠多於候選,fusion 零和。
3. **量測方法論**:用「維護照留、只關 skip」拆出收益、用「pinned 全關但遮罩全留」拆出遮罩稅地板——這種拆分比單看淨值更能定位「該不該繼續」。
