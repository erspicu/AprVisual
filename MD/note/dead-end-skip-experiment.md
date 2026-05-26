# dead-end-skip 實驗結果(failed)

> 日期:2026-05-26
> Branch:`aot-codegen`(diag + skip code 留作 reference,default 都 off)
> 起源:Phase 2.5 / bitset-bfs 失敗後,user 提出新方向 ── 用變數名 / 註解語意手動挑「真正可 LUT 化區」
> 經前置 diag 改為:用 node 圖屬性(Gates.Count、Callback、handler whitelist)識別「state 沒人讀」的 dead-end node,把它們在 BFS 中 skip

## TL;DR

`--dead-end-diag` 顯示 **38.22% 全 BFS work 落在「Gates.Count==0 且非 callback」的 node 上**。 看起來是巨大可省空間。

但 `--dead-end-skip`(嘗試 3 個版本)**全部 break 模擬正確性**:
- v1 整個跳過(no group, no walk)── CPU 立刻 derail 到 PC=$FFFF / IR=$92 KIL
- v2 conservative(skip group,保 channel walk)── CPU 不同 code path,PC 走錯
- v3 minimal(只 skip 最後 SetNodeState writeback)── 雖然 +4.1% bench rate,CPU 還是不同 code path

**結論**:dead-end-skip 在這 BFS 架構下不可行。 「Gates.Count==0」**不等於**「state 沒人讀」── state 透過 group walk 間接傳到 data bus / 其他 read 端。

## Diag 細節

`--dead-end-diag` 量到的 38.22% breakdown(full_palette.nes 50K hc):

```
total BFS node-visits:           42,342,230
dead-end candidate visits:       16,184,043  (38.22% of all)
  ├ leaf (≤2 channels, "safe"):   6,723,013  (15.88%)
  └ hub  (≥3 channels, risky):    9,461,030  (22.34%)
distinct dead-end nodes:         3,044
```

By prefix:
| Prefix | nodes | visits | % |
|---|---|---|---|
| (global) unnamed | 2,010 | 10.9M | 25.89% |
| ppu | 621 | 4.2M | 9.98% |
| cpu | 253 | 0.88M | 2.07% |
| cart.prg | 10 | 0.09M | 0.22% |

## Root cause:dead-end 真正多麼少

vid_* 視訊輸出網路(實證真 closed):
- vid_sync_l/h、vid_burst_l/h、vid_luma0..3_l/h、vid_emph(13 nodes)
- 唯一接到 `vid` (724) ── silicon 上 VID pin,接外部 NTSC encoder
- 我們從不讀
- ~30 transistors,**全 work 的 0.16%**

剩下 3,031 個 "dead-end candidate" 其實 NOT closed:
- `ppu.pal_d{0,2,3,4}_preout` ── pal data output pre-buffer
- 雖然 Gates.Count==0,channel 跟 `ppu.db` 連通
- group walk 內 pal_preout 跟 db 共享 resolution
- skip writeback 改變 group flag 結果 → db 讀錯 → CPU 拿錯 opcode

要 真 safe 必須:**leaf 自己 + group 內所有 co-resolved node** 都是 dead-end ── 等於 closed dead-end subgraph,需 union-find 遞迴。 真 closed subgraph 大概就 vid (~0.2%),加上一些 cart EXP pin、APU snd 輸出(已沒在跑),合計 << 1%。

## 跟之前 5 dead-end 同類 pattern

| 嘗試 | 假設 | 實際 |
|---|---|---|
| Per-chip parallel | graph 夠大可平行 | per-wave too small |
| Phase 2.5 codegen | owned set 夠大攤 dispatcher overhead | 62 named ALU mids only |
| S4 AOT batch | re-eval 全 graph 比 track 便宜 | 3-6× 慢 |
| Bitset BFS | dense scan 比 pointer chase 快 | 156× 慢 |
| **dead-end-skip** | **「Gates==0」 = 沒人讀** | **state 透 group walk 仍流到 db** |

5 個 dead-end 共通:**沒看清 BFS group resolution 的 indirect 依賴關係**。 micro-optimization 在 group-resolution architecture 下都失效。

## 工程產物

留下:
- `WireCore.DeadEndDiag.cs` ── `--dead-end-diag` (per-node visit count + leaf/hub split + by-prefix bucketing)
- `--dead-end-skip` CLI flag(default off,留作 reference 跟 future 重試的進入點)
- v3 minimal writeback-skip 版本邏輯保留在 Recalc.cs

不留:
- v1 / v2 早期變體(commit history 有,實際 code 已刪)

## 量化教訓

| 量 | 數字 |
|---|---|
| dead-end-diag 識別 "dead-end" 比例 | 38% BFS work |
| 實際 closed dead-end | < 1%(主要 vid_*) |
| **誤判率** | **~37 percentage points 是假陽性** |
| 真 safe 子集的 RoI | < +1% speedup,工程 1-2 天 |
| 不做 | 0% gain,0 工程 |

## 後續

此路線結束。 memory entry `[[dead-end-skip-dead-end]]` 記住「Gates==0 ≠ 沒人讀」這個常見誤判,避免下次重踩。

跟 [[s4-route-single-instance]] 的 wind-down 結論一致:single-instance CPU peak 仍 ~48.6K hc/s(Rust port),real-time 需要 ~17.8M hc/s。 距離 ~365×,任何 micro-skip 都不會關閉這個 gap。
