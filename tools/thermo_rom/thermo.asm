; ============================================================================
; thermo.asm  —  NES open-bus "thermometer" test ROM  (NROM / mapper 0)
;
; Measures the PPU open-bus decay time and shows it on screen as a 6-digit hex
; number. The decay time is set by AprNes's temperature knob (--openbus-temp),
; so the displayed number scales with temperature:
;   warmer -> faster decay -> smaller count ; colder -> slower -> bigger count.
;
; Method (matches WebSite/s1a/nes-thermometer.html):
;   1. prime the PPU open-bus latch to $1F (write $FF to $2003 / OAMADDR — drives
;      all 8 latch bits without enabling NMI or rendering)
;   2. tight-poll $2002, keep only the low 5 open-bus bits, count iterations until
;      the first bit drops (a 24-bit counter in zero page $10..$12)
;   3. render the count as hex and also leave it at $0010 for --dump-mem
;
; The low 5 bits of $2002 are the decaying latch; a $2002 read does NOT refresh
; them (only the CPU external bus self-refreshes on fetch), so polling is valid.
; ============================================================================

.MEMORYMAP
DEFAULTSLOT 0
SLOT 0 $C000 $4000
.ENDME
.ROMBANKSIZE $4000
.ROMBANKS 1

.EMPTYFILL $00

; zero-page 24-bit decay counter (little-endian)
.DEFINE cnt0 $10
.DEFINE cnt1 $11
.DEFINE cnt2 $12

.BANK 0 SLOT 0
.ORG $0000

RESET:
    sei
    cld
    ldx #$FF
    txs
    lda #$00
    sta $2000            ; NMI off
    sta $2001            ; rendering off

    ; wait for the first PPU warm-up vblank
vwait1:
    bit $2002
    bpl vwait1

    ; clear the 2KB internal RAM
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

    ; second warm-up vblank
vwait2:
    bit $2002
    bpl vwait2

    ; ---- palette: bg=black, color1=white ----
    lda #$3F
    sta $2006
    lda #$00
    sta $2006
    lda #$0F
    sta $2007            ; universal background = black
    lda #$30
    sta $2007            ; color 1 = white

    ; ---- clear nametable $2000-$23FF with the blank tile ($00) ----
    lda #$20
    sta $2006
    lda #$00
    sta $2006
    lda #$00
    ldx #$04             ; 4 * 256 = 1024 bytes ($2000-$23FF)
    ldy #$00
ntclr:
    sta $2007
    dey
    bne ntclr
    dex
    bne ntclr

    ; ==== measure open-bus decay ====
    lda #$FF
    sta $2003            ; prime the open-bus latch to $1F (low 5 bits)
    lda #$00
    sta cnt0
    sta cnt1
    sta cnt2
meas:
    lda $2002
    and #$1F
    cmp #$1F
    bne measdone         ; first bit dropped -> stop
    inc cnt0
    bne meas
    inc cnt1
    bne meas
    inc cnt2
    jmp meas
measdone:

    ; ==== display: 6 hex digits at row 14, col 13  ($2000 + 14*32 + 13 = $21CD) ====
    lda #$21
    sta $2006
    lda #$CD
    sta $2006
    lda cnt2
    jsr puthex
    lda cnt1
    jsr puthex
    lda cnt0
    jsr puthex

    ; ==== enable rendering ====
    bit $2002            ; reset the $2005/$2006 write toggle
    lda #$00
    sta $2005
    sta $2005
    sta $2000            ; NMI off, BG pattern table 0
    lda #$0A
    sta $2001            ; show background
forever:
    jmp forever

; puthex: A = byte -> write two hex-digit tiles to $2007.
; hex-digit glyph for nibble N lives at tile index $30 + N.
puthex:
    pha
    lsr a
    lsr a
    lsr a
    lsr a
    clc
    adc #$30
    sta $2007
    pla
    and #$0F
    clc
    adc #$30
    sta $2007
    rts

nmi_isr:
irq_isr:
    rti

.ORG $3FFA
.dw nmi_isr
.dw RESET
.dw irq_isr
