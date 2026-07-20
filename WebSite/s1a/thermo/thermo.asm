; ============================================================================
;  thermo.asm  —  NES open-bus "thermometer" test ROM   (NROM / mapper 0)
;
;  A teaching / demonstration ROM for the AprVisual study
;  "Can the NES tell you the room temperature?" (WebSite/s1a/nes-thermometer.html).
;  It reads the PPU open-bus decay time and prints the temperature in Celsius,
;  e.g.  "25.0 DEGREE CELSIUS".
;
;  ---------------------------------------------------------------------------
;  WHY THIS WORKS (the physics)
;  ---------------------------------------------------------------------------
;  The PPU I/O "open bus" latch — the value you read back from the low 5 bits of
;  $2002, or from a write-only PPU register — is not driven by anything once you
;  stop writing to it. It is held only by a tiny parasitic capacitance, and it
;  slowly bleeds to 0 through reverse-bias junction leakage.
;
;  That leakage current is exponential in temperature (Arrhenius):
;         I_leak  ~  exp(-Ea / kT)
;  so the time the latch survives scales the same way:
;         t(T)    =  t0 * exp( Ea/k * (1/T - 1/Tref) )
;  Warmer die -> more leakage -> FASTER decay -> the latch dies sooner.
;
;  So if we PRIME the latch to all-ones and then time how long it takes to fall,
;  that time is a thermometer. We do not have a real clock, so we "time" it by
;  counting how many times a tight polling loop runs before the latch decays.
;  Warmer -> fewer loops (smaller count); colder -> more loops (bigger count).
;
;  ---------------------------------------------------------------------------
;  THE ONE TRICK: read the PPU latch, NOT the CPU bus
;  ---------------------------------------------------------------------------
;  You cannot poll a CPU open-bus address: every instruction fetch re-drives the
;  CPU's external data bus, re-charging that capacitor, so it never decays. The
;  PPU's internal I/O latch is on the other die and the CPU's fetches never touch
;  it — that is why we poll $2002 and mask the low 5 bits.
;
;  ---------------------------------------------------------------------------
;  FROM COUNT TO CELSIUS (done with NO 6502 multiply / divide / float)
;  ---------------------------------------------------------------------------
;  count -> temperature is a non-linear (Arrhenius) inversion. Instead of doing
;  logarithms on the 6502, we precompute the whole curve on the PC (build.py) as
;  a lookup table: thr[i] = the count you would measure at temperature
;  (i*0.1 - 0.05) C, for i = 0..511 (so index i == temperature in TENTHS of a
;  degree, 0.0 .. 51.1 C). The table decreases (cold = big count), so the answer
;  is simply "the largest index i whose threshold is still >= our count" — found
;  with a 9-step power-of-two binary search (no division). Then we format the
;  index as N.N and print " DEGREE CELSIUS".
;
;  Honest limits (this is a technical demo, not a precision instrument):
;   * When the decay is fast (warm), the loop runs few times, so the count is
;     coarse — around 40 C and up, neighbouring degrees can share a count, so the
;     0.1 digit there is not real resolution (~+-0.5 C). Cold end (0..~30 C) is
;     genuinely ~0.1 C.
;   * The table covers 0.0 .. 51.1 C; outside that it clamps.
;   * The count also depends on the emulator's exact loop timing, so the table is
;     calibrated to THIS build (tools/aprnes) — it is a per-model calibration.
; ============================================================================

.MEMORYMAP
DEFAULTSLOT 0
SLOT 0 $C000 $4000
.ENDME
.ROMBANKSIZE $4000
.ROMBANKS 1

.EMPTYFILL $00

; ------------------------------------------------------------------ zero page
.DEFINE cnt0 $10      ; measured decay count, 24-bit little-endian (lo/mid/hi)
.DEFINE cnt1 $11
.DEFINE cnt2 $12
.DEFINE idxL $13      ; binary-search result = temperature in tenths (0..511)
.DEFINE idxH $14
.DEFINE stpL $15      ; search step (256,128,...,1)
.DEFINE stpH $16
.DEFINE tstL $17      ; candidate index = idx + step ; later reused as BCD work
.DEFINE tstH $18
.DEFINE tblL $19      ; thr_*[test] fetched here (24-bit)
.DEFINE tblM $1A
.DEFINE tblH $1B
.DEFINE ptr  $1C      ; 16-bit table pointer ($1C/$1D)
.DEFINE intP $1E      ; integer part of the temperature (0..51)
.DEFINE frac $1F      ; fractional digit (0..9)
.DEFINE tens $20      ; tens digit of the integer part (0..5)
.DEFINE ones $21      ; ones digit of the integer part (0..9)

.BANK 0 SLOT 0
.ORG $0000

; Read-only text, placed first so the code below sees it as a 16-bit address
; (a forward reference would make WLA guess zero-page and fail to fix it).
suffix_str:
    .db " DEGREE CELSIUS", $00

; ============================================================================
;  RESET  —  power-on / init
; ============================================================================
RESET:
    sei
    cld
    ldx #$FF
    txs
    lda #$00
    sta $2000            ; NMI off (we run without interrupts)
    sta $2001            ; rendering off (blank screen while we measure)

    ; the PPU needs ~1 frame to warm up; wait for the first vblank
vwait1:
    bit $2002
    bpl vwait1

    ; clear the 2KB internal RAM ($0000-$07FF)
    lda #$00
    tax
clrram:
    sta $0000,x
    sta $0100,x
    sta $0200,x
    sta $0300,x
    sta $0400,x
    sta $0500,x
    sta $0600,x
    sta $0700,x
    inx
    bne clrram

    ; second warm-up vblank (PPU is now stable enough for $2006/$2007 writes)
vwait2:
    bit $2002
    bpl vwait2

    ; ---- palette: universal background = black, colour 1 = white ----
    lda #$3F
    sta $2006
    lda #$00
    sta $2006
    lda #$0F
    sta $2007            ; $3F00 = black
    lda #$30
    sta $2007            ; $3F01 = white  (our text colour)

    ; ---- clear name table $2000-$23FF to the blank tile ($20 = space) ----
    lda #$20
    sta $2006
    lda #$00
    sta $2006
    lda #$20             ; blank/space tile
    ldx #$04             ; 4 * 256 = 1024 bytes
    ldy #$00
ntclr:
    sta $2007
    dey
    bne ntclr
    dex
    bne ntclr

; ============================================================================
;  MEASURE  —  time the open-bus decay as a loop count
; ============================================================================
    ; Prime the PPU open-bus latch to $1F. Writing ANY PPU register drives the
    ; latch; we use $2003 (OAMADDR) because it has no NMI/rendering side effects.
    lda #$FF
    sta $2003

    lda #$00
    sta cnt0
    sta cnt1
    sta cnt2
measure:
    lda $2002            ; read status; low 5 bits = the decaying open-bus latch
    and #$1F             ; keep only the open-bus bits (mask out vblank/sprite flags)
    cmp #$1F             ; still all-ones?
    bne measured         ; a bit dropped -> the latch has decayed -> stop timing
    inc cnt0             ; else count this iteration (24-bit increment)
    bne measure
    inc cnt1
    bne measure
    inc cnt2
    jmp measure
measured:
    ; cnt2:cnt1:cnt0 now holds the decay time in loop iterations.

; ============================================================================
;  CONVERT  —  count -> temperature index via power-of-two binary search
;
;  The table thr_*[] decreases with index (index 0 = coldest = biggest count).
;  We want the largest index i such that thr[i] >= count. We build that 9-bit
;  index one bit at a time, testing 256,128,...,1 — no division needed.
; ============================================================================
    lda #$00
    sta idxL
    sta idxH
    sta stpL
    lda #$01
    sta stpH             ; step = 256

rx_loop:
    ; test = idx + step
    lda idxL
    clc
    adc stpL
    sta tstL
    lda idxH
    adc stpH
    sta tstH
    ; the table only has 512 entries; if test >= 512, this bit can't be set
    lda tstH
    cmp #$02
    bcs rx_next

    jsr read_thr         ; tblH:tblM:tblL = thr[test]

    ; if thr[test] >= count  -> keep this bit (idx = test)
    ; compute thr - count and look at the final borrow (carry)
    lda tblL
    cmp cnt0
    lda tblM
    sbc cnt1
    lda tblH
    sbc cnt2
    bcc rx_next          ; borrow => thr < count => do not set this bit
    lda tstL
    sta idxL
    lda tstH
    sta idxH
rx_next:
    lsr stpH             ; step >>= 1
    ror stpL
    lda stpH
    ora stpL
    bne rx_loop
    ; idxH:idxL (0..511) == temperature in tenths of a degree C.

; ============================================================================
;  FORMAT  —  split the tenths value into  tens '.' ones '.' frac  digits
;  (all with repeated subtraction; the 6502 has no divide)
; ============================================================================
    ; work = idx (16-bit) ; frac = work mod 10 ; intP = work / 10
    lda idxL
    sta tstL
    lda idxH
    sta tstH
    ldx #$00
div10:
    lda tstL             ; work -= 10 (16-bit)
    sec
    sbc #10
    tay
    lda tstH
    sbc #$00
    bcc div10_done       ; went negative -> quotient complete
    sta tstH
    sty tstL
    inx
    jmp div10
div10_done:
    stx intP             ; intP = idx / 10   (integer degrees, 0..51)
    lda tstL
    sta frac             ; frac = idx mod 10 (the 0.1 digit)

    ; split intP (0..51) into tens and ones
    ldx #$FF
    lda intP
t10:
    inx
    sec
    sbc #10
    bcs t10
    adc #10              ; undo the last (overshoot) subtraction -> ones digit
    sta ones
    stx tens             ; tens (0..5)

; ============================================================================
;  DISPLAY  —  write  "<tens><ones>.<frac> DEGREE CELSIUS"  to the name table
;  Tiles are ASCII-indexed (the CHR font places each glyph at tile == its ASCII
;  code), so we just store ASCII bytes straight into $2007.
; ============================================================================
    lda #$21             ; name table $2000 + row 14 * 32 + col 6 = $21C6
    sta $2006
    lda #$C6
    sta $2006

    lda tens             ; leading-zero blanking: show a space instead of '0'
    bne show_tens
    lda #$20             ; ' '
    jmp put_tens
show_tens:
    ora #$30             ; '0' + tens
put_tens:
    sta $2007
    lda ones
    ora #$30             ; '0' + ones
    sta $2007
    lda #$2E             ; '.'
    sta $2007
    lda frac
    ora #$30             ; '0' + frac
    sta $2007

    lda #<suffix_str     ; then the literal " DEGREE CELSIUS" (via a zp pointer,
    sta ptr              ; which avoids absolute,X addressing-mode ambiguity)
    lda #>suffix_str
    sta ptr+1
    ldy #$00
suffix:
    lda (ptr),y
    beq suffix_done
    sta $2007
    iny
    bne suffix
suffix_done:

; ============================================================================
;  RENDER  —  reset scroll and turn the picture on; then idle forever
; ============================================================================
    bit $2002            ; reset the $2005/$2006 write toggle
    lda #$00
    sta $2005
    sta $2005
    sta $2000            ; NMI off, BG uses pattern table 0 (where our font lives)
    lda #$0A
    sta $2001            ; show background
forever:
    jmp forever

; ----------------------------------------------------------------------------
;  read_thr:  load the 24-bit table entry thr[test] into tblH:tblM:tblL.
;  The three SoA arrays are contiguous (thr_lo, thr_mid, thr_hi; 512 bytes each),
;  so thr_mid = thr_lo + 512 and thr_hi = thr_lo + 1024: after pointing at
;  thr_lo+test we just add 512 (two to the high byte) to walk to the next array.
; ----------------------------------------------------------------------------
read_thr:
    lda #<thr_lo
    clc
    adc tstL
    sta ptr
    lda #>thr_lo
    adc tstH
    sta ptr+1
    ldy #$00
    lda (ptr),y          ; thr_lo[test]
    sta tblL
    lda ptr+1            ; += 512  -> thr_mid[test]
    clc
    adc #$02
    sta ptr+1
    lda (ptr),y
    sta tblM
    lda ptr+1            ; += 512  -> thr_hi[test]
    clc
    adc #$02
    sta ptr+1
    lda (ptr),y
    sta tblH
    rts

nmi_isr:
irq_isr:
    rti

; the 512x3 count->temperature lookup table (generated by build.py)
.INCLUDE "thermo_table.inc"

; ---- CPU vectors at $FFFA/$FFFC/$FFFE ----
.ORG $3FFA
.dw nmi_isr
.dw RESET
.dw irq_isr
