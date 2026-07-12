# Run 4 屍檢:141 顆全數執行;收尾 frame-IRQ 競速 → $0600 BRK 風暴

> 日期:2026-07-13。接續 `2026-07-12-*-internal-data-bus-analysis-and-handoff.md`。
> 狀態:**test 141 通過(歷史障礙解除);新阻斷者 = 收尾中斷競速,已完整定位,未修。**

## 1. 一句話

**Run 4(telemetry+快照全開)史上第一次跑完全部 141 顆測試 —— `$2007 Stress` 和
`Internal Data Bus` 都通過 —— 但收尾階段 APU frame IRQ 在向量已指向死 RAM 時合法升起,
CPU 掉進 `$0600` 的 BRK 風暴,完成區塊永遠沒寫。Run 3 的「卡在 test 141」是誤診,
實為同一個風暴(當時無 telemetry,畫面停在最後一顆測試名)。**

## 2. 證據鏈(全部可從 `tools/testrom/out/ac_run4_brkstorm/` 重現)

| Frame | 事件 | 證據 |
|---:|---|---|
| ≤4785 | `frm_intmode=1`(IRQ 抑制中),flag=0 | resume f4770 + `--watch` |
| 4780-4785 | 測試 139/140 完成(PC 在 `WaitForVBlank` $F916-19) | 每幀 telemetry |
| **4786** | **`frm_intmode → 0`**:IDB 的 DMA-sync 寫 `$4017` 重置序列器(PC=$FD81 = `CSWaste94Cy`,CycleSync 燒週期家族) | resume f4780 + `--progress-frames 1` |
| 4786→4787 | IDB 完成,`$490=$01`(**PASS**),results=141 | 快照 diff f4780/f4790 |
| **4787** | **`cpu./frame_irq → 0`(升起,永不 ACK)**;`/pcm_irq` 全程=1(DMC 無罪) | resume f4780 + `--watch` |
| 4787+ | PC 進 `$0600-$0602`,AB 敲 `$FFFE`,堆疊循環 `02 06 35`(B=1 → BRK) | telemetry + 快照 RAM |
| →6070+ | BRK 風暴 1,280+ 幀,`$07F0` 完成區塊全零 | 使用者發現超過 6000 幀 |

關鍵 ASM 事實:
- `AccuracyCoin.asm:391`:「**$600 is dedicated to the IRQ routine**」,`$FFFE` 指向 RAM `$0600`
- `:2623/2659`:`$2007 Stress` 取樣資料溢出時**改存 `$600`**(覆蓋 IRQ 常式是 ROM 既定行為)
- `:2994`:ALE+Read 也用 `$600` 當資料區
- ROM 假設「之後不會再有 IRQ」—— 真機以數百週期之差贏得「收尾保護 vs 旗標升起」競速

## 3. S1 為什麼輸掉競速

`$4017` 寫入(4786)→ 4-step 序列走完(~1 幀)→ flag 升起(4787)。這段是合法硬體行為。
差異在:真機上收尾程式碼(SEI / 再抑制 / `$4015` 讀清)趕在旗標升起前完成;S1 的
**DMC/CycleSync 週期時序有微小偏差**(與 `$0A` 六連星同一家族 —— 那六顆測試量的就是
DMA 偷週期數),導致收尾保護輸給旗標。

**修法方向(未定)**:
- (a) 根修 DMC DMA 偷週期時序(同時可能修好 `$0A` 家族與這個競速)—— 先做 --joypad
  實驗排除儀器問題後,剩餘的時序偏差才是真目標
- (b) 文件化 shim:收尾窗口的 frame-IRQ 競速仲裁(不建議先做 —— 治標)
- ⚠️ 不要動 `FrameIrqShim` —— 本案已證明它無罪(旗標升起是合法的,不是 stale hold)

## 4. Run 4 最終計分(f4790 快照,對 oracle temp/ac/t200.txt)

- **132/141 與 oracle 一致**(含 `2007_Stress=$01`、`InternalDataBus=$01`、LXA、DMC 通道、
  DMCDMAPlusOAMDMA、PPUOpenBus 等)
- SHA_93/9F、SHS_9B:`$05` vs `$09` —— **皆為 PASS**(不同可接受變體;編碼 = 奇數即過)
- 真偏差 9 顆:
  - `$0A` 六連星(= 各自錯誤 2):ControllerStrobing/Clocking、APURegActivation、
    DMABusConflict、Implicit/ExplicitDMAAbort → 行為層手把未掛(Task #1/#3)
  - OpenBus `$12`(錯 4:PC 移到 open bus 執行) → Task #2
  - LAE `$0E`(錯 3:類比匯流排合併家族) → Task #2
  - **ALERead `$0A`(錯 2:「精準時機的 $2007 讀取應能干擾 PPU 位址匯流排」)← 新發現**,
    ALE 類比家族;注意 isolated 驗收時它曾過,正式全套下 FAIL —— 狀態相依

## 5. 工具鏈紀錄(這次全部兌現)

- 結果 byte 編碼:**FAIL = `(錯誤碼<<2)|2`,PASS = 奇數(變體<<2|1)**(`TEST_Fail` @ asm:3524)
- `ac_snap_results.py`:從快照 MEMS 讀 `u1.ram` 的 `$0400-$04FF` 計分板,對 oracle diff
- `--resume` + `--watch`(a1e797f 起 test mode 可用):準位觸發的 IRQ 永不 ACK →
  幀粒度採樣就能指認兇手;三次 resume 探測各花 2-4 分鐘
- `--resume` + `--progress-frames 1`:每幀 PC → 對 `.fns` 符號表定位到副程式
- 快照庫:`ac_run4_brkstorm/snaps/` 607 檔,任何點 2 秒直達

## 6. 下一步(依序)

1. Task #1:--joypad 聚焦實驗(六連星確認)
2. Task #3:--joypad 掛牌跑 —— **注意:收尾風暴大概率仍在**(joypad 不影響 CycleSync 競速),
   所以掛牌跑可能「141 顆全有結果但無完成區塊」;runner 可考慮在 resultsFilled=141 且
   PC 進入 $0600 BRK 特徵時判定「suite 完成(結果表完整)+ epilogue-storm 註記」——
   或先修 (a) 再跑。**這是使用者要拍板的取捨。**
3. Task #2/#6:OpenBus、LAE、ALERead、時序根修
