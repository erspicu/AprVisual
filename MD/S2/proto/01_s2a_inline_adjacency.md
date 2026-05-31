# S2-A 原型:節點鄰接內聯 —— 結果

> 2026-05-31。C# 先行(`src/AprVisual.S2/`)。**通過硬閘門:bit-exact + 穩定贏過 S1。**

## 做了什麼

把每節點的 channel 子表(c1c2 (gate,other) pairs / gnd gates / pwr gates)**內聯**進
`NodeInfo`,消滅 73% singleton 熱路徑那次 `NodeInfos[nn] → TransistorList[idx]` 的 dependent
pointer-chase(序列化 L2 stall)。

- `NodeInfo` 16B → **32B**(2 個/64B line):加 `C1c2Count`/`GndCount`/`PwrCount`/`Inline` +
  `fixed ushort InlinePayload[7]`,保留 3 個 `Tlist*` 索引當 overflow fallback。
- 佈局:`InlinePayload = [c1c2 pairs][gnd gates][pwr gates]`,count-driven(可展開,無 0-terminator 載入)。
- **內聯涵蓋 96%**(payload = 2*c1c2+gnd+pwr ≤ 7);其餘 ~4% 高 fan-out(匯流排/clk)`Inline==0`,
  走原 `TransistorList` 路徑(未改、bit-exact)。fan-out 分布實測:mean 6.14,≤5=89.5% / ≤7=96.1% / ≤13=98.0%,長尾 max 32,476。
- 三個熱點改寫(分支 inline / 舊路徑):RecalcNode cls==2 檢查、RecalcNodeFast(gnd/pwr 掃描)、AddNodeToGroup(BFS)。
- **全程非託管指標**(`NodeInfo* ns` / `ushort* pay`)—— 依使用者要求,熱路徑資料用指標處理。
  (fixed-size buffer 必須經指標存取,正好契合。)

## 驗證(整機 NES,full_palette 300k hc)

- **bit-exact**:NodeStates checksum `0x794A43ABDF169ADA` @ t=300192 —— **與 S1 完全相同** ✓
- **效能**:interleaved-paired,8 輪丟 warmup,S1↔S2-A 交替:

| | S1 baseline | **S2-A** |
|---|---|---|
| 中位數 | 77,828 | **81,730 hc/s** |
| 平均 | 78,077 | 81,093 |
| **配對勝場** | — | **7/7 全勝** |
| **中位配對差** | — | **+3,255 hc/s = +4.18%** |
| 平均差 | — | **+3.86%** |

原始(diff = S2-A − S1):+4713 / +4101 / +3902 / +377 / +1831 / +3255 / +2933(round 5 較小但仍正)。

## 判讀

- **明確、可重現的勝利**(7/7、非雜訊),且 bit-exact。**這是整個 S2 第一個真正打敗 S1 的東西。**
- 落在我預測的 ROI 帶下緣(我估 +5–15%,實得 +4%)。低於 Gemini 的「>1.15× silver bullet」——
  印證我當初下修它的判斷是對的(那 350KB ushort list 多半已在 L2,省的是 L2-hit 而非 DRAM-miss;
  但消除序列化依賴仍實打實有 +4%)。
- 驗證了核心論點:S1 的天花板是**記憶體延遲**,內聯確實打中。

## 還能疊加的(後續)

1. **S2-B**:把 overflow(`Inline==0`)分支與冷路徑 `NoInlining` outline 出去,讓 96% 熱路徑迴圈更小、保 L1i(C# 要小心別讓熱 cascade 失效)。
2. **記憶體清除**(使用者要求):進 timed loop 前釋放 `_nodes`/`_transistors` 管理圖 + GC,確保計時無 GC pause、gen2 堆小。預期 perf 小但符合要求、利於量測穩定。
3. **變體 A/B**:目前是「32B 含 fallback 索引、Payload[7]/96%」;可試 union 佈局把 Payload 擴到 13(98%)看是否再賺。
4. 移植 Rust(`experiment/rust-s2/`)後各自量(預期 sign-flip,分開判定)。

## 對應 commit

S2-A 程式碼:`src/AprVisual.S2/Sim/WireCore{,.Recalc,.FastPath,.Group}.cs`。
