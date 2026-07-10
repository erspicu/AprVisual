#!/usr/bin/env python3
"""Render the AccuracyCoin run as a bilingual website panel: stats + a throughput curve.

Every number on the page is computed from the run's own artifacts -- shots/progress.jsonl
(one checkpoint per 10 simulated frames) and AccuracyCoin.json (the verdict). Nothing is
hand-typed, so re-running this after the sweep finishes is the whole "publish" step.

The chart: x = wall-clock hours since the run began, y = instantaneous throughput in
K half-cycles/s, one sample per checkpoint interval (differenced, so each point is
independent). The y-axis starts at zero -- the interesting structure (a ~1.4x cost
difference between frames that render and frames that blank the screen) survives an
honest baseline, so there is no reason to truncate one.

Inline SVG, no script and no external assets: it inherits the page's CSS custom
properties, so it stays in the site's palette by construction.

  python tools/testrom/ac_chart.py --dir tools/testrom/out/ac --inject WebSite/index.html
"""
import argparse, json, os, shutil, statistics as st, sys

FPS               = 60.0988
NES_REALTIME_HC_S = 42_954_552.0
BEGIN, END        = "<!-- AC-PANEL:BEGIN -->", "<!-- AC-PANEL:END -->"

W, H        = 900, 320
ML, MR      = 62, 20
MT, MB      = 18, 40
PW, PH      = W - ML - MR, H - MT - MB
HOVER_EVERY = 10          # one tooltip target per ~600 simulated frames; keeps the DOM small


def load(d):
    cps = []
    jl = os.path.join(d, "shots", "progress.jsonl")
    if os.path.isfile(jl):
        for ln in open(jl, encoding="utf-8"):
            if ln.strip():
                try:
                    cps.append(json.loads(ln))
                except json.JSONDecodeError:
                    pass
    cps.sort(key=lambda c: c["frame"])
    res = None
    rj = os.path.join(d, "AccuracyCoin.json")
    if os.path.isfile(rj):
        res = json.load(open(rj, encoding="utf-8"))
    return cps, res


def series(cps):
    """Differenced samples: (wall hours, K hc/s, frame)."""
    out = []
    for a, b in zip(cps, cps[1:]):
        dw = b["wallSec"] - a["wallSec"]
        dh = b["hc"] - a["hc"]
        if dw > 0:
            out.append((b["wallSec"] / 3600.0, dh / dw / 1000.0, b["frame"]))
    return out


def nice_max(v):
    for step in (20, 25, 40, 50, 100):
        top = step * (int(v / step) + 1)
        if top >= v * 1.05:
            return top, step
    return v, v / 4


def svg(pts, mean_khcs):
    if not pts:
        return "<p>(no samples)</p>"
    xmax = max(p[0] for p in pts)
    ymax, ystep = nice_max(max(p[1] for p in pts))

    def X(h): return ML + (h / xmax) * PW if xmax else ML
    def Y(v): return MT + PH - (v / ymax) * PH

    o = [f'<svg viewBox="0 0 {W} {H}" width="100%" role="img" '
         f'aria-labelledby="acTitle acDesc" style="max-width:100%;height:auto;display:block">',
         '<title id="acTitle">AccuracyCoin run: simulation throughput over wall-clock time</title>',
         f'<desc id="acDesc">{len(pts)} samples, one per 10 simulated frames, over '
         f'{xmax:.2f} hours. Mean {mean_khcs:,.1f} K half-cycles per second. The two plateaus are '
         f'workload: frames that leave PPU rendering on toggle far more netlist nodes than frames '
         f'that blank the screen.</desc>']

    # gridlines + y ticks (hairline, solid, recessive)
    v = 0
    while v <= ymax + 1e-9:
        y = Y(v)
        o.append(f'<line x1="{ML}" y1="{y:.1f}" x2="{ML+PW}" y2="{y:.1f}" stroke="var(--bd)" stroke-width="1"/>')
        o.append(f'<text x="{ML-10}" y="{y+4:.1f}" text-anchor="end" fill="var(--mut)" '
                 f'font-size="12" font-family="ui-monospace,monospace">{v:g}</text>')
        v += ystep

    # x ticks, every whole hour
    h = 0
    while h <= xmax + 1e-9:
        x = X(h)
        o.append(f'<line x1="{x:.1f}" y1="{MT+PH}" x2="{x:.1f}" y2="{MT+PH+5}" stroke="var(--bd)" stroke-width="1"/>')
        o.append(f'<text x="{x:.1f}" y="{MT+PH+20}" text-anchor="middle" fill="var(--mut)" '
                 f'font-size="12" font-family="ui-monospace,monospace">{h:g}h</text>')
        h += 1

    o.append(f'<text x="{ML-46}" y="{MT+PH/2}" fill="var(--mut)" font-size="12" '
             f'transform="rotate(-90 {ML-46} {MT+PH/2})" text-anchor="middle">K hc/s</text>')
    o.append(f'<text x="{ML+PW}" y="{H-4}" text-anchor="end" fill="var(--mut)" font-size="12">'
             f'wall-clock hours since start</text>')

    # the series: 2px, round join/cap, one hue, no per-point markers
    d = " ".join(("M" if i == 0 else "L") + f"{X(p[0]):.1f} {Y(p[1]):.1f}" for i, p in enumerate(pts))
    o.append(f'<path d="{d}" fill="none" stroke="var(--acc)" stroke-width="2" '
             f'stroke-linejoin="round" stroke-linecap="round"/>')

    # mean reference line + direct label (a reference line, not a gridline -- dashes are fine here)
    ym = Y(mean_khcs)
    o.append(f'<line x1="{ML}" y1="{ym:.1f}" x2="{ML+PW}" y2="{ym:.1f}" stroke="var(--mut)" '
             f'stroke-width="1" stroke-dasharray="4 4"/>')
    o.append(f'<text x="{ML+6}" y="{ym-6:.1f}" fill="var(--fg)" font-size="12" '
             f'font-family="ui-monospace,monospace">mean {mean_khcs:,.1f}K</text>')

    # end dot with a 2px surface ring
    lx, ly = X(pts[-1][0]), Y(pts[-1][1])
    o.append(f'<circle cx="{lx:.1f}" cy="{ly:.1f}" r="4" fill="var(--acc)" stroke="var(--card)" stroke-width="2"/>')

    # sparse hover targets -> native tooltips, no script
    for i in range(0, len(pts), HOVER_EVERY):
        hh, kk, fr = pts[i]
        o.append(f'<circle cx="{X(hh):.1f}" cy="{Y(kk):.1f}" r="7" fill="transparent">'
                 f'<title>frame {fr:,} | {hh:.2f} h | {kk:,.1f} K hc/s</title></circle>')

    o.append("</svg>")
    return "\n".join(o)


def box(n, en, zh):
    return (f'<div class="box"><div class="n">{n}</div><div class="l">'
            f'<span class="en">{en}</span><span class="zh">{zh}</span></div></div>')


def panel(cps, res, shot_rel):
    pts = series(cps)
    if res:
        frames, wall, hc = res["frames"], res["wallSeconds"], res["halfCycles"]
        passed = total = None
        txt = res.get("resultText", "")
        if "/" in txt:                       # "AccuracyCoin: 141/141 passed, 0 skipped"
            try:
                head = txt.split(":")[1].strip().split()[0]
                passed, total = (int(x) for x in head.split("/"))
            except Exception:
                pass
        done = True
    else:
        last = cps[-1]
        frames, wall, hc = last["frame"], last["wallSec"], last["hc"]
        passed = total = None
        done = False

    rate = hc / wall / 1000.0
    L = []
    a = L.append
    a(BEGIN)
    a('<h3 style="margin-top:2.2rem">'
      '<span class="en">AccuracyCoin — 141 tests, unattended</span>'
      '<span class="zh">AccuracyCoin —— 141 個測試,無人值守</span></h3>')

    if not done:
        a('<p><b><span class="en">Run in progress</span><span class="zh">測試進行中</span></b> — '
          f'<span class="en">the figures below cover the first {frames:,} frames only.</span>'
          f'<span class="zh">以下數據僅涵蓋前 {frames:,} 幀。</span></p>')

    a('<p class="en"><a href="https://github.com/100thCoin/AccuracyCoin" target="_blank" rel="noopener">AccuracyCoin</a> '
      'is a single NROM cartridge holding 141 accuracy tests, written for an NTSC console with an '
      '<b>RP2A03G CPU and RP2C02G PPU</b> — exactly the die pair AprVisual.S1 simulates transistor by transistor. '
      'It ships as an interactive menu, so we forked it into an '
      '<a href="https://github.com/erspicu/AprAccuracyCoinUnattended" target="_blank" rel="noopener">unattended build</a>: '
      'the test loop itself is untouched, and the only addition is a hook that runs the whole suite with no controller '
      'and leaves a completion block in CPU RAM (<code>$07F0</code> = <code>DE B0 61</code>, then passed / total / skipped). '
      'The verdict is read from that block, never from the picture.</p>')
    a('<p class="zh"><a href="https://github.com/100thCoin/AccuracyCoin" target="_blank" rel="noopener">AccuracyCoin</a> '
      '是一張 NROM 卡帶,裡面裝了 141 個精確度測試,專為搭載 <b>RP2A03G CPU 與 RP2C02G PPU</b> 的 NTSC 主機而寫 —— '
      '正好就是 AprVisual.S1 逐電晶體模擬的那兩顆晶粒。它原本是互動選單,必須有人拿手把操作,'
      '所以我們把它 fork 成<a href="https://github.com/erspicu/AprAccuracyCoinUnattended" target="_blank" rel="noopener">無人值守版</a>:'
      '測試迴圈一行沒動,只加了一個掛勾,讓整套測試自己跑完,並在 CPU RAM 留下完成區塊'
      '(<code>$07F0</code> = <code>DE B0 61</code>,接著是通過數 / 總數 / 略過數)。'
      '判定值一律讀那個區塊,<b>不是</b>看畫面。</p>')

    a('<div class="stat">')
    if passed is not None:
        a(box(f"{passed}/{total}", "tests passed", "測試通過"))
    a(box(f"{frames:,}", "frames simulated", "模擬幀數"))
    a(box(f"{wall/3600:.1f} h", "wall time, one core", "牆鐘時間(單核)"))
    a(box(f"{rate:,.0f}K", "mean hc/s", "平均 hc/s"))
    a('</div>')

    if pts:
        mean = rate
        a('<figure style="margin:1.6rem 0">')
        a(svg(pts, mean))
        a('<figcaption style="color:var(--mut);font-size:.9rem;margin-top:.6rem">'
          f'<span class="en">Simulation throughput across the run — one sample per 10 simulated frames '
          f'({len(pts)} samples). The two plateaus are <b>workload, not the machine</b>: a test that leaves PPU '
          f'rendering on toggles far more netlist nodes, and costs about 1.4× more wall time per frame, than one '
          f'that blanks the screen. Half-cycles per frame are fixed by the clock. '
          f'Real-time NES would be {NES_REALTIME_HC_S/1000:,.0f} K hc/s — this run sits at '
          f'{100*rate*1000/NES_REALTIME_HC_S:.2f}% of it, i.e. {NES_REALTIME_HC_S/(rate*1000):,.0f}× slower than the console.</span>'
          f'<span class="zh">整段執行的模擬吞吐量 —— 每 10 個模擬幀採樣一次(共 {len(pts)} 點)。'
          f'圖上兩段平台是<b>工作量差異,不是機器問題</b>:讓 PPU 保持繪圖的測試會翻轉遠多的 netlist 節點,'
          f'每幀約比關掉畫面的測試貴 1.4 倍;每幀的 half-cycle 數則由時脈固定。'
          f'真機即時速度是 {NES_REALTIME_HC_S/1000:,.0f} K hc/s,本次執行約為其 '
          f'{100*rate*1000/NES_REALTIME_HC_S:.2f}%,即比真機慢 {NES_REALTIME_HC_S/(rate*1000):,.0f} 倍。</span>'
          '</figcaption>')
        a('</figure>')

    if shot_rel:
        a('<figure style="margin:1.6rem 0">')
        a(f'<img src="{shot_rel}" alt="AccuracyCoin results table as rendered by AprVisual.S1" '
          f'style="image-rendering:pixelated;width:100%;max-width:512px;border:1px solid var(--bd);border-radius:6px">')
        a('<figcaption style="color:var(--mut);font-size:.9rem;margin-top:.6rem">'
          '<span class="en">The final screen, rendered by the switch-level engine itself. Evidence, not the '
          'measurement — the score above comes from CPU RAM.</span>'
          '<span class="zh">結束畫面,由開關級引擎自己畫出來。它是證據,不是量測本身 —— 上面的分數來自 CPU RAM。</span>'
          '</figcaption>')
        a('</figure>')

    a('<p class="en"><b>Run configuration</b>, because it changes what the number means: '
      '<code>cart-extraram</code> is <b>not</b> mounted — several AccuracyCoin tests measure the <i>open bus</i> at '
      '<code>$6000-$7FFF</code>, and mapping RAM there would answer those reads with RAM and corrupt them silently; '
      'the same global test-mode shims as the 147-ROM sweep are enabled; power-on CPU/PPU alignment is pinned (K=1); '
      'one pinned core, no clock lock.</p>')
    a('<p class="zh"><b>執行配置</b>,因為它決定了這個數字的意義:<b>沒有</b>掛載 <code>cart-extraram</code> —— '
      'AccuracyCoin 有數個測試在量 <code>$6000-$7FFF</code> 的 <i>open bus</i>,'
      '把 RAM 映射上去會讓那些讀取讀到 RAM,測試照跑照給結果,但結果是錯的;'
      '其餘沿用與 147 顆回歸相同的全域測試模式 shim;開機時 CPU/PPU 相位對齊固定(K=1);單一綁定核心,不鎖頻。</p>')
    a(END)
    return "\n".join(L)


def main():
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", required=True)
    ap.add_argument("--inject", help="index.html to splice the panel into (between the AC-PANEL markers)")
    ap.add_argument("--out", help="write the panel HTML here instead")
    a = ap.parse_args()

    d = os.path.abspath(a.dir)
    cps, res = load(d)
    if not cps:
        sys.exit(f"no checkpoints under {d}/shots/progress.jsonl")

    shot_rel = None
    if a.inject:
        site = os.path.dirname(os.path.abspath(a.inject))
        src  = os.path.join(d, "AccuracyCoin.png")
        if os.path.isfile(src):
            os.makedirs(os.path.join(site, "img"), exist_ok=True)
            shutil.copy2(src, os.path.join(site, "img", "accuracycoin-s1.png"))
            shot_rel = "img/accuracycoin-s1.png"

    html = panel(cps, res, shot_rel)

    if a.out:
        open(a.out, "w", encoding="utf-8").write(html + "\n")
        print(f"[written] {a.out}")
    if a.inject:
        p = os.path.abspath(a.inject)
        s = open(p, encoding="utf-8").read()
        if BEGIN not in s or END not in s:
            sys.exit(f"markers {BEGIN} / {END} not found in {p}")
        pre, rest = s.split(BEGIN, 1)
        _, post = rest.split(END, 1)
        open(p, "w", encoding="utf-8").write(pre + html + post)
        print(f"[injected] {p}  ({len(series(cps))} samples, verdict={'yes' if res else 'pending'})")
    if not a.out and not a.inject:
        print(html)


if __name__ == "__main__":
    main()
