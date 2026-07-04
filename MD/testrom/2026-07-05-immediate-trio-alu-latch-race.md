# Unofficial immediate 三連(ANC/ALR/ARR/LXA)—— ALU 輸入閂鎖賽跑

日期:2026-07-05
狀態:**全部修復** —— micro-ROM 640/640;blargg 三 ROM(v3-02/v5-03/nes-02)
全 Passed。兩個 shim:ALU 輸入閂鎖 hold(修 ANC/ALR/ARR)+ LXA magic=$FF
(修 ATX)。全 141 回歸重驗進行中。

## 失敗現象

三個套件(instr_test-v3 02、instr_test-v5 03、nes_instr_test 02)同樣五行:
`0B/2B AAC`、`4B ASR`、`6B ARR`、`AB ATX`。官方 `AND #imm`($29)與 `AXS`($CB)皆過。

## 顯微鏡工具鏈(全部新沉澱)

1. **micro-ROM + `--micro`**:手工組譯 640 組合(5 op × C∈{0,1} × 8A × 8imm),
   每組 `LDA #p; PHA; PLP; LDX #$5A; LDA #a; OP #imm; PHP; STA; PLA; STA` 存
   A/P 到 $0200+;`--micro` 跑 3 幀後 dump u1.ram。分析器對照文件語意
   (`temp/micro_imm/gen_rom.py` / `analyze.py`)。
2. **`--op-probe <rom> <hexaddr>`**:AB 命中觸發,逐半週期記錄
   db/idl/alua/alub/sb/A/ADD + 13 條資料路控制線(ACSB/SBADD/0ADD/DBADD/
   nDBADD/ANDS/SUMS/SRS/ORS/ADDSB7/ADDSB06/SBAC/SBDB)+ **PLA 列 firing 集合**
   (`WireCore.AllNodeNames()` 枚舉 `op-*` 名字)。

## 病理解剖(ANC #$55, A=$FF, 對照官方 AND)

失敗前的正確部分:
- **PLA union 完全正確**:$0B firing = AND 列 ∪ ASL-A 列(op-T+-ora/and/eor/adc
  + op-T+-asl/rol-a + op-shift 等),與 6502 架構一致
- **bus wired-AND 湧現**:ACSB+SBDB 短接 → SB = A&imm = $55(NMOS 低態勝出,
  netlist 忠實重現!)
- IR[7:5]=000 → 家族解碼選 ORS(ORA)✓ 架構正確
- alua=alub=$55(合併值上到 ALU 兩側)✓

失敗點(execute cycle 的 φ1→φ2 邊界,hc 13):
- `SBADD/DBADD` 關門與 SB/DB 匯流排崩塌(55→FF)發生在**同一個半週期**
- quiescent settle 讓崩塌**穿過正在關的門**:alua/alub 55→FF,ADD 鎖進
  FF|FF=FF(自洽但錯誤的不動點),SBAC 寫回 → A=FF(舊值)
- 對照 AND($29):alub 在 φ2 保持 55 → ADD=FF&55=55 → A=55 ✓(單列解碼下
  崩塌不波及)

## 與 DMC 賽跑的關係:同類、極性相反

| | DMC pcm_latch | ALU 輸入閂鎖 |
|---|---|---|
| 賽跑 | 資料沿 vs 時脈關門沿同半週期 | 匯流排崩塌 vs 選擇線關門同半週期 |
| 真矽晶 | **資料贏**(時脈衰減重疊導通) | **門贏**(hold time 保住 φ1 值) |
| 二值模型 | 門先關 → 少捕捉一拍 | 崩塌穿門 → 閂鎖被汙染 |

→ 每個閂鎖實例的賽跑極性由真實傳播深度決定,不存在全域規則;
修法 = **帶極性的文件化閂鎖 shim 表**。

## 修法:`WireCore.EnableAluLatchShim()`(hold 模式)

每半週期快照 alua/alub;`SBADD`/`DBADD` 下降沿時,若同步值被改,恢復步前
快照(drive→settle→release)= 閂鎖「hold time 滿足」的本意語意。
測試模式限定;benchmark 路徑不受影響。

## 結果

- micro-ROM 640 組合:**344 mismatch → 0**(含旗標:ANC C=b7、ALR C=b0、
  ARR N=Cin/C=b6/V=b6⊕b5 全對 —— 旗標路徑在資料路修好後自然正確)
- **LXA magic 觀測 = $00**(A=X=(A|$00)&imm=A&imm):與常見真機 $EE/$FF 不同;
  分析器以自家讀值推 magic,對 LXA 是循環論證 —— blargg checksum 是作者
  真機錄的,ATX 行的最終判定看 blargg ROM 實跑
- blargg 三 ROM(v3-02 / v5-03 / nes-02)驗證中;之後 instr_test 全家族回歸

## LXA magic shim(2026-07-05 定案)

四次迭代的引擎物理課:
1. 事後強制 A/X/N/Z(單發)→ A/X ✓ 但 P 被沖:**旗標是主動再生迴路**,
   單發 force 一個 cycle 內被復原;且旗標在 operand 週期尾就已更新
2. 延後強制 → 無效(同因)
3. 壓制 ACSB 控制線 → 無效:**LUT 優先序 = 群組內 VCC 導通 > 外部 drive-low**,
   主動驅動的控制線壓不住
4. ✅ **A/X 單發 force + 旗標雙側成對({p1,#566}、{p7,#1045})持續驅動
   3 cycles 再 SetFloat** —— 持續驅動讓迴路所有相位捕捉新態,release 後自持
   (power-up palette 雙側教訓的進階版)

偵測:φ2 fall 狀態機;注意**本 netlist 取樣點上 cpu.sync 超前一拍**
(fall N 的 sync=1 表示 cycle N+1 是 opcode fetch)。

證據:NESdev(magic 隨晶片/溫度而異)、TriCNES 原始碼(magic=$FF +
「supposedly different depending on the CPU's temperature」註解)、blargg
checksum 實錄(其主機 $FF 類,三 ROM 通過所證)。我們裸 netlist 的 $00 =
二值 GND-wins 對比例式匯流排打架的量化;$FF 修正 = NTSC G 版次共識。

## 結果總表

- micro-ROM:344 mismatch → **0/640**(ANC/ALR/ARR/LXA 全對,含旗標)
- blargg:v3-02 / v5-03 / nes-02 全 **Passed**(原本三個全 FAIL)
- 預期計分:129/12 → 132/9(等全 141 回歸確認)

## 待辦

- 全 141 回歸(shim 是全 CPU 域,防誤傷官方指令 —— 三 ROM 本身已是強金絲雀)
- 報告頁卷宗補 LXA magic 條目
- raw-id 別名與 pin 別名的 `#` 前綴衝突(小 id 撞號)—— 之後清理
