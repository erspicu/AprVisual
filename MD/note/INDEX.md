# MetalNES 研究筆記 — 索引

研究對象：`ref/metalnes-main`（[github.com/redirtfsh/metalnes](https://github.com/redirtfsh/metalnes) 風格的 OSX 版，作者 nesdev 圈內人）

> **MetalNES 一句話**：用一個統一的「電晶體 + 線網」開關層級模擬引擎，把 Visual2A03 / Visual2C02 兩顆晶片的網表，加上主機板上的 TTL 晶片（74LS139、74LS373、74LS368、74HC04、SRAM、4021…），全部接在一起跑出一台**電晶體層級的 NES**，並輸出影像（含 composite NTSC 電壓階梯）與音訊。

**這個專案對我們的意義**：它就是我們設計文件（`MD/struct/`）裡描述的「全封裝晶片架構」的**已實現版本，但跑在 CPU 上**。它證明了「switch-level netlist → 可執行模擬器」這條路真的走得通，而且把我們會踩到的雷（reset 行為、bus 整合、support chip、dynamic node、迴路）全部用實際程式碼解過一遍。它就是我們 CPU evaluator 階段最好的參考實作 —— 但**它的演算法是 Visual6502 chipsim.js 的優化版，並沒有做我們設計文件主張的「邏輯抽取 / IR / CUDA codegen」**，這正是我們可以往前推進的空間。

## 閱讀順序

| 順序 | 文件 | 內容 |
|------|------|------|
| 1 | [00_概觀_專案結構.md](./00_概觀_專案結構.md) | 專案目標、目錄結構、技術棧、與我們專案的關係 |
| 2 | [01_模擬核心演算法.md](./01_模擬核心演算法.md) | `wire_compute` 引擎：node group、`getNodeValue`、flags 查表、相對 Visual6502 的優化 |
| 3 | [02_模組化網表系統.md](./02_模組化網表系統.md) | `wire_defs` 模組組裝、connection=always-on transistor、`.js` 格式、support chip 全建成電晶體 |
| 4 | [03_系統整合與週期推進.md](./03_系統整合與週期推進.md) | `system_state`、handler（RAM/ROM/clock/video/audio）、reset、step loop、callback、threading |
| 5 | [04_驗證與測試策略.md](./04_驗證與測試策略.md) | 子電路 unit test、blargg test ROM、`$6000` 簽章、靠命名節點讀內部暫存器 |
| 6 | [05_對本專案的啟示.md](./05_對本專案的啟示.md) | 可直接抄的、要做得不一樣的、與設計文件的差距分析、建議的下一步 |

## 與既有文件的關係

- `MD/struct/` 內 7 份是**規劃**文件（我們想做什麼）
- 本目錄 `MD/note/` 是**對既有實作 MetalNES 的研究**（別人怎麼做的）
- `MD/struct/07_參考資料與實作對照.md` 講的是 Visual6502 的 `chipsim.js`（原始 JS 演算法）；MetalNES 是這個演算法的 C++ 工程化升級版，兩者可對照看
