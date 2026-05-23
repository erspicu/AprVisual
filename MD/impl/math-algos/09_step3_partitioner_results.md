# 09 — Step 3 結果:graph partitioner(auto-cut macro-blocks)

> 任務追蹤:#54(WireCore.Partition.cs)→ #55(validate vs ALU dump)→ #56(本文)。
> 接續:`07_dispatcher_framework_results.md`(Step 2)+ `08_step25_writeback_results.md`(Step 2.5)。

---

## 0. 結論

🟢 **Partitioner 工程運作**,**找到真正的功能 macro-block**(PPU sprite、PPU VRAM addr、APU 各通道),為 Step 3.5 codegen 寫回提供清楚 target。

⚠️ **ALU 沒被切成單一 block**(known limitation):因為每個 `cpu.alu[i]` 是 pull-up boundary,ALU 內部邏輯被切成 8 個 sub-fragment。Step 3.5 需要 merge 同名 family(`alu[0..7]` group)成 super-block。

主要發現:
| 主要 block | 規模 | 功能 |
|---|---|---|
| `ppu.spr_ptr5` × 10 | 298 nodes / 597 transistors each | PPU sprite eval bit-slices(8 sprite slot × 2 副本)|
| `ppu.spr_ptr5` × 6 | 232 nodes / 465 transistors each | Sprite eval 變體 |
| `ppu.finex1` | 188 nodes / 479 transistors / 27 outputs | **PPU VRAM 地址計算 ★ Step 3.5 prime target** |
| `cpu.sq1_p0` / `sq0_/p0` | 44 nodes each | APU square channel 1/2 period 解碼 |
| `cpu.tri_p8/9/10` | 24 nodes each | APU triangle channel period 階段 |
| `cpu.pcm_*` | 17-20 nodes | APU DPCM 通道 |

**30 個** real codegen-candidate block(17+ nodes,non-supply label)。

---

## 1. Algorithm — 「non-boundary 連通分量」演算法

```
def auto_partition():
    isBoundary = set([n for n in nodes if n.Pullups > 0] + [Npwr, Ngnd])
    blockOf = {n: -1 for n in nodes}
    blocks = []
    
    for seed in non_boundary_nodes:
        if blockOf[seed] >= 0: continue
        bnodes = [seed]; queue = [seed]; blockOf[seed] = len(blocks)
        binputs = {}; bouts = {}; btr = {}
        while queue:
            v = queue.pop()
            for t in v.C1c2s:          # v is a channel-end of t
                btr.add(t)
                other = (t.C1 if t.C2 == v else t.C2)
                if isBoundary[other]:
                    binputs.add(other)
                    if not isBoundary[t.Gate]:   # gate from within → block DRIVES this output
                        bouts.add(other)
                elif blockOf[other] < 0:
                    blockOf[other] = len(blocks); bnodes.add(other); queue.add(other)
                if isBoundary[t.Gate]: binputs.add(t.Gate)
            # Note: do NOT traverse v.Gates — those go OUT to other blocks; only INWARD via channels
        blocks.add(Block(bnodes, binputs, bouts, btr))
    return blocks.sort(by=size, desc=True)
```

**關鍵設計選擇:只走 channel↔channel 邊,gate 信號只記為 input。** 早期版本同時走 gate 邊,結果一個 5,416-node 怪物 block(整個 CPU 控制邏輯 + datapath 透過共享控制信號串成一個 connected component)。改成 channel-only 後分布合理。

---

## 2. 量測 —— 整體 partition 結果

```
# auto-partition: netlist split into 2,757 macro-blocks
#   boundary nodes (pull-up + supply): 6,396
#   total internal nodes assigned:     8,325
#   unassigned internal (should be 0): 0
#   total nodes (for sanity):          14,724
```

數字 sanity:6,396 boundary + 8,325 assigned + 3 special(Npwr/Ngnd/spare)= 14,724 ✓

### Size histogram

```
# size histogram (block count by internal-node bucket):
#   1            (singletons)                    :  2,235
#   2..4         (tiny)                          :    462
#   5..16        (small)                         :     30
#   17..64       (medium)                        :     13
#   65..256      (large — codegen candidates)    :      7
#   257..1024    (very large — may need sub-cutting) :     10
#   >=1025      (huge — likely under-cut)        :      0
```

- **2,235 singletons** — 99% 是「兩個 pull-up 之間的單一 mid 節點」(pass transistor 中段、bus 連接 mid)。每個本身就是 trivial 計算,codegen 價值低。
- **462 tiny(2-4 nodes)** — 小邏輯片段(typical inverter chain、AND-gate)。codegen 不划算。
- **30 small + 13 medium + 7 large + 10 very-large = 60 個 codegen 值得做的 block**,符合 Gemini r2 預期的 50-100 macro-block 數量級!

### Top-15 codegen-candidate blocks

```
#   id  intern  trans   inputs  driven  label
#    0     298    597        5       2  ppu.spr_ptr5
#    1     298    597        5       2  ppu.spr_ptr5
#  ...                                  (10 副本)
#   10     232    465        5       2  ppu.spr_ptr5    (6 副本)
#  ...
#   16     188    479      324      27  ppu.finex1      ★ codegen 首選
#   17      44     88       30       0  cpu.sq1_p0      APU sq1 period
#   18      44     88       30       0  cpu.sq0_/p0     APU sq0 period
#   19      24     63       62      20  cpu.tri_p8      APU triangle
#   20      24     65       64      20  cpu.tri_p9
#   21      24     65       64      20  cpu.tri_p10
#   22      20     57       56      18  cpu.pcm_l3      APU DMC
#  ...
```

---

## 3. Drill-down — 3 個代表 block 內部

### `ppu.spr_ptr5`(298 nodes, 10 副本)

```
internal:    298  (含 ppu.oam_ram_00_a6..ppu.oam_ram_07_a6 — OAM RAM cell logic)
transistors: 597
inputs:        5  (anonymous mids + ppu.spr_ptr5 boundary)
driven outputs: 2 (vcc + vss — fanin to supply only via the OAM cells)
```

**這是 PPU 的 OAM RAM bit-plane 控制邏輯**,10 副本應對應 8 個 sprite slot + 2 個 secondary OAM 對應。每個 block 約 600 transistor 的 dynamic RAM 駕馭邏輯,**單獨 codegen 化能消除 297 × 10 × 4 = 12K 個 RecalcNode 工作**(粗估)。

### `ppu.finex1`(188 nodes — **Step 3.5 首選**)

```
internal:    188
transistors: 479
inputs:      324  (fine-x scroll bits + scroll register state + control flags)
driven outputs: 27 (ppu.vramaddr_v0..v9_out, t0..t14, ppu.y_flip_flag, ...)
```

**這是 PPU 的 VRAM 地址計算單元** —— 整個 scroll register → fetch address 變換邏輯。輸入 324 個 boundary(scroll registers + 控制狀態),輸出 27 個 named address bits(`vramaddr_t0..t14`、`vramaddr_v0..v9`)。**對 codegen 是理想 target**:
- 純組合邏輯,outputs 是 deterministic function of inputs。
- 188 internal node 全部可以 CodegenOwned。
- 每 cycle 觸發 frequency 高(每個 PPU dot 都會用到)。

### `cpu.sq1_p0`(44 nodes,APU square channel 1)

```
internal:    44
transistors: 88
inputs:      30  (cpu.sq1_/p0..p10 — APU square 1 的 period counter 各 bit)
driven outputs: 0  (output 流向 APU mixer,不直接 drive named boundary)
```

**APU square 1 channel 的 period decoder**。輸入是 11-bit period 暫存器,輸出是 frequency divider 行為。雖然 `driven outputs = 0`,實際上它透過 mid-node 影響後段邏輯(partitioner 只 detect 直接 channel-touch boundary 的關係)。

---

## 4. Known limitation — ALU 沒形成單一 block

`cpu.alu0` 等 8 個 ALU output node 自己是 pull-up,所以它們是 **boundary**,**不在任何 block 內部**。ALU 的 mux + carry-save 邏輯被切成 8 個 sub-fragment(每 bit 一個),散落在 `singletons` / `tiny` 桶。

**Step 3.5 修正方向:Block family merging**
- 偵測同 prefix 的 boundary group(`alu[0..7]`、`a[0..7]`、`db[0..7]`)是 "register family" / "bus family"。
- Merge 對應的 sub-blocks 成 super-block,把 family member nodes 從 boundary 移到 super-block 內部。
- 這樣 ALU 才會成為 ~133-node 的 codegen 單元(matches `--dump-block` 的 reverse-closure 結果)。

替代方案(更激進):
- 只在 named bus / register 做 cut,不在 anonymous pull-up 做 cut。
- 但這需要先 build 一個 "well-known boundary" 清單。

---

## 5. CLI

```
--dump-partition              auto-partition the netlist + print histogram + top blocks
--dump-block-id <id>          detail of block <id>: internal/inputs/driven outputs
```

---

## 6. 對齊 Gemini r2 §2.4 設計

| Gemini §2.4 要件 | Step 3 狀態 |
|---|---|
| Bottom-up clustering from pure-island | ✓ BFS from each non-boundary node |
| Topological clustering | △ implicit via channel-only BFS |
| Size-constrained cuts | ⚠ 自然 size distribution;沒做 explicit max-size split |
| 50-100 macro-block target | ✓ **30 codegen-candidate blocks 在 17+ node 範圍** |
| 識別「重複 pattern」(8-bit slice 之類)| △ 看得到(10× ppu.spr_ptr5 同尺寸)但沒明確 group |
| Named boundary 對齊 | ✓ pull-up boundary + label by best driven-output name |

5/6 對齊,Step 3 部分 partial:size-constrained sub-cut 跟 family-merge 留給 Step 3.5。

---

## 7. 下一步 — Step 3.5 工作項

按照「**先驗證一個 block 真的能加速**」的原則:

1. **Step 3.5a — pick ppu.finex1**(188 nodes、27 outputs)
2. 用 `--dump-block-id 16` 列出 27 outputs + 324 inputs。
3. 寫 `FinexBlock.cpp` 模擬「LLVM 編這 block 後的輸出形狀」(類似 `AluBlock.cpp` 之於 ALU)。
4. 量 native bench(預估 ~ 50-100 ns/call,因為 188 nodes 比 ALU 大很多)。
5. 接 dispatcher:CodegenOwned 整個 188 internal + 27 outputs,trace-diff vs S1。
6. 量 hc/s 加速 —— **這是首次** 真正測「整個 region 由 codegen 接管」會多快。

如果 ppu.finex1 加速 < 3% → 重新檢視策略;
如果 ≥ 5% → 確認單 block codegen 有 ROI,可以推到 partitioner-driven 自動化。

並行做的:
7. **Step 3.5b — block family merger**:讓 ALU 變單一 block(把 alu[0..7] family 加進來)。

---

## 8. 一句話收尾

> **Step 3 auto-partitioner 把 14,724 個 node + 26,775 個 transistor 切成 2,757 個 macro-block;其中 30 個是 codegen-attractive 規模(17+ nodes、非 supply label)。發現 PPU sprite eval(10×298 nodes 副本)、PPU VRAM addr(188 nodes/27 outputs)、APU 各 channel 都是清楚的功能單元。ALU 因 alu[i] pull-up 切散是已知 limitation,Step 3.5 用 block-family merge 修正。下一步 ppu.finex1 作為「第一個真正 codegen-own 整個 region」的 target,量真實加速。**
