# -*- coding: utf-8 -*-
# AprVisual S1 ARM (Pi 5 / Cortex-A76) per-version performance report — self-contained HTML + inline SVG.
import csv, os, html
ROOT=r"C:\ai_project\AprVisual"; OUT=os.path.join(ROOT,"MD","REPORT")
rows=[]
with open(os.path.join(OUT,"data","version_perf_arm.csv"),encoding="utf-8-sig") as f:
    for r in csv.DictReader(f):
        rows.append(dict(v=r["version"],date=r["date"],hc=int(r["best3avg_hcs"]),il=int(r["il"]),native=int(r["native"]),tf=r["tf"]))
first,last=rows[0],rows[-1]
spd=last["hc"]/first["hc"]
order=[r["v"] for r in rows]

DESC={
"2026.05.30":("基線(R-1 之前)","SoA 非託管陣列、雙緩衝事件驅動 settle、FlagsToState LUT、fast-path 雛形。起跑點。"),
"2026.05.31":("R-1 動態 singleton fast-path ⭐","群組可證明只有自己(~70% pop)→ O(1) 解析、跳過群組走訪。ARM 上 +17%(commit a80dab4)。"),
"2026.06.01":("S2-A 內嵌節點鄰接","channel 資料內嵌進 NodeInfo,消掉進 TransistorList 的 pointer-chase(commit 9cc0dc7)。"),
"2026.06.03":("重構 + 官網","邏輯抽進 inline helper:ProcessQueue 自身 IL 359→130。"),
"2026.06.04":("hot-path 微優化","NodeConnections 移出 BFS 主迴圈等佈局微調。"),
"2026.06.05":("hot-path 微優化","延續佈局/分支微調。"),
"2026.06.07":("P-2/P-3/P-4 事件數剪枝 ⭐","在節點可證明不會變時跳過 enqueue 重算 —— 從源頭刪無謂重算。"),
"2026.06.07b":("剪枝定版 + 遮罩合併","P-2/3/4 收斂;合併成 bit-packed PruneMask。ARM 較 06.07 +11%。"),
"2026.06.08":("剖析工具(≈持平)","DEBUG-only 剖析器;C# hot path native 不變。"),
"2026.06.09":("&&-條件重排 + 供電折入","SetNodeState &&-子句依選擇度重排。"),
"2026.06.09b":("供電跳過折入遮罩(.NET 11 起)","turn-off 入列檢查一致化。native 3340,與 09c/d/e 完全相同。"),
"2026.06.09c":("BFS/DFS + 診斷","native 3340(同前)→ 沒改 C# hot path。"),
"2026.06.09d":("小改","native 3340(同前)→ 沒改 C# hot path。"),
"2026.06.09e":("小改","native 3340(同前)→ 沒改 C# hot path。"),
"2026.06.11":("range-prune + 自我捕捉 locality ⭐","class-major 重編號 → 剪枝遮罩變 ID 區間比較;首次彈出順序自我捕捉。"),
"2026.06.12":("B1 兩節點成對路徑","可證明兩節點群組就地解析(占 77% 走訪)。"),
"2026.06.18":(".NET 11 + 下降寫回拆分 ⭐","turn-off 扇出走預過濾端點清單。ARM 上 +14.6%(比 x64 的 +1.2% 大很多 —— 像 Rust 的 +6.9%,branchless 走訪受益更大)。"),
"2026.06.19":("disasm 驅動 codegen 微優化","讀 JIT x64 找的 5 個 bit-exact 優化(在 ARM 上也帶來 +4.9%)。"),
}
MILE={"2026.05.31","2026.06.07b","2026.06.11","2026.06.18"}

def line_chart(values,color,w=920,h=270,pad=52,fmt=lambda x:f"{x:,}",mark=None):
    n=len(values);vmin=min(values);vmax=max(values);span=(vmax-vmin)or 1
    lo=vmin-span*0.12;hi=vmax+span*0.10
    X=lambda i:pad+i*(w-2*pad)/(n-1); Y=lambda val:h-pad-(val-lo)*(h-2*pad)/(hi-lo)
    area=f'{X(0):.1f},{Y(lo):.1f} '+" ".join(f"{X(i):.1f},{Y(v):.1f}" for i,v in enumerate(values))+f' {X(n-1):.1f},{Y(lo):.1f}'
    pts=" ".join(f"{X(i):.1f},{Y(v):.1f}" for i,v in enumerate(values))
    dots=""
    for i,v in enumerate(values):
        big=mark and order[i] in mark
        dots+=f'<circle cx="{X(i):.1f}" cy="{Y(v):.1f}" r="{4.6 if big else 3}" fill="{"#ffd24a" if big else color}" stroke="#0d1420" stroke-width="{1.5 if big else 0}"><title>{order[i]}: {fmt(v)}</title></circle>'
    grid=""
    for k in range(5):
        gv=lo+(hi-lo)*k/4;gy=Y(gv)
        grid+=f'<line x1="{pad}" y1="{gy:.1f}" x2="{w-pad}" y2="{gy:.1f}" stroke="#1d2942"/><text x="{pad-6}" y="{gy+4:.1f}" text-anchor="end" font-size="10" fill="#7f93b3">{fmt(int(gv))}</text>'
    xl=""
    for i in range(n):
        xl+=f'<text x="{X(i):.1f}" y="{h-pad+15:.1f}" text-anchor="end" font-size="9" fill="#7f93b3" transform="rotate(-42 {X(i):.1f} {h-pad+15:.1f})">{order[i].replace("2026.","")}</text>'
    return f'<svg viewBox="0 0 {w} {h}" width="100%" style="max-width:{w}px"><defs><linearGradient id="g{color[1:]}" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="{color}" stop-opacity="0.30"/><stop offset="1" stop-color="{color}" stop-opacity="0"/></linearGradient></defs>{grid}<polygon points="{area}" fill="url(#g{color[1:]})"/><polyline points="{pts}" fill="none" stroke="{color}" stroke-width="2.6"/>{dots}{xl}</svg>'

chart_hc=line_chart([r["hc"] for r in rows],"#39d98a",mark=MILE)
chart_nat=line_chart([r["native"] for r in rows],"#f0a35e")
maxb=max(r["hc"] for r in rows)
bars="".join(f'<div class="barrow"><span class="bl mv">{r["v"]} {"★" if r["v"] in MILE else ""}</span><span class="bt"><span class="bf" style="width:{100*r["hc"]/maxb:.1f}%"></span></span><span class="bn">{r["hc"]:,} <span class="mut">({r["hc"]/first["hc"]:.2f}×)</span></span></div>' for r in rows)
prev=None;cards=""
for r in rows:
    d=DESC[r["v"]]; star="background:linear-gradient(90deg,#1c2c1a,#121c2c)" if r["v"] in MILE else ""
    dhc="" if prev is None else (f'<span style="color:#39d98a">+{100*(r["hc"]-prev["hc"])/prev["hc"]:.1f}%</span>' if r["hc"]>prev["hc"]*1.012 else f'<span class="mut">≈持平</span>')
    cards+=f'<div class="vc" style="{star}"><div class="vh"><span class="mv">{r["v"]}</span> <span class="mut">{r["date"]} · {r["tf"]}</span> <b>{d[0]}</b></div><div class="vd">{html.escape(d[1])}</div><div class="vm mut">{r["hc"]:,} hc/s {dhc} · ProcessQueue native {r["native"]:,} B</div></div>'
    prev=r
trs="";prev=None
for r in rows:
    dnat="" if prev is None else (f'<span style="color:#39d98a">▼{prev["native"]-r["native"]}</span>' if r["native"]<prev["native"] else (f'<span style="color:#f06a6a">▲{r["native"]-prev["native"]}</span>' if r["native"]>prev["native"] else '<span class="mut">=</span>'))
    trs+=f'<tr><td class="mv">{r["v"]}</td><td class="mut">{r["date"]}</td><td class="num">{r["hc"]:,}</td><td class="num">{r["hc"]/first["hc"]:.2f}×</td><td class="num">{r["native"]:,} {dnat}</td><td class="num">{r["il"]}</td><td class="mut">{r["tf"]}</td></tr>'
    prev=r
xtimes=42954552//last["hc"]

HTML=f"""<!doctype html><html lang="zh-Hant"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>AprVisual S1 — ARM (Pi 5) 效能改善歷史</title><style>
 body{{margin:0;background:#0d1420;color:#cdd9ec;font:15px/1.65 -apple-system,Segoe UI,Roboto,"Microsoft JhengHei",sans-serif}}
 .wrap{{max-width:1000px;margin:0 auto;padding:0 20px 90px}}
 .hero{{background:radial-gradient(120% 140% at 0% 0%,#163a2a 0%,#0d1420 55%);border-bottom:1px solid #23314a;padding:42px 0 30px;margin-bottom:8px}}
 .hero .wrap{{padding-bottom:0}} h1{{font-size:26px;margin:0 0 6px;color:#fff}}
 h2{{font-size:19px;margin:38px 0 12px;color:#fff;border-bottom:1px solid #23314a;padding-bottom:6px}}
 .mut{{color:#7f93b3}} .num{{text-align:right;font-variant-numeric:tabular-nums}}
 .kpi{{display:flex;gap:14px;flex-wrap:wrap;margin-top:22px}} .kpi .k{{flex:1;min-width:150px;background:#121c2c;border:1px solid #23314a;border-radius:12px;padding:14px 16px}}
 .k .big{{font-size:30px;font-weight:800;color:#fff;line-height:1.1}} .k .lab{{font-size:12px;color:#7f93b3;margin-top:4px}} .grn{{color:#39d98a}}
 .card{{background:#121c2c;border:1px solid #23314a;border-radius:12px;padding:16px 18px;margin:14px 0}}
 table{{width:100%;border-collapse:collapse;font-size:13px}} th,td{{padding:6px 8px;border-bottom:1px solid #1c2942;text-align:left}}
 th{{color:#9fc0ff;font-weight:600}} .mv{{font-family:ui-monospace,Consolas,monospace;color:#e7c98a;white-space:nowrap}}
 code{{background:#1b2740;padding:1px 5px;border-radius:4px;font-size:12.5px}}
 .note{{background:#15233a;border-left:3px solid #39d98a;padding:10px 14px;border-radius:0 8px 8px 0;margin:12px 0}} .warn{{border-left-color:#f0a35e;background:#241d12}}
 .barrow{{display:flex;align-items:center;gap:10px;margin:3px 0;font-size:12px}} .bl{{width:96px;flex:none}} .bt{{flex:1;background:#16223a;border-radius:5px;height:15px;overflow:hidden}}
 .bf{{display:block;height:100%;background:linear-gradient(90deg,#2c6fd6,#39d98a)}} .bn{{width:150px;text-align:right;flex:none;font-variant-numeric:tabular-nums}}
 .vc{{background:#121c2c;border:1px solid #23314a;border-radius:10px;padding:11px 14px;margin:8px 0}} .vh{{margin-bottom:3px}} .vd{{font-size:13.5px}} .vm{{font-size:12px;margin-top:5px}}
</style></head><body>
<div class="hero"><div class="wrap">
<h1>🔥 AprVisual S1 — ARM 效能改善歷史(Raspberry Pi 5)</h1>
<p class="mut">switch-level NES 模擬器 · 18 版(2026.05.30 → 2026.06.19)· <b>Cortex-A76 @ 鎖頻 2.4GHz</b> · 全程 <b>bit-exact</b>(checksum <code>0x9174E19D961CB6E5</code>,與 x64 逐位元相同)· best-of-3-of-5</p>
<div class="kpi">
 <div class="k"><div class="big grn">{spd:.2f}×</div><div class="lab">更快(首版→末版,ARM)</div></div>
 <div class="k"><div class="big">{first['hc']:,}<span style="font-size:16px;color:#7f93b3"> → </span>{last['hc']:,}</div><div class="lab">hc/s @ 2.4GHz</div></div>
 <div class="k"><div class="big">18</div><div class="lab">版,版版 bit-exact</div></div>
 <div class="k"><div class="big">~{xtimes}×</div><div class="lab">距 NES 即時(42.95M hc/s)</div></div>
</div></div></div>
<div class="wrap">
<div class="note">同一份引擎、跨指令集 <b>位元完全相同</b>(ARM checksum = x64 golden)。在 Pi 5(Cortex-A76)鎖頻 2.4GHz 下,引擎被連續打磨成 <b>{spd:.2f} 倍快</b>。下面是 ARM 上逐版的吞吐量(每版跑 5 次取最好 3 次平均)+ 每版改了什麼。</div>

<h2>📈 吞吐量成長(hc/s · 越高越好 · ★=里程碑)</h2>
<div class="card">{bars}</div>
<div class="card">{chart_hc}</div>

<h2>🛠 每一版改了什麼(ARM)</h2>
{cards}

<h2>🧱 ProcessQueue ARM 機械碼大小(bytes)</h2>
<div class="card">{chart_nat}</div>
<div class="note warn"><b>ARM native 比 x64 精簡</b>(末版 ARM 4,636 B vs x64 5,321 B)。<b>06.09b/c/d/e 的 native 完全相同(3,340 B)</b> → 這幾版沒改 C# hot path(Rust/診斷/小改),它們之間的 hc/s 差異純屬量測噪音(~61K 上下,<1%)。IL 在 06.03 由 359→130(邏輯抽進 inline helper)。</div>

<h2>📋 完整數據</h2>
<div class="card" style="overflow-x:auto"><table>
<tr><th>版本</th><th>日期</th><th class="num">hc/s(ARM)</th><th class="num">vs 首版</th><th class="num">native (B)</th><th class="num">IL</th><th>TFM</th></tr>
{trs}</table></div>

<h2>🔬 方法論與限制</h2>
<ul>
<li><b>機器</b>:Raspberry Pi 5(Arm Cortex-A76, ARMv8.2, 4c)· <b>鎖頻 2.4GHz</b>(cpufreq performance governor;測完還原 ondemand)· 同一顆 ROM、同 400k hc。</li>
<li><b>數值</b>:每版跑 5 次、<b>取最好 3 次平均</b>的 hc/s。全版 bit-exact(checksum 0x9174…)→ 工作量相同,差異純為引擎優化。</li>
<li><b>同一 runtime</b>:全部在 Pi 的 <b>.NET 11</b> runtime 上跑(net10.0 版用 <code>DOTNET_ROLL_FORWARD=Major</code>)。這<b>隔離了「引擎演進」與「.NET10→11 runtime 變更」</b>(x64 報告是各版用各自打包的 runtime;此 ARM 報告全在 .NET 11 → 更純粹反映引擎)。</li>
<li><b>R-1 / S2-A 來自 commit</b>:05.31(R-1)與 06.01(S2-A)的 release <b>tag</b> 是 pre-portable-fork 的 WinForms(net*-windows,Linux build 不了),故改用其引擎 commit <code>a80dab4</code>(R-1)/ <code>9cc0dc7</code>(S2-A)量測 —— 忠實對應該里程碑。其餘 16 版由 release tag 直接 build arm64。</li>
<li><b>native 大小</b>:<code>DOTNET_JitDisasmSummary</code> 的 ProcessQueue(舊版名 ProcessQueueInterp);ARM A64 機械碼。</li>
<li>vs x64:見 <a href="perf-history.html">x64 (Zen2) 報告</a>。x64 末版 ~138.7K / ARM 末版 ~76.3K(≈0.55×,時脈+微架構差距);兩邊趨勢一致、checksum 逐位元相同。</li>
</ul>
<p class="mut" style="margin-top:26px">ARM cycle-level 紀錄 · 全程 bit-exact · 產生於 2026-06-19 · 量測:tools/pi/(freq lock)+ nohup arm_perf_run.sh @ Pi 5</p>
</div></body></html>"""
p=os.path.join(OUT,"perf-history-arm.html")
open(p,"w",encoding="utf-8").write(HTML)
print("WROTE",p,f"({len(HTML)} bytes)  speedup {spd:.2f}x  {first['hc']}->{last['hc']} hc/s")
