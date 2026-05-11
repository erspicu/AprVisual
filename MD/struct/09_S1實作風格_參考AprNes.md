# S1 實作風格 — 參考 AprNes

研究對象：`ref/AprNes`（你自己寫的 C# + WinForms NES 模擬器，目標 .NET 10 / net48 雙targets，rendering 用 Win32 GDI）。本文件整理它的設計風格特色，讓你挑哪些要用在 S1（C# 改寫 MetalNES 開關層級引擎）。

---

## Part 1：AprNes 的 rendering 方式（最簡單顯示 256×240 的做法）

### 1.1 它怎麼做的（call chain）

AprNes **完全不用 PictureBox / Bitmap.Image / Invalidate-Paint**。它直接把一塊 unmanaged `uint*` ARGB buffer 用 GDI `SetDIBitsToDevice` blit 到一個 `Panel` 的 HDC 上。鏈條：

```
NesCore.digitalFrameRgb / AnalogScreenBuf   ←  unmanaged uint*（256×240×4 bytes，ARGB），由 PPU 寫入
        │
RenderPipeline (純 uint* 運算，平台無關)      ←  xBRZ / Scalex / NN / scanline 濾鏡（S1 不需要）
        │
Render_resize : InterfaceGraphic             ←  WinForms 端的 GDI 殼
   .init(uint* input, Graphics device)       ←  device = panel1.CreateGraphics()
        └→ NativeGDI.initHighSpeed(device, w, h, uint* data, x, y)
              ├ 建一個暫時的 Bitmap + Graphics 取得 hdcSrc
              ├ grDest.GetHdc() 取得 panel 的 hdcDest
              └ 填好 BITMAPINFOHEADER（biHeight = -h 代表 top-down）
   .Render()
        └→ NativeGDI.DrawImageHighSpeedtoDevice()
              └→ gdi32!SetDIBitsToDevice(hdcDest, x, y, w, h, 0,0,0,h, data_ptr, ref info, DIB_RGB_COLORS)
```

核心就一個檔案 `tool/NativeRendering.cs`（~80 行）+ `tool/NativeAPIShare.cs` 裡的 3 個 P/Invoke（`SelectObject`、`DeleteObject`、`SetDIBitsToDevice`）+ `BITMAPINFO` / `BITMAPINFOHEADER` 結構。**這整套可以幾乎原樣搬過來。**

幾個關鍵細節：

- `biHeight = -h`（負值）→ DIB 是 top-down（第一個 pixel 在左上角），跟我們 framebuffer 的記憶體順序一致，不用上下翻
- `biBitCount = 32`、`biCompression = BI_RGB` → buffer 就是 `0xAARRGGBB` 的 `uint`（GDI 其實忽略 alpha，當 0x00RRGGBB 看）
- buffer 是 **unmanaged**（`NativeMemory.AlignedAlloc` 或 `Marshal.AllocHGlobal`），不是 `uint[]` —— 這樣 P/Invoke 不用 pin、GC 不用管
- 雙緩衝：`AnalogScreenBuf`（front，emu 寫）↔ `AnalogScreenBufBack`（back，GDI 讀）swap，避免讀寫同一塊
- rendering 跑在獨立 thread（`RenderThreadLoop`），用 `ManualResetEventSlim` 跟 emu thread 握手；FPS 限制用 `Stopwatch` busy-wait

### 1.2 對 S1 的最簡版本（建議）

S1 是 CPU evaluator 驗證階段，**不需要濾鏡、不需要雙緩衝、不需要獨立 render thread**。最簡：

```csharp
// 一個 Form，上面放一個 256x240（或放大整數倍）的 Panel
// framebuffer = unmanaged uint[256*240]，PPU/composite 解碼後寫進去
// 每幀（VBL）：在 UI thread 上呼叫 NativeGDI.DrawImageHighSpeedtoDevice()
//   （或更簡單：emu 跑在 UI thread 上，每幀 step 完就 blit；S1 不追求 60fps，先求正確）

// NativeGDI.cs ← 幾乎原樣抄 AprNes/tool/NativeRendering.cs + NativeAPIShare.cs 的 GDI 部分
// 啟動時：NativeGDI.initHighSpeed(panel.CreateGraphics(), 256, 240, fbPtr, 0, 0)
//   （若 Panel 放大顯示，可以放大 framebuffer 再 blit，或用 StretchDIBits 代替 SetDIBitsToDevice）
// 每幀：NativeGDI.DrawImageHighSpeedtoDevice()
```

**比 GDI 更簡單但稍慢的替代**（如果連 P/Invoke 都不想碰）：
- `var bmp = new Bitmap(256, 240, 256*4, PixelFormat.Format32bppRgb, (IntPtr)fbPtr);`（Bitmap 直接包 unmanaged buffer，零拷貝）→ `panel.BackgroundImage = bmp` 或在 `Paint` 事件 `e.Graphics.DrawImageUnscaled(bmp, 0, 0)` → 每幀 `panel.Invalidate()`。比 SetDIBitsToDevice 慢一點（多一層 GDI+ 包裝），但程式碼最短，S1 階段完全夠用。

→ **我的建議**：直接抄 AprNes 的 `NativeGDI` GDI 路徑（它已經寫好、測過、就 ~100 行），不用退而求其次。

### 1.3 CLI → 跳出視窗的結構（AprNes 的做法）

`Program.cs` 極簡：

```csharp
[STAThread]
static int Main(string[] args)
{
    if (args.Length > 0)
        return TestRunner.Run(args);          // 有參數 → headless 測試模式，回 exit code，Console 印 PASS/FAIL

    Application.EnableVisualStyles();
    Application.Run(AprNesUI.GetInstance());  // 無參數 → 跳出完整 GUI（singleton form）
}
```

`TestRunner.Run` 解析 `--rom X --wait-result --max-wait N --region R --screenshot ...`，跑 headless emu，偵測 blargg `$6000` 簽章，印 `PASS | rom | name` 或 `FAIL(code) | rom | (...)`，回對應 exit code。

→ **我們 S1 想要的**（你的需求：「最簡單 cli 的方式，到驗證時候載入 rom，畫面顯示在 cli 執行後跳出的 winform 視窗上的某個 UI 元件上 256×240」）：

```
AprVisual.exe --rom path\to\game.nes          → 跳出一個 Form，上面一個 Panel 顯示 256×240 即時畫面（switch-level 模擬）
AprVisual.exe --test path\to\test.nes         → headless，跑到 $6000 簽章出現，印 PASS/FAIL，回 exit code（無視窗）
AprVisual.exe --test-dir path\to\nes-test-roms → 批次跑一整個目錄
（無參數）                                      → 之後可以加個簡單的「開檔」GUI，但 S1 不急）
```

---

## Part 2：AprNes 的設計風格 — 特色清單（讓你挑）

下面每一條：**它怎麼做** + **我建議 S1 用不用** + **理由**。✅=建議用、🟡=部分用/看情況、❌=S1 先不用。

### A. 記憶體與資料結構

| # | 特色 | S1 建議 | 理由 |
|---|------|---------|------|
| A1 | **`unsafe` + 原始指標**（`byte*` `uint*` `byte**`）作為核心資料 | ✅（核心熱路徑） | 你明確說要「低階指標 array」。`wire_compute` 的 hot data（`byte[] nodeStates`、攤平的 `int[] transistorList`、`nodeinfo[]`）用指標跑 group BFS / recalc 內迴圈，省 bounds check |
| A2 | **`NativeMemory.AlignedAlloc(size, 64)` 配 unmanaged 記憶體**（.NET 10），net48 fallback `Marshal.AllocHGlobal` | ✅ | 64-byte 對齊 = cache line / SIMD 友善；GC 不用管、P/Invoke 不用 pin。我們只 target .NET 10 的話可以拿掉 fallback |
| A3 | **顯式 `FreeUnmanagedMemory()`** 一次釋放全部指標 | ✅ | 配 A1/A2 必須有 |
| A4 | **bank-pointer array**（`byte** chrBankPtrs[8]`、`mem_read_page[addr>>13]` 用 function pointer 做 8-entry page dispatch） | 🟡 | AprNes 是行為級模擬才需要 bank switching。S1 的開關層級引擎不需要這個；但「攤平成一塊連續陣列、null 結尾子清單」（MetalNES 的 `_transistor_list` 風格）是同精神，**那個要用** |
| A5 | **struct 打包成 64-byte block 一次複製**（`PtrBlock8`：8×`byte*`）取代 8 次元素 assign | 🟡 | 微優化，等 profile 再說 |
| A6 | **`System.Numerics` / `Vector<T>` SIMD** + `Unsafe.InitBlockUnaligned` | ❌（S1） | S1 不需要。S2/S3 IR 攤平後、S4 bit-slicing 時才有意義 |

### B. 程式碼組織

| # | 特色 | S1 建議 | 理由 |
|---|------|---------|------|
| B1 | **一個巨型 `static partial class NesCore`** 拆成 ~15 個 .cs（CPU.cs / PPU.cs / MEM.cs / Main.cs / APU.cs ...），全 `static` 無 instance | ❌ → 改用 **分離 class + 組合** | AprNes 是「一台 NES」，static 巨型 class 對它 OK。但我們 S1 之後要長出 IR / codegen / 多 backend / 也許多 instance，巨型 static class 會卡死擴展。建議：`NetlistGraph` / `WireCompute`（內部可用指標欄位）/ `SystemState` / `handler_*` 各自是 class。**但「partial class 拆多檔」這個習慣可以保留**（例如 `WireCompute` 拆成 `.Recalc.cs` / `.Group.cs`） |
| B2 | **mapper 用 `IMapper` interface + 一堆 `MapperNNN : IMapper`** + `MapperRegistry` 查表 | 🟡 | S1 只 NROM，先不需要。但「handler 用 interface + registry」是好模式（對應 MetalNES 的 handler 機制）——`IWireHandler` / `register_handlers<T>("*func<ram>")` |
| B3 | **`#if NET10_0_OR_GREATER` 條件編譯**雙 target（.NET 10 + net48），新 API 有 fallback | ❌ | 你說 .NET 10，那就**只 target .NET 10**，不要雙 target 的包袱。直接用 `NativeMemory.AlignedAlloc`、`delegate* unmanaged<>`、`[UnmanagedCallersOnly]`、collection expressions、`required` 等新語法 |
| B4 | **`InterfaceGraphic` 介面**抽象 rendering backend（GDI / analog / headless） | ✅ | 我們也會有「視窗顯示」vs「headless 測試」兩種輸出，留個 `IFrameSink { void Present(uint* fb); }` 之類的介面很值得 |
| B5 | **C# 11 LangVersion** | → C# 13（.NET 10 預設） | 用最新 |

### C. 熱路徑優化技巧

| # | 特色 | S1 建議 | 理由 |
|---|------|---------|------|
| C1 | **function-pointer dispatch table**（`delegate* unmanaged<void>* opFnPtrs[256]` 做 CPU opcode dispatch；`ppuTickVisibleTable[341]` 做 per-dot PPU），.NET 10 用 `[UnmanagedCallersOnly]` 讓 `calli` 跳過 GC safe-point poll | 🟡 | 開關層級引擎沒有「opcode dispatch」這種東西——它的內迴圈是 group BFS + flags OR + 查表。**但 MetalNES 的 `_flags_to_state[256]` 查表這招要用**（已在 `01_技術指南` / `note/01` 講過）。等 S2 把 IR 攤平成線性指令序列時，「指令 → handler 的 dispatch table」就會用得上 |
| C2 | **`[MethodImpl(AggressiveInlining)]` 在小熱函數**、`[MethodImpl(NoInlining)]` 在罕用路徑（保持熱路徑 code 小） | ✅ | 標準功夫，直接用 |
| C3 | **`Optimize=true` 連 Debug 都開**、`AllowUnsafeBlocks`、x64 | ✅ | 配 A1 必須 `AllowUnsafeBlocks`；x64 + Optimize 合理 |
| C4 | **`Parallel.For` 做可平行的像素運算**（NN scaling 等） | 🟡 | S1 用不太到。但若 framebuffer 解碼（palette index → RGB，256×240）想平行可以用，不過 61440 個 pixel 其實單執行緒也很快 |

### D. 執行緒與時序

| # | 特色 | S1 建議 | 理由 |
|---|------|---------|------|
| D1 | **emu 跑在自己的 thread，render 跑在另一個 thread**，用 `ManualResetEventSlim`（`renderReady` / `renderDone`）握手 | 🟡 → S1 先單執行緒 | MetalNES 也是 emu thread + GUI thread + dispatch queue。但 S1 第一版**先單執行緒**（emu 跑在 UI thread 或一個 worker thread，每幀 blit），求正確不求 fps。等正確了再分執行緒。對應 MetalNES 的 `wire_thread` + `dispatch_queue` |
| D2 | **雙緩衝 framebuffer + swap** | 🟡 | 單執行緒時不需要。分執行緒後再加 |
| D3 | **FPS 限制用 `Stopwatch` busy-wait** + `Thread.Sleep(1)` 粗調 | ❌（S1） | S1 先不限 fps（驗證時跑越快越好；要看畫面時再說） |
| D4 | **批次 step**（emu 一次跑一批 cycle 再讓 render） | ✅（精神） | 對應 MetalNES 的 `step(1024)`。S1 可以「一次跑到下一個 VBL」當一批 |

### E. CLI / 測試 / 工具

| # | 特色 | S1 建議 | 理由 |
|---|------|---------|------|
| E1 | **`Program.Main`：有參數 → headless test runner（回 exit code，Console 印結果）；無參數 → GUI** | ✅ | 正是你要的「最簡單 cli」結構 |
| E2 | **共用測試框架**（link `../unittest/NesTestFramework/` 的 `IEmulatorCore` / `BlarggTestRunner` / `TestCatalog` + adapter），blargg `$6000` 簽章偵測，`--rom X --wait-result --max-wait N --region R` | ✅ | 強烈建議——這套等價驗證基建（`IEmulatorCore` 抽象 + blargg runner）正好是 S1 過關條件需要的。如果你那個 `unittest/NesTestFramework` 是獨立的，我們可以也 link 它，讓「AprNes（行為級）」「MetalNES-C#（開關層級）」「未來 IR backend」「未來 CUDA backend」都實作同一個 `IEmulatorCore` → 同一套 runner 比對。**這直接呼應設計文件 §13.6 的「同一份介面、多 backend、逐節點比對」** |
| E3 | **`--screenshot` / `--timed-screenshots` / `--benchmark` / `--dump-debug` 等進階旗標** | 🟡 | S1 先做 `--rom`（顯示）+ `--test`（PASS/FAIL）。`--benchmark`（量 cycles/sec）留到 S3，但介面可以先預留 |
| E4 | **`TestRunnerCore` 把 base-dir / save-screenshot / benchmark-filter 用 `Func<>` 注入**（核心不依賴 WinForms） | ✅ | 好設計——測試核心邏輯與平台殼解耦。我們的 test runner 也該這樣 |
| E5 | **以 `TriCNES` 為 reference**，註解寫對方檔案行號 | ✅（換成 MetalNES） | 我們的對照組是 `ref/metalnes-main`——註解寫 `wire_module.cpp:1583` 之類，方便對照。`MD/note/` 已經做了這個對照工作 |

### F. 其他可借鏡的小東西

- `RomDatabase.cs` — iNES header + ROM 資料庫（CRC → mapper/region）。S1 只 NROM，先不用，但 iNES 解析（`TestRunner.cs` 那種極簡 header parse）要有
- `LangINI.cs` — i18n。S1 不用
- 多個 mapper 檔每個 ~100-300 行、各自獨立 — 提醒我們：handler / module 也該一個檔一個，不要塞一起

---

## Part 3：S1 專案骨架（已採用 AprNes 風格）

> 以下骨架反映 Part 4 已拍板的決議：核心用 `unsafe` + unmanaged 指標、組織成**巨型 `static partial class`**（AprNes 風格）、rendering 用 GDI `SetDIBitsToDevice`、CLI 單一 exe 用 args 分流。

```
AprVisual.sln
  src/
    AprVisual/                         # 單一 WinExe（.NET 10-windows）— 不拆 class library，照 AprNes
      Program.cs                       # [STAThread] Main(args): args.Length>0 → TestRunner.Run(args)（headless）; else → MainForm
      MainForm.cs / MainForm.Designer.cs   # 一個 Panel（256×240，或整數倍放大）；--rom 時跳出並顯示即時畫面
      Sim/                             # ★ 核心引擎 = 一個 unsafe static partial class（暫名 SwitchSim 或 WireCore）
        WireCore.cs                    #   static unsafe partial class WireCore { ... } — 共用欄位/初始化/Free
        WireCore.Parse.cs              #   .js 模組格式解析（仿 MetalNES wire_defs；保留第 7 欄 IsWeak、pull marker）
        WireCore.Module.cs             #   組裝：instance node-id 配置、connection = gate 接 VCC 的永遠導通電晶體、名稱解析（a[7:0]/a[]/x|y|z/*wildcard）
        WireCore.Recalc.cs             #   recalcNodeList / processQueue / recalcNode（雙緩衝 recalc list + hash 去重）
        WireCore.Group.cs              #   computeNodeGroup / addNodeToGroup / getNodeValue（flags OR + 256-entry 查表 + _maxState 啟發式）
        WireCore.Handlers.cs           #   ClockHandler / RamHandler / RomHandler ...（callback = 假電晶體連假節點）
        WireCore.System.cs             #   載入 nes-001、裝 handler、真實 reset（setHigh res; step(192); setLow res）
        WireCore.Trace.cs              #   trace log 環形緩衝、dump cpu.a/x/y/p/s/pc 等
        WireCore.Native.cs             #   NativeMemory.AlignedAlloc(size,64) 包裝 + FreeUnmanagedMemory() 一次釋放全部指標
      // 核心欄位範例（全 static，全在 WireCore 這個 partial class 裡）：
      //   static byte*  nodeStates;       // [nodeId] 0/1（hot，每 recalc 狂讀）
      //   static NodeInfo* nodeInfos;     // [nodeId] flags + connections + tlist* 索引
      //   static int*   transistorList;   // 攤平鄰接表，null(0) 結尾子清單
      //   static byte   flagsToState[256];// 預算的 flags → state 查表
      //   static int*   recalcList; static int* recalcListNext; ...
      Render/
        NativeGDI.cs                   # ← 幾乎原樣抄 AprNes/tool/NativeRendering.cs（initHighSpeed / DrawImageHighSpeedtoDevice / freeHighSpeed / UpdateDataPtr）
        NativeApi.cs                   # ← 抄 AprNes/tool/NativeAPIShare.cs 的 GDI 部分（SelectObject / DeleteObject / SetDIBitsToDevice + BITMAPINFO/BITMAPINFOHEADER）
      Rom/
        NesRom.cs                      # iNES header 解析（NROM only，仿 AprNes/NesCore/nesrom 風格）
      Test/
        TestRunner.cs                  # --rom（顯示）/ --test（headless，跑到 $6000 簽章，印 PASS/FAIL，回 exit code）/ --test-dir（批次）/ 預留 --benchmark
        (可選 link) NesTestFramework/   # 若沿用 AprNes 那套 IEmulatorCore + BlarggTestRunner（讓多 backend 共用同一 runner）
  data/  system-def/  ...              # 從 MetalNES 借來的 .js 模組定義（或我們自己寫）；S1 至少要 2a03 那套 + nes-001.js
  ref/  ...（已 gitignore）
```

組織風格注意（既然選了巨型 static partial class）：

- 整個開關層級引擎是**一個** `static unsafe partial class WireCore`，拆成上面那些 `WireCore.*.cs`。全 static、無 instance —— 跟 AprNes 的 `NesCore` 一模一樣。
- **代價要心裡有數**：S2 之後若要「同時跑多個 instance」（bit-slicing 之前的 CPU 多實例、或單純為了 benchmark 跑兩份），static 巨型 class 做不到「兩個獨立狀態」。到時若真需要，要嘛把 static 欄位包成一個 `struct State` + 一組指標、要嘛接受「一個 process 一個 instance」。S1/S2/S3 單 instance 都沒問題；這個包袱在 S4 bit-slicing（那本來就是「一份 code 算 N 個 instance」）反而不衝突。先照你的決定走，到時再說。
- handler（RAM/ROM/clock）可以是真正的小 class（或巢狀 static class），但「掛鉤」走 WireCore 的 callback 機制（假電晶體連假節點），跟 MetalNES 一致。

---

## Part 4：拍板決議（2026-05-12）

| 項目 | 決議 | 備註 |
|------|------|------|
| 核心引擎記憶體 | **`unsafe` + unmanaged 指標**（`NativeMemory.AlignedAlloc(size, 64)`，`byte* nodeStates` / `int* transistorList` / `NodeInfo* nodeInfos`，內迴圈零 bounds-check） | AprNes 風格；需 `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` |
| 畫面顯示 | **GDI `SetDIBitsToDevice`**（直接抄 AprNes 的 `NativeRendering.cs` + GDI P/Invoke）；unmanaged `uint*` framebuffer → `Panel` 的 HDC | `biHeight = -h` top-down；S1 先單緩衝、單執行緒、不限 fps |
| CLI / 視窗 | **單一 exe，`Program.Main` 用 args 分流**：`--rom X` → 跳 Form 顯示即時 256×240；`--test X` → headless 印 PASS/FAIL 回 exit code；`--test-dir` → 批次；無參數 → S1 先不做 | AprNes 風格 |
| 核心程式碼組織 | **巨型 `static unsafe partial class WireCore`**，拆成 `WireCore.Parse.cs` / `.Module.cs` / `.Recalc.cs` / `.Group.cs` / `.Handlers.cs` / `.System.cs` / `.Trace.cs` / `.Native.cs` | AprNes 風格；多 instance 的限制如 Part 3 註記 |
| Target | **只 .NET 10**（不雙 target net48），用最新 C# / API（`NativeMemory.AlignedAlloc`、`delegate* unmanaged<>`、`[UnmanagedCallersOnly]`、collection expressions…） | — |
| 其他沿用 | `[MethodImpl(AggressiveInlining/NoInlining)]`、`Optimize=true`、x64、`InterfaceGraphic` 等價的 `IFrameSink`、blargg `$6000` 簽章偵測、handler 用 callback 掛鉤、註解對照 `ref/metalnes-main` 行號 | 見 Part 2 各列 |
| S1 先不做 | SIMD/`Vector<T>`、雙緩衝 framebuffer、獨立 render thread、fps 限制、mapper（只 NROM）、i18n | 留到後面階段 |
