# test_ppu_read_buffer #67 診斷(TEST_SPHIT_DMA_PPU_BUS)

> #67:`$4014 <- $20` 觸發 OAM DMA,source = PPU 暫存器空間($2000-$20FF,
> 即 $2000-$2007 鏡像 32 次),讀進 OAM 後做 sprite-0-hit。四個失敗候選:
> DMA 讀取不正確 / PPU bus 未保持最後傳輸值 / $2002 讀值不符 / $2004 讀改 OAM。

## 診斷結論:DMA 讀 PPU 暫存器的 open-bus 取樣時序錯位(差一)

用 wla-dx 組的 micro-ROM(`temp/readbuf/`)+ 引擎 --micro 物理 OAM cell dump
(`ppu.oam_ram_XX_bN`,b side = 真值;a side = /值反相;attribute byte
index≡2 mod4 只有 5 bit,bits 2-4 無 cell → $8A 存成 $82,**這是真 2C02 行為
非損毀**)三案對照:

| source | 開 bus=$0A 下 OAM[0..3] | 正確? |
|---|---|---|
| **CPU 直接讀** $2000-$2003 | `0A 0A 8A 8A` | ✓ 完美(open-bus 完全正確)|
| **DMA 從 RAM**(填 0,1,2,3…)| `00 01 02 03` | ✓ 完美(DMA 位址/寫入無誤)|
| **DMA 從暫存器空間** | `0A 8A 82 00` | ✗ **差一位移** |

**關鍵:直接讀 OK + DMA-from-RAM OK,只有 DMA-from-registers 錯。**
錯法 = 暫存器讀值**整體位移一格**:OAM[0] 拿到 $2001 的值、OAM[1] 拿到
$2002($8A)、OAM[2] 拿到 $2003($8A→attr-mask $82)、OAM[3] 拿到 $2004($00)。
即 `OAM[i] = register-read[i+1]`。

## 為什麼是 open-bus 取樣時序(非 DMA 位址 bug)

- DMA-from-RAM 無位移(source[i]→OAM[i] 正確)→ DMA 位址產生器與寫入**正確**。
  若是通用 DMA 差一,所有 sprite DMA 都會壞、大量測試崩(實際大多過)。
- 差一只在讀「動態 PPU 暫存器」(open-bus 只在讀取當下被驅動)時出現 →
  問題在 **rapid DMA 讀取下 open-bus 值的取樣相位**與直接讀不同,錯位一格。
- 同 double_2007 / even_odd 的「快速存取同波時序」硬類別。

## 排除的候選

- 「PPU bus 未保持最後傳輸值」:直接讀 `0A 0A 8A 8A` 證明 open-bus 保持**完全正確**。
- 「$2002 讀值不符」:直接讀 $2002=$8A 正確。
- 「$2004 讀改 OAM」:尚未單獨證實;但主症狀是位移,先攻位移。
- OAMADDR 寫入損毀(RP2C02G bug):是真實忠實行為,但被 DMA 覆寫、不影響
  #67 最終 OAM;**注意**我最初的 readback(重複 STA $2003)誤觸它 → 假象
  `0A 8A 82 00`,與 DMA 真結果**巧合相同**(都因位移),但成因不同。

## 工具(可重用)

- `tools/wla-dx/wla-6502.exe` + `wlalink.exe`:正式組譯(取代易錯手組)。
- `temp/readbuf/`:d67d(直接讀)/ d67v(DMA-from-RAM 驗證)/ d67dma(DMA-from-reg)
  + build.sh;NMI handler 內做事(VBL set & unread);**必須先 warmup 等 2 個
  VBL 再寫 $2000**(暖機期 $2000 寫入被忽略,否則 NMI 不觸發)。
- 引擎:`--micro-frames N`(預設 3,NMI 要 ≥~10);MicroDump 末尾物理 OAM dump
  (OAM00/OAM10 兩行,b-side)。
- `--pin 0`:sweep 佔 2-14 時 core 0 空,micro 跑這裡不撞。

## 下一步

trace DMA 期間 cpu.ab / rw / ppu io_db 逐 hc,定位 open-bus 取樣相位差一的
確切機制 → 決定 shim(測試模式,類 double_2007 的儀器級手法)。
金絲雀:所有 sprite DMA 測試(sprite_hit/dma_ram/dma_rom)、oam_read。
