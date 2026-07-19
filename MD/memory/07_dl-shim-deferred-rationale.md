---
name: dl-shim-deferred-rationale
description: DL(idl 輸入資料閂鎖)刻意留 scoped shim 不推機制——通則修法是架構級(M7/兩階段 settle),成本判斷
metadata:
  type: project
---

# DL shim 暫時不動的原因(2026-07-19 決策)

Source: `WireCore.System.cs:915-976`(DlShimStep,scoped $4016/$4017 + 2-bit signature);
`524`(M4 edge-latch 的 dl_idl transparent row);shim 列 `WebSite/s1a.html` DL row。

## 決策:DL 留 scoped shim,不進 milestone 機制臂(它是這批唯一沒機制臂的)
DL = 6502 輸入資料閂鎖(node `cpu.idl[7:0]`,DL=Data Latch)。純開關級 sim 在離散 settle 抓到
φ2 中途匯流排毛刺(idl=$00 vs settled db=$5D,5 bit;instance node-id ordering 抽籤,跟 DMC/LAE 同家族)。

## 為什麼不能便宜通則(其他顆補一個 M4/M6 row 就退役,DL 不行)
1. **「追隨 settled 外部匯流排」只在外部讀對** —— 內部 $4015 讀時外部匯流排是 open-bus 垃圾,同規則
   塞垃圾(AC OpenBus test 7 記錄);分辨外部/內部讀要**開關級之上的位址語意**。故 shim 硬釘 $4016/$4017。
2. **毛刺 vs 合法**:差 2+ 位元=毛刺、差 1 位元=合法手把移位邊界 → 靠啟發式 signature,非乾淨物理律。
3. **DL force 與 open-bus replay 互咬**:各自對,兩個一起=hang(ImpliedDummyRead 卡 $48xx)。不可組合。

## 真正的通則(⚠️ 2026-07-20 Gemini 諮詢更正)
- **M7 對 DL 是 unsound** —— 只把 glitch 變決定論(藏症狀,order-dependence 病還在)。**別把 M7 當 DL 的解。**
- **正解有正式名字 = inertial-delay filtering(慣性延遲濾波)**:
  - 通式 = **兩階段 / delta-cycle settle**(evaluate → 更新到 quiescent 再 latch);
  - 便宜局部版 = **settle guard**(只把那顆 latch 延到輸入群穩定才取值)—— **現行 DL shim 已是其局部版**,
    可正名為標準事件模擬技術,而非「臨時補丁」或「架構級的貴」。
- **可能從根拔掉**:wire_compute 的 contention 按**驅動強度(M1)**優先解(enhancement 下拉即刻贏 depletion 上拉,
  別過中間 X/high-Z 誤觸 latch)—— 很多零延遲 NMOS glitch 強度優先後就消失。
- IRSIM 用 RC/Elmore + event squash(input 在 R·C 到期前又變 → 取消)= 慣性延遲;我方零延遲難 bolt-on,故 settle guard 最划算。
→ DL 先留 scoped shim(=局部 inertial delay);通式 guard 待排程。詳 `MD/suggest/2026-07-20-gemini-three-walls`。
關聯 [[socket-pattern-dut-immutability]]、[[hotpath-ceiling-and-antipatterns]]。
