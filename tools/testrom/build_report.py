#!/usr/bin/env python3
"""Build WebSite/Report/ from tools/testrom/out/ results.

Merges out/results/*.json with the catalog (pending tests show as such), copies
screenshots, writes results.json (AprNes-compatible fields + S1 extras) and a
self-contained index.html. Run directly or via run_tests.py.
"""
import json, os, shutil, time

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO       = os.path.abspath(os.path.join(SCRIPT_DIR, "..", ".."))
OUT_DIR    = os.path.join(SCRIPT_DIR, "out")
CATALOG    = os.path.join(SCRIPT_DIR, "catalog.json")
REPORT_DIR = os.path.join(REPO, "WebSite", "Report")

# Originals stay PNG under out/screenshots/; the site gets lossless WebP (~70% smaller).
try:
    from PIL import Image
    HAVE_PIL = True
except ImportError:
    HAVE_PIL = False
    print("(PIL not available — site screenshots will be PNG copies)")

def key_of(t):
    return f"{t['suite']}/{t['rom']}".replace("/", "__")

cat = json.load(open(CATALOG, encoding="utf-8"))
rows, engine_ver = [], ""
counts = {"pass": 0, "fail": 0, "timeout": 0, "pending": 0}

# screenshots/ is a pure build artifact — wipe and regenerate from out/ (originals stay there as PNG)
shot_root = os.path.join(REPORT_DIR, "screenshots")
if os.path.isdir(shot_root):
    shutil.rmtree(shot_root)
os.makedirs(shot_root, exist_ok=True)

site_ext = ".webp" if HAVE_PIL else ".png"
for t in cat["tests"]:
    k = key_of(t)
    jpath = os.path.join(OUT_DIR, "results", k + ".json")
    shot_src = os.path.join(OUT_DIR, "screenshots", t["suite"].replace("/", os.sep), t["rom"].replace(".nes", ".png"))
    shot_rel = f"screenshots/{t['suite']}/{t['rom'].replace('.nes', site_ext)}"

    row = {"suite": t["suite"], "rom": t["rom"], "class": t["class"],
           "status": "pending", "exit_code": -1, "result_text": "", "screenshot": "",
           "detection": "", "frames": 0, "simSeconds": 0, "wallSeconds": 0, "resetCount": 0, "khcPerSec": 0}
    if os.path.isfile(jpath):
        try:
            r = json.load(open(jpath, encoding="utf-8"))
            row.update(status=r.get("status", "pending"),
                       exit_code=0 if r.get("status") == "pass" else r.get("resultCode", -1),
                       result_text=r.get("resultText", ""),
                       detection=r.get("detection", ""),
                       frames=r.get("frames", 0),
                       simSeconds=r.get("simSeconds", 0),
                       wallSeconds=r.get("wallSeconds", 0),
                       resetCount=r.get("resetCount", 0),
                       khcPerSec=round(r.get("halfCycles", 0) / r.get("wallSeconds", 1) / 1000, 1) if r.get("wallSeconds", 0) > 0 else 0)
            engine_ver = r.get("engineVersion", engine_ver) or engine_ver
        except Exception as e:
            row["result_text"] = f"(result json unreadable: {e})"
    if os.path.isfile(shot_src):
        dst = os.path.join(REPORT_DIR, "screenshots", t["suite"].replace("/", os.sep), t["rom"].replace(".nes", site_ext))
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        if HAVE_PIL:
            Image.open(shot_src).save(dst, "WEBP", lossless=True, method=6)
        else:
            shutil.copyfile(shot_src, dst)
        row["screenshot"] = shot_rel
    counts[row["status"] if row["status"] in counts else "pending"] += 1
    rows.append(row)

with open(os.path.join(REPORT_DIR, "results.json"), "w", encoding="utf-8") as fp:
    json.dump(rows, fp, indent=1, ensure_ascii=False)

done = counts["pass"] + counts["fail"] + counts["timeout"]
build_date = time.strftime("%Y-%m-%d %H:%M:%S")

html = """<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>AprVisual.S1 Switch-Level Test Report</title>
<style>
*{box-sizing:border-box;margin:0;padding:0}
:root{--bg:#0f172a;--surface:#1e293b;--surface2:#283548;--border:#334155;--text:#e2e8f0;--dim:#94a3b8;
--pass:#22c55e;--pass-bg:rgba(34,197,94,.15);--fail:#ef4444;--fail-bg:rgba(239,68,68,.15);
--to:#eab308;--to-bg:rgba(234,179,8,.15);--pend:#64748b;--pend-bg:rgba(100,116,139,.18);--accent:#3b82f6}
body{background:var(--bg);color:var(--text);font-family:-apple-system,'Segoe UI',system-ui,sans-serif;line-height:1.5;padding:1.5rem}
a{color:var(--accent);text-decoration:none}
.header{text-align:center;margin-bottom:1.2rem}
.header h1{font-size:1.7rem;letter-spacing:-.02em}
.header .sub{color:var(--dim);font-size:.85rem;margin-top:.3rem}
.stats{display:flex;justify-content:center;gap:2.2rem;margin:1.2rem 0;flex-wrap:wrap}
.stat{text-align:center}.stat .num{font-size:2rem;font-weight:700}
.stat .lbl{font-size:.72rem;color:var(--dim);text-transform:uppercase;letter-spacing:.05em}
.stat.pass .num{color:var(--pass)}.stat.fail .num{color:var(--fail)}.stat.timeout .num{color:var(--to)}.stat.pending .num{color:var(--pend)}
.progress-wrap{max-width:600px;margin:.6rem auto}
.progress-bar{width:100%;height:12px;background:var(--surface);border-radius:6px;overflow:hidden;display:flex}
.progress-bar .p{background:var(--pass)}.progress-bar .f{background:var(--fail)}.progress-bar .t{background:var(--to)}
.progress-text{text-align:center;font-size:.82rem;color:var(--dim);margin-top:.35rem}
.note{margin:1rem auto;max-width:860px;padding:.7rem 1rem;background:#1a2332;border:1px solid #2d4a6f;border-radius:8px;font-size:.83rem;color:#8cb4e0}
.controls{display:flex;gap:.7rem;justify-content:center;flex-wrap:wrap;margin:1.2rem 0;align-items:center}
.btn-group{display:flex;border-radius:8px;overflow:hidden;border:1px solid var(--border)}
.btn-group button{padding:.42rem .85rem;border:none;background:var(--surface);color:var(--text);cursor:pointer;font-size:.8rem}
.btn-group button:hover{background:var(--surface2)}.btn-group button.active{background:var(--accent);color:#fff}
select,input[type=text]{padding:.42rem .85rem;border:1px solid var(--border);border-radius:8px;background:var(--surface);color:var(--text);font-size:.8rem;outline:none}
.suite-section{margin-bottom:1.1rem}
.suite-header{display:flex;align-items:center;gap:.7rem;padding:.6rem 1rem;background:var(--surface);border:1px solid var(--border);border-radius:8px;cursor:pointer;user-select:none;margin-bottom:.6rem}
.suite-header:hover{background:var(--surface2)}
.suite-header .arrow{transition:transform .2s;font-size:.72rem;color:var(--dim)}
.suite-header .arrow.collapsed{transform:rotate(-90deg)}
.suite-header .name{font-weight:600;flex:1}
.badge{font-size:.7rem;padding:.13rem .42rem;border-radius:4px;font-weight:600}
.badge.pass{background:var(--pass-bg);color:var(--pass)}.badge.fail{background:var(--fail-bg);color:var(--fail)}
.badge.timeout{background:var(--to-bg);color:var(--to)}.badge.pending{background:var(--pend-bg);color:var(--pend)}
.badge.cls{background:rgba(59,130,246,.15);color:var(--accent)}
.badge.det{background:rgba(148,163,184,.15);color:var(--dim)}
.card-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(225px,1fr));gap:.8rem}
.card-grid.hidden{display:none}
.card{background:var(--surface);border:1px solid var(--border);border-radius:8px;overflow:hidden}
.card:hover{box-shadow:0 4px 14px rgba(0,0,0,.35)}
.card .thumb{width:100%;aspect-ratio:256/240;background:#000;cursor:pointer;position:relative;overflow:hidden}
.card .thumb img{width:100%;height:100%;object-fit:contain;image-rendering:pixelated;display:block}
.card .thumb .no-img{position:absolute;inset:0;display:flex;align-items:center;justify-content:center;color:var(--dim);font-size:.72rem}
.card .info{padding:.6rem .7rem}
.card .rom-name{font-weight:600;font-size:.84rem;margin-top:.32rem;word-break:break-word}
.card .suite-name{font-size:.7rem;color:var(--dim);margin-top:.08rem}
.card .meta{font-size:.68rem;color:var(--dim);margin-top:.25rem}
.card .expand-btn{font-size:.7rem;color:var(--accent);cursor:pointer;border:none;background:none;padding:.18rem 0;margin-top:.3rem}
.card .result-text{font-size:.66rem;font-family:Consolas,monospace;color:var(--dim);white-space:pre-wrap;word-break:break-word;max-height:0;overflow:hidden;transition:max-height .25s}
.card .result-text.expanded{max-height:300px;overflow-y:auto;margin-top:.3rem;padding:.4rem;background:var(--bg);border-radius:4px}
.modal-overlay{display:none;position:fixed;inset:0;background:rgba(0,0,0,.88);z-index:1000;justify-content:center;align-items:center;flex-direction:column;cursor:pointer}
.modal-overlay.active{display:flex}
.modal-overlay img{max-width:min(90vw,768px);max-height:85vh;image-rendering:pixelated;border:2px solid var(--border);border-radius:4px}
.modal-overlay .modal-title{margin-top:1rem;background:var(--surface);padding:.4rem 1rem;border-radius:8px;font-size:.85rem;border:1px solid var(--border)}
.empty-msg{text-align:center;color:var(--dim);padding:3rem}
.en,.zh{display:none}
body.lang-en .en,body.lang-zh .zh{display:revert}
.lang-sw{position:absolute;top:1rem;right:1.2rem;display:flex;border-radius:8px;overflow:hidden;border:1px solid var(--border)}
.lang-sw button{padding:.3rem .7rem;border:none;background:var(--surface);color:var(--text);cursor:pointer;font-size:.78rem}
.lang-sw button.active{background:var(--accent);color:#fff}
.footer{text-align:center;color:var(--dim);font-size:.73rem;margin-top:2rem;padding-top:1rem;border-top:1px solid var(--border)}
</style>
</head>
<body class="lang-en">
<div class="lang-sw"><button id="btn-en" class="active" onclick="setLang('en')">English</button><button id="btn-zh" onclick="setLang('zh')">中文</button></div>
<div class="header">
  <h1>AprVisual.S1 Switch-Level Test Report</h1>
  <div class="sub"><span class="en">transistor/switch-level NES simulation (Visual2A03 + Visual2C02 netlists) &mdash; every result below is computed by
  propagating individual transistor state changes, not by a behavioral emulator.</span><span class="zh">電晶體/開關級 NES 模擬(Visual2A03 + Visual2C02 netlist)—— 以下每一筆結果都是逐一傳播電晶體狀態變化算出來的,不是行為層模擬器。</span></div>
  <div class="sub" id="build-info"></div>
</div>
<div class="stats">
  <div class="stat pass"><div class="num" id="n-pass">0</div><div class="lbl"><span class="en">Passed</span><span class="zh">通過</span></div></div>
  <div class="stat fail"><div class="num" id="n-fail">0</div><div class="lbl"><span class="en">Failed</span><span class="zh">失敗</span></div></div>
  <div class="stat timeout"><div class="num" id="n-to">0</div><div class="lbl"><span class="en">Timeout</span><span class="zh">逾時</span></div></div>
  <div class="stat pending"><div class="num" id="n-pend">0</div><div class="lbl"><span class="en">Pending</span><span class="zh">待跑</span></div></div>
  <div class="stat"><div class="num" id="n-total">0</div><div class="lbl"><span class="en">Total</span><span class="zh">總數</span></div></div>
</div>
<div class="progress-wrap">
  <div class="progress-bar"><div class="p" id="bar-p"></div><div class="f" id="bar-f"></div><div class="t" id="bar-t"></div></div>
  <div class="progress-text" id="progress-text"></div>
</div>
<div class="note"><span class="en"><strong>Scope:</strong> 141 NROM/CNROM NTSC test ROMs. Detection runs once per simulated frame
 (a single test takes minutes-to-an-hour of wall time at switch level); the class badge on each card says how
 the verdict is detected:</span><span class="zh"><strong>範圍:</strong>141 個 NROM/CNROM NTSC 測試 ROM。判定每模擬幀執行一次
 (開關級下單一測試需數分鐘到一小時的牆鐘時間);每張卡片上的類別徽章標示其判定方式:</span>
 <div style="margin-top:.5rem;display:grid;grid-template-columns:auto 1fr;gap:.25rem .7rem;font-size:.8rem">
   <span class="badge cls" style="justify-self:start">A</span>
   <span><span class="en">blargg <code>$6000</code> protocol — the ROM writes its result to <code>$6000</code>
    (0 = pass, else fail code); the engine reads one byte per frame and stops the moment the result appears.</span><span class="zh">blargg <code>$6000</code> 協定 —— ROM 把結果寫到 <code>$6000</code>(0=通過,其他=失敗碼);引擎每幀讀一個 byte,結果一出現立即停止。</span></span>
   <span class="badge cls" style="justify-self:start">A-r</span>
   <span><span class="en">same <code>$6000</code> protocol, <strong>plus the ROM requests soft resets</strong>: status
    <code>$6000=$81</code> asks the runner to press Reset — the engine waits 6 frames, pulses the console's
    res line for 192 half-cycles (<code>WireCore.SoftReset</code>), and the test continues across the reset
    (up to 10 times). Used by the apu_reset / cpu_reset suites, which verify post-reset hardware state.</span><span class="zh">同樣的 <code>$6000</code> 協定,<strong>但 ROM 會主動要求軟重設</strong>:狀態 <code>$6000=$81</code> 表示「請按 Reset」—— 引擎等 6 幀後把主機 res 線拉 192 個半週期(<code>WireCore.SoftReset</code>),測試跨越重設繼續(最多 10 次)。apu_reset / cpu_reset 套件用它驗證重設後的硬體狀態。</span></span>
   <span class="badge cls" style="justify-self:start">B</span>
   <span><span class="en">screen text — no <code>$6000</code>; the engine decodes nametable 0 every frame (blargg CHR maps
    tile&nbsp;=&nbsp;ASCII) and stops at a terminal <code>Passed</code>/<code>Failed</code>/<code>$0X</code> marker
    (2-frame confirm; no 90-frame stability wait).</span><span class="zh">畫面文字 —— 無 <code>$6000</code>;引擎每幀解碼 nametable 0(blargg 的 CHR 對映 tile=ASCII),遇到終端 <code>Passed</code>/<code>Failed</code>/<code>$0X</code> 標記即停(連續 2 幀確認;不等 90 幀穩定)。</span></span>
   <span class="badge cls" style="justify-self:start">C</span>
   <span><span class="en">on-screen CRC — the ROM prints a CRC32; it is compared against the per-console accept set
    (dmc_dma visual tests).</span><span class="zh">畫面 CRC —— ROM 印出 CRC32,與依機種而異的合法集合比對(dmc_dma 視覺測試)。</span></span>
 </div>
 <div style="margin-top:.5rem"><span class="en">apu_mixer's $6000 pass only certifies sequence completion (its real verdict is
 auditory) — treat those 4 as smoke tests.</span><span class="zh">apu_mixer 的 $6000 通過只代表「音頻序列播完沒當機」(真正的判定是聽覺的)—— 那 4 個請視為 smoke test。</span></div>
 <details style="margin-top:.6rem">
  <summary style="cursor:pointer;color:#5dadec"><strong><span class="en">Hardware model — what is netlist, what is behavioral</span><span class="zh">硬體模型 —— 哪些是 netlist、哪些是行為層</span></strong></summary>
  <div style="margin-top:.5rem;overflow-x:auto"><table style="border-collapse:collapse;font-size:.78rem;min-width:640px">
   <tr style="color:#5dadec;text-align:left"><th style="padding:.15rem .6rem .15rem 0"><span class="en">Part</span><span class="zh">部件</span></th><th style="padding:.15rem .6rem"><span class="en">Device</span><span class="zh">器件</span></th><th style="padding:.15rem .6rem"><span class="en">Simulation level</span><span class="zh">模擬層級</span></th></tr>
   <tr><td style="padding:.15rem .6rem .15rem 0">CPU</td><td style="padding:.15rem .6rem">Ricoh <strong>RP2A03G</strong> (NTSC)</td>
    <td style="padding:.15rem .6rem"><span class="en"><strong>Transistor netlist</strong> — Quietust's Visual2A03 die tracing: the whole die
     (6502 core with BCD disabled, APU, OAM-DMA, controller I/O)</span><span class="zh"><strong>電晶體 netlist</strong> —— Quietust 的 Visual2A03 晶粒描繪:整顆晶片(BCD 停用的 6502 核心、APU、OAM-DMA、手把 I/O)</span></td></tr>
   <tr><td style="padding:.15rem .6rem .15rem 0">PPU</td><td style="padding:.15rem .6rem">Ricoh <strong>RP2C02G</strong> (NTSC)</td>
    <td style="padding:.15rem .6rem"><span class="en"><strong>Transistor netlist</strong> — Quietust's Visual2C02 die tracing,
     <em>including palette RAM and OAM as physical storage cells</em> (not hoisted to handlers)</span><span class="zh"><strong>電晶體 netlist</strong> —— Quietust 的 Visual2C02 晶粒描繪,<em>palette RAM 與 OAM 保持為物理儲存 cell</em>(未抽成 handler)</span></td></tr>
   <tr><td style="padding:.15rem .6rem .15rem 0">Board glue</td><td style="padding:.15rem .6rem">NES-001: 74LS373 (PPU AD-bus latch),
     74LS139 (address decoder), 2&times;74LS368, 74HC04, CIC, controller ports</td>
    <td style="padding:.15rem .6rem"><span class="en"><strong>Gate-level transistor modules</strong> (hand-authored netlist defs, MetalNES lineage)</span><span class="zh"><strong>閘級電晶體模組</strong>(手寫 netlist 定義,MetalNES 血緣)</span></td></tr>
   <tr><td style="padding:.15rem .6rem .15rem 0">Memories</td><td style="padding:.15rem .6rem">2 KB CPU RAM (u1), 2 KB CIRAM (u4),
     cart PRG/CHR ROM, 8 KB cart WRAM (test ROMs)</td>
    <td style="padding:.15rem .6rem"><span class="en"><strong>Behavioral byte arrays</strong> behind <em>physical tri-state pass-gates</em>
     (chip-select wiring is netlist, so open-bus hold emerges physically; missing: charge decay, access time)</span><span class="zh"><strong>行為層位元組陣列</strong>,接在<em>物理三態 pass-gate</em> 後面(晶片選取接線是 netlist,open-bus 保持是物理湧現;缺:電荷衰減、存取時間)</span></td></tr>
   <tr><td style="padding:.15rem .6rem .15rem 0">Mapper</td><td style="padding:.15rem .6rem">NROM / CNROM</td>
    <td style="padding:.15rem .6rem"><span class="en">NROM = pure wiring; CNROM = <strong>behavioral</strong> CHR bank latch on the PRG bus</span><span class="zh">NROM = 純接線;CNROM = PRG 匯流排上的<strong>行為層</strong> CHR bank latch</span></td></tr>
   <tr><td style="padding:.15rem .6rem .15rem 0">Clock</td><td style="padding:.15rem .6rem">21.477 MHz master</td>
    <td style="padding:.15rem .6rem"><span class="en"><strong>Behavioral</strong> half-cycle toggle; the &divide;12 CPU / &divide;4 PPU dividers are inside the dies</span><span class="zh"><strong>行為層</strong>半週期翻轉;&divide;12 CPU / &divide;4 PPU 除頻器在晶粒內</span></td></tr>
   <tr><td style="padding:.15rem .6rem .15rem 0">Video out</td><td style="padding:.15rem .6rem">framebuffer</td>
    <td style="padding:.15rem .6rem"><span class="en">Measurement tap: palette-RAM cells read at each pclk1 edge (does not affect the sim)</span><span class="zh">量測 tap:每個 pclk1 邊沿讀 palette-RAM cell(不影響模擬)</span></td></tr>
  </table></div>
  <div style="margin-top:.4rem"><span class="en">Composed system: <strong>14,723 nodes / 26,775 transistors</strong>
   (after connection-merge lowering; raw 15,164 / 27,305). The RP2A03G + RP2C02G pair is the same revision
   AccuracyCoin targets.</span><span class="zh">組成後的系統:<strong>14,723 節點 / 26,775 電晶體</strong>(connection-merge lowering 後;原始 15,164 / 27,305)。RP2A03G + RP2C02G 正是 AccuracyCoin 鎖定的版次。</span></div>
  <div style="margin-top:.4rem"><span class="en">Lineage: the engine is AprVisual.S1's C# re-implementation of
   <a href="../metalnes.html">MetalNES</a>'s wire / group-resolution core (itself descended from
   visual6502.org's chipsim); the chip netlists are Quietust's Visual2A03 / Visual2C02 die tracings and the
   board/system module definitions follow MetalNES's system-def format — see
   <a href="../lineage.html">the lineage page</a> for full credits.</span><span class="zh">血緣:引擎是 AprVisual.S1 對 <a href="../metalnes.html">MetalNES</a> wire/群組解析核心的 C# 重寫(其前身為 visual6502.org 的 chipsim);晶片 netlist 為 Quietust 的 Visual2A03 / Visual2C02 晶粒描繪,主機板/系統模組定義沿用 MetalNES 的 system-def 格式 —— 完整致謝見<a href="../lineage.html">血緣頁</a>。</span></div>
 </details>
 <details style="margin-top:.6rem">
  <summary style="cursor:pointer;color:#5dadec"><strong><span class="en">Faithful deviations — where failing IS the faithful result (evidence dossier)</span><span class="zh">忠實偏差 —— 失敗本身就是忠實的結果(證據卷宗)</span></strong></summary>
  <div style="margin-top:.5rem"><span class="en">Some FAILs below are not simulator bugs: the switch-level model reproduces real-silicon
   behaviors that the tests themselves document as hardware-dependent. Every claim here is backed three ways:
   <b>(a)</b> the test author's own words, quoted verbatim with the file path inside the <code>nes-test-roms</code>
   collection so anyone can verify locally; <b>(b)</b> NESdev wiki / forum references; <b>(c)</b> how reference-grade
   emulators model the same variability.</span><span class="zh">以下部分 FAIL 不是模擬器 bug:開關級模型重現了測試自己都記載為「依硬體而異」的真實矽晶行為。每一條主張都有三重佐證:<b>(a)</b> 測試作者的原文逐字引用,附 <code>nes-test-roms</code> 合集內的檔案路徑,任何人可本地驗證;<b>(b)</b> NESdev wiki/論壇文獻;<b>(c)</b> 參考級模擬器對同一變異性的建模方式。</span></div>

  <div style="margin-top:.7rem"><strong><span class="en">1. OAM is dynamic RAM — <code>oam_read</code>, <code>cpu_dummy_writes_oam</code></span><span class="zh">1. OAM 是動態記憶體(DRAM)—— <code>oam_read</code>、<code>cpu_dummy_writes_oam</code></span></strong><br>
   <span class="en">Our 2C02 keeps OAM as physical DRAM cells in the netlist (not a plain array); oam_read fails with a
   <code>*</code>-patterned dump (CRC E03E03AD).</span><span class="zh">我們的 2C02 把 OAM 保持為 netlist 中的物理 DRAM cell(不是普通陣列);oam_read 以帶 <code>*</code> 圖樣的 dump 失敗(CRC E03E03AD)。</span><br>
   <em><span class="en">Author's own readme</span><span class="zh">作者自己的 readme</span></em> (<code>oam_read/readme.txt</code>):
   <blockquote style="margin:.3rem 0 .3rem .8rem;padding:.3rem .6rem;border-left:3px solid #2d4a6f;color:#a8c7e8;font-style:italic">
   "On my NTSC front-loader NES, I get the following four general patterns at random after power/reset"</blockquote>
   <span class="en">— and of blargg's four documented real-hardware patterns, <strong>three end in "Failed"</strong>
   (CRCs 694ADBE0, E9E8E60F, 44551956); only one passes. Our result is a member of that documented family.</span><span class="zh">—— blargg 記錄的四種真實硬體圖樣中,<strong>三種以「Failed」收場</strong>(CRC 694ADBE0、E9E8E60F、44551956),只有一種通過。我們的結果正是這個已記載家族的一員。</span><br>
   <em><span class="en">The other test declares its own limitation on screen</span><span class="zh">另一個測試在畫面上自述其限制</span></em> (cpu_dummy_writes_oam):
   <blockquote style="margin:.3rem 0 .3rem .8rem;padding:.3rem .6rem;border-left:3px solid #2d4a6f;color:#a8c7e8;font-style:italic">
   "Requirement: OAM memory reads MUST be reliable. This is often the case on emulators, but NOT on the real NES."</blockquote>
   <span class="en">Community: <a href="https://www.nesdev.org/wiki/PPU_OAM" target="_blank" rel="noopener">NESdev: PPU OAM</a>
   ("OAM uses dynamic memory (which will slowly decay if the PPU is not rendering)");
   <a href="https://www.nesdev.org/wiki/PPU_power_up_state" target="_blank" rel="noopener">NESdev: PPU power-up state</a>
   ("The contents of OAM are unspecified both at power on and at reset due to DRAM decay").
   Reference emulators: <a href="https://github.com/SourMesen/Mesen2" target="_blank" rel="noopener">Mesen2</a> ships an
   opt-in <code>EnablePpuOamRowCorruption</code> setting (Core/Shared/SettingTypes.h) precisely because real OAM misbehaves.</span><span class="zh">社群文獻:<a href="https://www.nesdev.org/wiki/PPU_OAM" target="_blank" rel="noopener">NESdev: PPU OAM</a>(「OAM 使用動態記憶體(PPU 未渲染時會慢慢衰減)」);<a href="https://www.nesdev.org/wiki/PPU_power_up_state" target="_blank" rel="noopener">NESdev: PPU power-up state</a>(「OAM 內容在上電與重設時均為未定義,因 DRAM 衰減」)。參考級模擬器:<a href="https://github.com/SourMesen/Mesen2" target="_blank" rel="noopener">Mesen2</a> 內建可選的 <code>EnablePpuOamRowCorruption</code> 設定(Core/Shared/SettingTypes.h)—— 正因為真實 OAM 會出錯。</span></div>

  <div style="margin-top:.7rem"><strong><span class="en">2. CPU&divide;12 / PPU&divide;4 power-on alignment — <code>10-even_odd_timing</code></span><span class="zh">2. CPU&divide;12 / PPU&divide;4 上電對齊 —— <code>10-even_odd_timing</code></span></strong><br>
   <span class="en">A real console powers up in one of several CPU-PPU fine alignments
   (<a href="https://forums.nesdev.org/viewtopic.php?t=6186" target="_blank" rel="noopener">NESdev forum: "CPU-PPU clock
   alignment"</a>, with blargg's own analysis in-thread; the odd-frame dot skip is documented at
   <a href="https://www.nesdev.org/wiki/PPU_frame_timing" target="_blank" rel="noopener">NESdev: PPU frame timing</a>).
   Reference emulators: Mesen2 ships <code>RandomizeCpuPpuAlignment</code> (Core/NES/NesCpu.cpp) and logs the drawn
   alignment per power-on — the variability is standard knowledge.</span><span class="zh">真實主機上電時會落入數種 CPU-PPU 細對齊之一
   (<a href="https://forums.nesdev.org/viewtopic.php?t=6186" target="_blank" rel="noopener">NESdev 論壇:「CPU-PPU clock alignment」</a>,串內有 blargg 本人的分析;奇數幀 dot-skip 行為見
   <a href="https://www.nesdev.org/wiki/PPU_frame_timing" target="_blank" rel="noopener">NESdev: PPU frame timing</a>)。
   參考級模擬器:Mesen2 內建 <code>RandomizeCpuPpuAlignment</code>(Core/NES/NesCpu.cpp)並在每次上電記錄抽到的對齊 —— 這種變異性是社群標準知識。</span><br>
   <span class="en">We <em>deterministically enumerated all four alignments</em> (engine flag <code>--reset-hold-extra</code>; the probe also
   revealed the CPU divider free-runs from power-on while the PPU divider restarts at /res release) and measured:</span><span class="zh">我們<em>確定性地列舉了全部四種對齊</em>(引擎旗標 <code>--reset-hold-extra</code>;探針同時發現 CPU 除頻器自上電起自由運轉、PPU 除頻器在 /res 釋放時重啟),實測如下:</span>
   <div style="margin:.4rem 0 .4rem .8rem;overflow-x:auto"><table style="border-collapse:collapse;font-size:.78rem">
    <tr style="color:#5dadec;text-align:left"><th style="padding:.1rem .6rem .1rem 0"><span class="en">Alignment (K)</span><span class="zh">對齊(K)</span></th>
     <th style="padding:.1rem .6rem"><span class="en">NMI-edge family (8 tests)</span><span class="zh">NMI 邊沿家族(8 個)</span></th><th style="padding:.1rem .6rem">10-even_odd_timing</th></tr>
    <tr><td style="padding:.1rem .6rem .1rem 0">1 (K=0)</td><td style="padding:.1rem .6rem">FAIL</td><td style="padding:.1rem .6rem">PASS</td></tr>
    <tr><td style="padding:.1rem .6rem .1rem 0"><strong><span class="en">7 (K=1) &larr; chosen</span><span class="zh">7(K=1)&larr; 定案</span></strong></td><td style="padding:.1rem .6rem">PASS</td><td style="padding:.1rem .6rem">FAIL</td></tr>
    <tr><td style="padding:.1rem .6rem .1rem 0">5 (K=3)</td><td style="padding:.1rem .6rem">PASS</td><td style="padding:.1rem .6rem">FAIL</td></tr>
    <tr><td style="padding:.1rem .6rem .1rem 0">3 (K=5)</td><td style="padding:.1rem .6rem">FAIL</td><td style="padding:.1rem .6rem">PASS</td></tr>
   </table></div>
   <span class="en">A <strong>perfect complementary 2+2 split — zero intersection</strong>: on this silicon model no single power-on state
   satisfies both groups, so any fixed-alignment system (including a real NES that is not power-cycled between tests)
   pays this price. We fix alignment 7 (the one blargg's NMI-edge tests were calibrated on);
   10-even_odd_timing's FAIL is the documented cost of that choice.</span><span class="zh"><strong>完美的 2+2 互補分裂 —— 零交集</strong>:在這個矽晶模型上,沒有任何單一上電狀態能同時滿足兩組測試;任何固定對齊的系統(包括一台不重開機換對齊的真 NES)都要付這個代價。我們固定在對齊 7(blargg 的 NMI 邊沿測試所校準的那個);10-even_odd_timing 的 FAIL 就是這個選擇的已文件化成本。</span></div>

  <div style="margin-top:.7rem"><strong><span class="en">3. PPU open-bus decay — <code>ppu_open_bus</code> (fix planned)</span><span class="zh">3. PPU open-bus 衰減 —— <code>ppu_open_bus</code>(修復計畫中)</span></strong><br>
   <em><span class="en">Author's readme</span><span class="zh">作者的 readme</span></em> (<code>ppu_open_bus/readme.txt</code>):
   <blockquote style="margin:.3rem 0 .3rem .8rem;padding:.3rem .6rem;border-left:3px solid #2d4a6f;color:#a8c7e8;font-style:italic">
   "If a bit isn't refreshed with a 1 for about 600 milliseconds, it will decay to 0 (some decay sooner, depending on
   the NES and temperature)."</blockquote>
   <span class="en">Temperature-dependent charge leakage is an analog phenomenon the switch-level model cannot express (floating nodes
   hold indefinitely); a documented behavioral decay timer is the planned fix.</span><span class="zh">依溫度而異的電荷洩漏是開關級模型無法表達的類比現象(浮空節點永久保持);計畫以文件化的行為衰減計時器修復。</span></div>

  <div style="margin-top:.7rem"><strong><span class="en">4. Power-up state — <code>power_up_palette</code>, <code>registers</code> (fixed via documented shim)</span><span class="zh">4. 上電狀態 —— <code>power_up_palette</code>、<code>registers</code>(已用文件化 shim 修復)</span></strong><br>
   <em><span class="en">Author's own source comment</span><span class="zh">作者原始碼開頭的註解</span></em> (<code>blargg_ppu_tests_2005.09.15b/source/power_up_palette.asm</code>, line 1):
   <blockquote style="margin:.3rem 0 .3rem .8rem;padding:.3rem .6rem;border-left:3px solid #2d4a6f;color:#a8c7e8;font-style:italic">
   "Reports whether initial values in palette at power-up match those that my NES has. These values are probably
   unique to my NES."</blockquote>
   <span class="en"><a href="https://www.nesdev.org/wiki/PPU_power_up_state" target="_blank" rel="noopener">NESdev: PPU power-up state</a>
   lists palette contents as "unspecified" at power on. Test mode injects the consensus table (and clears the Z flag to
   the real power-on P=$34) into the netlist cells via a drive&rarr;settle&rarr;release sequence; the benchmark path is untouched.</span><span class="zh"><a href="https://www.nesdev.org/wiki/PPU_power_up_state" target="_blank" rel="noopener">NESdev: PPU power-up state</a> 將 palette 上電內容列為「未定義」。測試模式以驅動&rarr;settle&rarr;釋放的程序把共識表(並把 Z flag 清為真實上電的 P=$34)注入 netlist cell;benchmark 路徑不受影響。</span></div>
 </details></div>
<div class="controls">
  <div class="btn-group" id="fbtns">
    <button class="active" data-f="all">All</button><button data-f="pass">Pass</button>
    <button data-f="fail">Fail</button><button data-f="timeout">Timeout</button><button data-f="pending">Pending</button>
  </div>
  <select id="suite-select"><option value="all">All Suites</option></select>
  <input type="text" id="search" placeholder="Search ROM name...">
</div>
<div id="content"></div>
<div class="modal-overlay" id="modal"><img id="modal-img" src="" alt=""><div class="modal-title" id="modal-title"></div></div>
<div class="footer"><span class="en">Generated by AprVisual.S1 test runner (tools/testrom/) &mdash;</span><span class="zh">由 AprVisual.S1 測試執行器產生(tools/testrom/)——</span>
 <a href="https://github.com/erspicu/AprVisual">github.com/erspicu/AprVisual</a></div>
<script>
function setLang(l){
  document.body.className='lang-'+l;
  document.documentElement.lang=(l==='zh')?'zh-Hant':'en';
  document.getElementById('btn-en').classList.toggle('active',l==='en');
  document.getElementById('btn-zh').classList.toggle('active',l==='zh');
  try{localStorage.setItem('aprvisual-lang',l);}catch(e){}
}
(function(){
  var l=null;
  try{l=localStorage.getItem('aprvisual-lang');}catch(e){}
  if(l!=='en'&&l!=='zh'){l=(navigator.language||'').toLowerCase().indexOf('zh')===0?'zh':'en';}
  setLang(l);
})();
var BUILD_DATE='__BUILD_DATE__', ENGINE='__ENGINE__';
var RESULTS=__RESULTS__;
document.getElementById('build-info').textContent='engine '+(ENGINE||'?')+' \\u00b7 generated '+BUILD_DATE;
var C={pass:0,fail:0,timeout:0,pending:0};
RESULTS.forEach(function(r){C[r.status]=(C[r.status]||0)+1});
var total=RESULTS.length, done=C.pass+C.fail+C.timeout;
document.getElementById('n-pass').textContent=C.pass;
document.getElementById('n-fail').textContent=C.fail;
document.getElementById('n-to').textContent=C.timeout;
document.getElementById('n-pend').textContent=C.pending;
document.getElementById('n-total').textContent=total;
document.getElementById('bar-p').style.width=(C.pass/total*100)+'%';
document.getElementById('bar-f').style.width=(C.fail/total*100)+'%';
document.getElementById('bar-t').style.width=(C.timeout/total*100)+'%';
document.getElementById('progress-text').textContent=C.pass+' pass / '+done+' run / '+total+' total'+(done<total?' ('+(total-done)+' pending)':'');
var filter='all',suiteFilter='all',search='',collapsed={};
var suiteNames=[],seen={};
RESULTS.forEach(function(r){if(!seen[r.suite]){seen[r.suite]=1;suiteNames.push(r.suite)}});
suiteNames.sort();
var sel=document.getElementById('suite-select');
suiteNames.forEach(function(s){var o=document.createElement('option');o.value=s;o.textContent=s;sel.appendChild(o)});
document.querySelectorAll('#fbtns button').forEach(function(b){b.addEventListener('click',function(){
document.querySelectorAll('#fbtns button').forEach(function(x){x.classList.remove('active')});
b.classList.add('active');filter=b.dataset.f;render()})});
sel.addEventListener('change',function(){suiteFilter=sel.value;render()});
document.getElementById('search').addEventListener('input',function(e){search=e.target.value.toLowerCase();render()});
var modal=document.getElementById('modal');
modal.addEventListener('click',function(){modal.classList.remove('active')});
function openModal(src,t){document.getElementById('modal-img').src=src;document.getElementById('modal-title').textContent=t;modal.classList.add('active')}
function toggleSuite(s){collapsed[s]=!collapsed[s];render()}
function toggleResult(id){var el=document.getElementById(id);if(el)el.classList.toggle('expanded')}
function esc(s){var d=document.createElement('div');d.appendChild(document.createTextNode(s||''));return d.innerHTML}
function render(){
var f=RESULTS.filter(function(r){
if(filter!=='all'&&r.status!==filter)return false;
if(suiteFilter!=='all'&&r.suite!==suiteFilter)return false;
if(search&&r.rom.toLowerCase().indexOf(search)<0&&r.suite.toLowerCase().indexOf(search)<0)return false;
return true});
var groups={};f.forEach(function(r){(groups[r.suite]=groups[r.suite]||[]).push(r)});
var order={fail:0,timeout:1,pending:2,pass:3};
var html='';
Object.keys(groups).sort().forEach(function(suite){
var ts=groups[suite];ts.sort(function(a,b){return(order[a.status]-order[b.status])||a.rom.localeCompare(b.rom)});
var sp=ts.filter(function(x){return x.status==='pass'}).length;
var sf=ts.filter(function(x){return x.status==='fail'}).length;
var st=ts.filter(function(x){return x.status==='timeout'}).length;
var safeId=suite.replace(/[^a-zA-Z0-9]/g,'_');
html+='<div class="suite-section"><div class="suite-header" onclick="toggleSuite(\\''+suite.replace(/'/g,"\\\\'")+'\\')">';
html+='<span class="arrow'+(collapsed[suite]?' collapsed':'')+'">&#9660;</span><span class="name">'+esc(suite)+'</span>';
if(sf)html+='<span class="badge fail">'+sf+' fail</span>';
if(st)html+='<span class="badge timeout">'+st+' t/o</span>';
html+='<span class="badge pass">'+sp+' pass</span><span class="badge det">'+ts.length+' tests</span></div>';
html+='<div class="card-grid'+(collapsed[suite]?' hidden':'')+'">';
ts.forEach(function(t,i){
var rid='r_'+safeId+'_'+i;
html+='<div class="card"><div class="thumb" onclick="openModal(\\''+t.screenshot+'\\',\\''+esc(t.rom)+'\\')">';
if(t.screenshot)html+='<img src="'+esc(t.screenshot)+'" loading="lazy" onerror="this.style.display=\\'none\\';this.nextElementSibling.style.display=\\'flex\\'"><div class="no-img" style="display:none">No Screenshot</div>';
else html+='<div class="no-img">No Screenshot</div>';
html+='</div><div class="info">';
html+='<span class="badge '+t.status+'">'+t.status+'</span> <span class="badge cls">'+esc(t['class'])+'</span>';
if(t.detection)html+=' <span class="badge det">'+esc(t.detection)+'</span>';
html+='<div class="rom-name">'+esc(t.rom)+'</div><div class="suite-name">'+esc(t.suite)+'</div>';
if(t.frames)html+='<div class="meta">'+t.frames+' frames \\u00b7 sim '+t.simSeconds.toFixed(1)+'s \\u00b7 wall '+Math.round(t.wallSeconds/60)+'min'+(t.khcPerSec?' \\u00b7 '+t.khcPerSec+' khc/s':'')+(t.resetCount?' \\u00b7 '+t.resetCount+' reset':'')+'</div>';
if(t.result_text){html+='<button class="expand-btn" onclick="toggleResult(\\''+rid+'\\')">Details &#9656;</button>';
html+='<div class="result-text" id="'+rid+'">'+esc(t.result_text)+'</div>'}
html+='</div></div>'});
html+='</div></div>'});
document.getElementById('content').innerHTML=html||'<div class="empty-msg">No matching tests.</div>';
}
render();
</script>
</body>
</html>
"""

html = html.replace("__BUILD_DATE__", build_date)
html = html.replace("__ENGINE__", engine_ver)
html = html.replace("__RESULTS__", json.dumps(rows, ensure_ascii=False))

with open(os.path.join(REPORT_DIR, "index.html"), "w", encoding="utf-8") as fp:
    fp.write(html)

print(f"REPORT: {counts} -> {os.path.join(REPORT_DIR, 'index.html')}")
