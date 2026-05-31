# S2 IR 設計(CPU-first)—— 我們自己的設計

> 來源:Gemini 3.1-pro CPU-first IR 諮詢(原文 log:`tools/knowledgebase/message/`,prompt 在
> `workflow`-era tmp);加上我(Claude)的評價 + math-algos 的實測教訓 + 自己的設計。
> 使用者定向(2026-06-01):**CPU-first(非 GPU);先求一版 IR 正確 + 速度不比 S1 慢,當階段性產出。**

---

## 0. 最重要的框架修正(使用者洞察 + Gemini 同意)

**以前走偏 = GPU-think。** 舊計畫 S4 目標是 GPU bit-sliced batch kernel,把 IR 推向 **oblivious / 批次**
(每步算全部 ~14.7K 節點、跨多實例 bit-parallel)。對 GPU 吞吐很好,但對**單實例 CPU 延遲是致命的**:
O(N)=14.7K vs 事件驅動 O(D)=610,且 i-cache 崩(實測 3–6× 慢,full oblivious 121×)。

→ **新 S2 IR 明確 CPU-first、單實例:必須擁抱事件驅動稀疏性,絕不批次。** 任何加速只能來自
「讓每個 event 更便宜」或「縮小 dirty set」,絕不來自「一次算全部」。

---

## 1. Gemini 的核心判定(我評價:大致正確,且被 math-algos 實測佐證)

| Gemini 主張 | 我的評價 |
|---|---|
| **per-node IR interpreter 打不過 80K S1,會更慢** | ✅ **被 math-algos 實測佐證**:當年 IR interp 對 ~41K S1 = break-even;S1 現在 ~80K(R-1+S2-A),per-node dispatch 的相對開銷在更緊的預算下只會更吃虧。**interpreter 的天花板 = break-even,在 80K 下可能略負。** |
| **唯一贏路 = macro-block codegen**:IR 當編譯期工具,把多節點鏈 collapse 成單一編譯 C# 方法,縮 D + 消 queue traffic | ✅ 同意,且與歷史 r2 指引一致。關鍵是**縮小 D**(把多節點吸進一個 block,只在 block 邊界排隊),不是讓 per-node eval 更快(那條已被 S1 封死)。 |
| **CPU-first:block 邊界事件驅動、block 內 oblivious**(直線編譯碼塞 L1 i-cache);logic 用 inline bitwise、bus 用 256-LUT | ✅ 漂亮。block 內 oblivious 是「**局部** oblivious」(~50 節點),和全域 oblivious(14.7K,死)完全不同 —— 局部塞得進 i-cache。 |
| **熱匯流排(cpu.db/ab)用 unrolled BFS**:把 BFS 針對該 bus 形狀硬編(寫死 gate/neighbor 索引、無迴圈無 array-chase),仍建同一個 flag byte + 同一個 LUT → bit-exact | ✅ **這是關鍵洞見**。它繞過了 math-algos 卡死的「結構抽取」—— 不把 bus 變成 boolean,而是把 S1 的動態 BFS **針對已知形狀展開成直線碼**。bit-exact by construction(做的事和 S1 完全一樣,只是展開)。 |
| **MVP:先手寫一個 macro-block 證明 ≥ S1,再寫 codegen** | ✅ 完全符合 beat-S1 規則(先在最小原型驗證再投資)。 |

**Gemini 唯一略偏**:它說 interpreter 會掉到 ~35-40K —— 那是假設 IR **完全取代** S1(per-node IR 全網表)。
我們的設計是 **hybrid**(IR 只接管可抽取的子集,其餘走 S1),hybrid 的非-IR 路徑 = 80K S1,所以整體
是 break-even 附近,不是 35-40K。但結論方向(interpreter 不會贏)成立。

---

## 2. 我們的設計(分階段;CPU-first;event-driven)

### 共同骨架(不變)
- **S1 仍是主控**:dirty queue、settle、bit-exact 群解 + 256-LUT + floating tie-break 全保留。
- **混合 dispatch**:queue pop 出來若屬於 macro-block → 跳編譯後的 block delegate;否則 → S1 RecalcNode。
- **block 邊界排隊**:event 打到 block 內節點 → enqueue **block id**(不是 node),block 內部 oblivious 算完。
- **絕不**:全域 oblivious、per-node function-pointer queue(indirect-branch 死)、GPU batch。

### Phase A — IR 基礎 + 正確性框架(本次目標:正確 + 不變慢)
1. **IR 抽取器(編譯期)**:在 Reset 從**建構期管理圖**(`Nodes[].C1c2s/Gates`,與 S2-A 內聯佈局無關)
   分析網表,分類:(A) feed-forward boolean island、(B) 可 unroll 的熱 bus、(C) 殘餘 SCC(走 S1)。
2. **verify-then-enable(正確性由構造保證)**:任何 IR/block 產物,先跑一輪「逐節點 EvalBlock == S1 值」
   驗證(整機 full_palette boot + N cycle),**只對零不符的啟用**;其餘永遠走 S1。→ 整機 checksum
   `0x794A43ABDF169ADA` **必然 bit-exact**(IR 部分 == S1,其餘 == S1)。
3. **第一個 block(MVP,手寫)**:挑一個最熱、形狀相對規則的子系統(候選:PPU hpos/vpos 計數器 —— 每 pclk
   都動、結構規則;或 CPU ALU)。手寫其 oblivious eval(bit-exact,必要處用 256-LUT),wire 進 queue,
   verify + bench。
   - **若 ≥ S1**:codegen 路徑被證明 → Phase B 自動化。
   - **若 break-even**:滿足「不變慢」階段性產出(IR 基礎 + 一個正確 block);記錄,Phase B 再追速度。
   - **若更慢**:誠實記錄(interpreter/單 block 開銷 > 省下的 D),回頭重挑 block 或調整邊界。

### Phase B — macro-block codegen(追速度,之後)
- Roslyn / `Reflection.Emit` 把 block 的 oblivious eval 自動生成 C# delegate(編譯期一次)。
- 熱 bus 用 unrolled-BFS codegen(Gemini §3)。
- 目標:把夠多熱節點吸進 ~50–100 個 block,縮 D + 消 queue traffic → 真正 > 80K。

---

## 3. 誠實的天花板評估

- **純 interpreter**:break-even 是上限(math-algos 實測 + Gemini),在 80K 下可能略負 → **不保證「不變慢」**。
- **macro-block codegen**:唯一可能 > S1 的路,但**正確性是殺手**(bit-exact 手寫子系統極難;math-algos 當年
  correctness 卡了 5 個 firing)。verify-then-enable 把「正確性」變安全(只啟用驗證過的),風險降為「夠多
  block 能驗過 + 真的更快」。
- **本次階段性產出的務實定義**:IR 基礎(抽取器 + verify 框架 + 混合 dispatch)+ 至少一個 **bit-exact 且
  不比 S1 慢**的 block。若連一個都做不到「不變慢」,則如實回報、解除任務(使用者已授權)。

---

## 4. 為何這次可能不同(對比 math-algos 的失敗)

- math-algos 卡在「per-node IR interp 只能 break-even」+「codegen 走 GPU-style oblivious 撞牆」。
- 這次:**(a)** 明確放棄 interpreter 當終點(只當正確性基礎);**(b)** codegen 走 **CPU-first 局部-oblivious
  macro-block**(非全域);**(c)** 熱 bus 用 **unrolled-BFS**(繞過結構抽取的死路);**(d)** verify-then-enable
  讓正確性不再是賭注。
- 不保證成功,但這是建立在「兩個實測天花板 + Gemini + r2 + 使用者 GPU-astray 洞察」上的、賠率最好的設計。
