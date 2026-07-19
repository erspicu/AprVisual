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

## 真正的通則 = 架構級(所以延後,成本判斷非死路)
- **M7 正準重編號**:決定論 node 排序 → 毛刺根本不出現(從根拔,連 shim 都不用);或
- **兩階段 settle**(settle 完再 latch)。
兩者都動核心 settle loop(熱路徑成本,見 [[hotpath-ceiling-and-antipatterns]])或要開關級之上語意。
→ 在該架構工作排上日程前,DL 誠實留 UNDECIDABLE + 最小波及半徑 shim。關聯 [[socket-pattern-dut-immutability]]。
