# -*- coding: utf-8 -*-
# AprVisual S1 ARM (Pi 5 / Cortex-A76) per-version report — overclock progression 2.4 / 3.0 / 3.2GHz + temp trend.
import csv, os, html
ROOT=r"C:\ai_project\AprVisual"; OUT=os.path.join(ROOT,"MD","REPORT")
def load(fn):
    d={}
    with open(os.path.join(OUT,"data",fn),encoding="utf-8-sig") as f:
        for r in csv.DictReader(f):
            d[r["version"]]=dict(v=r["version"],date=r["date"],hc=int(r["best3avg_hcs"]),il=int(r["il"]),native=int(r["native"]),tf=r["tf"],temp=float(r.get("temp_c") or 0))
    return d
stock=load("version_perf_arm.csv")          # 2.4 GHz stock
oc30 =load("version_perf_arm_oc.csv")        # 3.0 GHz OC
oc32 =load("version_perf_arm_oc32.csv")      # 3.2 GHz OC (+50mV)  <- headline + temp
order=list(oc32.keys())
rows=[oc32[v] for v in order]
first,last=rows[0],rows[-1]
spd=last["hc"]/first["hc"]
ocgain=last["hc"]/stock[last["v"]]["hc"]
tmax=max(r["temp"] for r in rows); tmin=min(r["temp"] for r in rows)

DESC={
"2026.05.30":("基線(R-1 之前)","SoA 非託管陣列、雙緩衝事件驅動 settle、FlagsToState LUT、fast-path 雛形。"),
"2026.05.31":("R-1 動態 singleton fast-path ⭐","群組可證明只有自己(~70% pop)→ O(1) 解析、跳過群組走訪(commit a80dab4)。"),
"2026.06.01":("S2-A 內嵌節點鄰接","channel 資料內嵌進 NodeInfo,消掉 pointer-chase(commit 9cc0dc7)。"),
"2026.06.03":("重構 + 官網","邏輯抽進 inline helper:ProcessQueue IL 359→130。"),
"2026.06.04":("hot-path 微優化","NodeConnections 移出 BFS 主迴圈等佈局微調。"),
"2026.06.05":("hot-path 微優化","延續佈局/分支微調。"),
"2026.06.07":("P-2/P-3/P-4 事件數剪枝 ⭐","節點可證明不變時跳過 enqueue 重算 —— 從源頭刪無謂重算。"),
"2026.06.07b":("剪枝定版 + 遮罩合併","P-2/3/4 收斂;合併成 bit-packed PruneMask。"),
"2026.06.08":("剖析工具(≈持平)","DEBUG-only 剖析器;hot path native 不變。"),
"2026.06.09":("&&-條件重排 + 供電折入","SetNodeState &&-子句依選擇度重排。"),
"2026.06.09b":("供電跳過折入(.NET 11 起)","native 3340,與 09c/d/e 完全相同。"),
"2026.06.09c":("BFS/DFS + 診斷","native 3340(同前)→ 沒改 C# hot path。"),
"2026.06.09d":("小改","native 3340(同前)→ 沒改 C# hot path。"),
"2026.06.09e":("小改","native 3340(同前)→ 沒改 C# hot path。"),
"2026.06.11":("range-prune + 自我捕捉 locality ⭐","class-major 重編號 → 剪枝遮罩變 ID 區間;首次彈出順序自我捕捉。"),
"2026.06.12":("B1 兩節點成對路徑","可證明兩節點群組就地解析(占 77% 走訪)。"),
"2026.06.18":(".NET 11 + 下降寫回拆分 ⭐","turn-off 扇出走預過濾端點清單。ARM 受益大(比 x64 的 +1.2% 大很多)。"),
"2026.06.19":("disasm 驅動 codegen 微優化","讀 JIT 找的 5 個 bit-exact 優化。"),
}
MILE={"2026.05.31","2026.06.07b","2026.06.11","2026.06.18"}

def triple_chart(w=920,h=300,pad=54):
    series=[("2.4GHz 原廠",[stock[v]["hc"] for v in order],"#5b86c4","5 4"),
            ("3.0GHz 超頻",[oc30[v]["hc"] for v in order],"#9b7fd6","2 3"),
            ("3.2GHz 超頻(+50mV)",[r["hc"] for r in rows],"#39d98a",None)]
    allv=[x for _,vals,_,_ in series for x in vals]; vmin=min(allv); vmax=max(allv); span=(vmax-vmin)or 1
    lo=vmin-span*0.10; hi=vmax+span*0.10; n=len(order)
    X=lambda i:pad+i*(w-2*pad)/(n-1); Y=lambda v:h-pad-(v-lo)*(h-2*pad)/(hi-lo)
    grid=""
    for k in range(5):
        gv=lo+(hi-lo)*k/4; gy=Y(gv)
        grid+=f'<line x1="{pad}" y1="{gy:.1f}" x2="{w-pad}" y2="{gy:.1f}" stroke="#1d2942"/><text x="{pad-6}" y="{gy+4:.1f}" text-anchor="end" font-size="10" fill="#7f93b3">{int(gv):,}</text>'
    body=""
    for name,vals,color,dash in series:
        pts=" ".join(f"{X(i):.1f},{Y(v):.1f}" for i,v in enumerate(vals))
        da=f' stroke-dasharray="{dash}"' if dash else ''
        body+=f'<polyline points="{pts}" fill="none" stroke="{color}" stroke-width="{2.6 if not dash else 2}"{da}/>'
        for i,v in enumerate(vals):
            big=(not dash) and order[i] in MILE
            body+=f'<circle cx="{X(i):.1f}" cy="{Y(v):.1f}" r="{4.6 if big else 2.6}" fill="{"#ffd24a" if big else color}" stroke="#0d1420" stroke-width="{1.4 if big else 0}"><title>{order[i]} @{name}: {v:,}</title></circle>'
    xl="".join(f'<text x="{X(i):.1f}" y="{h-pad+15:.1f}" text-anchor="end" font-size="9" fill="#7f93b3" transform="rotate(-42 {X(i):.1f} {h-pad+15:.1f})">{order[i].replace("2026.","")}</text>' for i in range(n))
    ly=14; leg=""
    for name,_,color,dash in series:
        leg+=f'<rect x="{w-215}" y="{ly}" width="14" height="3" fill="{color}"/><text x="{w-197}" y="{ly+5}" font-size="11" fill="#cdd9ec">{name}</text>'; ly+=18
    return f'<svg viewBox="0 0 {w} {h}" width="100%" style="max-width:{w}px">{grid}{body}{xl}{leg}</svg>'

def temp_chart(vals,w=920,h=180,pad=46):
    n=len(vals);lo=20;hi=80
    X=lambda i:pad+i*(w-2*pad)/(n-1);Y=lambda v:h-pad-(v-lo)*(h-2*pad)/(hi-lo)
    pts=" ".join(f"{X(i):.1f},{Y(v):.1f}" for i,v in enumerate(vals))
    dot="".join(f'<circle cx="{X(i):.1f}" cy="{Y(v):.1f}" r="3" fill="#f0a35e"><title>{order[i]}: {v}°C</title></circle>' for i,v in enumerate(vals))
    guard=f'<line x1="{pad}" y1="{Y(60):.1f}" x2="{w-pad}" y2="{Y(60):.1f}" stroke="#f06a6a" stroke-dasharray="6 4"/><text x="{w-pad}" y="{Y(60)-5:.1f}" text-anchor="end" font-size="11" fill="#f06a6a">60°C 中止門檻</text>'
    thr=f'<line x1="{pad}" y1="{Y(85):.1f}" x2="{w-pad}" y2="{Y(85):.1f}" stroke="#7a3b3b" stroke-dasharray="2 3"/><text x="{pad}" y="{Y(85)-4:.1f}" font-size="10" fill="#a05656">Pi 85°C 降頻點</text>'
    grid="".join(f'<line x1="{pad}" y1="{Y(g):.1f}" x2="{w-pad}" y2="{Y(g):.1f}" stroke="#1d2942"/><text x="{pad-6}" y="{Y(g)+4:.1f}" text-anchor="end" font-size="10" fill="#7f93b3">{g}°C</text>' for g in (30,45,60,75))
    xl="".join(f'<text x="{X(i):.1f}" y="{h-pad+14:.1f}" text-anchor="end" font-size="9" fill="#7f93b3" transform="rotate(-42 {X(i):.1f} {h-pad+14:.1f})">{order[i].replace("2026.","")}</text>' for i in range(n))
    return f'<svg viewBox="0 0 {w} {h}" width="100%" style="max-width:{w}px">{grid}{thr}{guard}<polyline points="{pts}" fill="none" stroke="#f0a35e" stroke-width="2.4"/>{dot}{xl}</svg>'

chart=triple_chart()
tchart=temp_chart([r["temp"] for r in rows])
maxb=max(r["hc"] for r in rows)
bars="".join(f'<div class="barrow"><span class="bl mv">{r["v"]} {"★" if r["v"] in MILE else ""}</span><span class="bt"><span class="bf" style="width:{100*r["hc"]/maxb:.1f}%"></span></span><span class="bn">{r["hc"]:,} <span class="mut">({r["hc"]/first["hc"]:.2f}×)</span></span></div>' for r in rows)
prev=None;cards=""
for r in rows:
    d=DESC[r["v"]];star="background:linear-gradient(90deg,#1c2c1a,#121c2c)" if r["v"] in MILE else ""
    dhc="" if prev is None else (f'<span style="color:#39d98a">+{100*(r["hc"]-prev["hc"])/prev["hc"]:.1f}%</span>' if r["hc"]>prev["hc"]*1.012 else f'<span class="mut">≈持平</span>')
    cards+=f'<div class="vc" style="{star}"><div class="vh"><span class="mv">{r["v"]}</span> <span class="mut">{r["date"]} · {r["tf"]}</span> <b>{d[0]}</b></div><div class="vd">{html.escape(d[1])}</div><div class="vm mut">{r["hc"]:,} hc/s @3.2G {dhc} · 3.0G {oc30[r["v"]]["hc"]:,} · 2.4G {stock[r["v"]]["hc"]:,} · native {r["native"]:,} B · {r["temp"]}°C</div></div>'
    prev=r
trs=""
for r in rows:
    sc=r["hc"]/stock[r["v"]]["hc"]
    trs+=f'<tr><td class="mv">{r["v"]}</td><td class="mut">{r["date"]}</td><td class="num">{stock[r["v"]]["hc"]:,}</td><td class="num">{oc30[r["v"]]["hc"]:,}</td><td class="num" style="color:#8fe9bd">{r["hc"]:,}</td><td class="num">{sc:.2f}×</td><td class="num">{r["temp"]}°C</td><td class="num">{r["native"]:,}</td><td class="mut">{r["tf"]}</td></tr>'
xtimes=42954552//last["hc"]

HTML=f"""<!doctype html><html lang="zh-Hant"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>AprVisual S1 — ARM (Pi 5) 效能歷史 + 超頻 2.4/3.0/3.2GHz</title><style>
 body{{margin:0;background:#0d1420;color:#cdd9ec;font:15px/1.65 -apple-system,Segoe UI,Roboto,"Microsoft JhengHei",sans-serif}}
 .wrap{{max-width:1000px;margin:0 auto;padding:0 20px 90px}} .hero{{background:radial-gradient(120% 140% at 0% 0%,#163a2a 0%,#0d1420 55%);border-bottom:1px solid #23314a;padding:42px 0 30px}}
 .hero .wrap{{padding-bottom:0}} h1{{font-size:24px;margin:0 0 6px;color:#fff}} h2{{font-size:19px;margin:38px 0 12px;color:#fff;border-bottom:1px solid #23314a;padding-bottom:6px}}
 .mut{{color:#7f93b3}} .num{{text-align:right;font-variant-numeric:tabular-nums}}
 .kpi{{display:flex;gap:14px;flex-wrap:wrap;margin-top:22px}} .kpi .k{{flex:1;min-width:140px;background:#121c2c;border:1px solid #23314a;border-radius:12px;padding:14px 16px}}
 .k .big{{font-size:27px;font-weight:800;color:#fff;line-height:1.1}} .k .lab{{font-size:12px;color:#7f93b3;margin-top:4px}} .grn{{color:#39d98a}} .org{{color:#f0a35e}}
 .card{{background:#121c2c;border:1px solid #23314a;border-radius:12px;padding:16px 18px;margin:14px 0}}
 table{{width:100%;border-collapse:collapse;font-size:13px}} th,td{{padding:6px 8px;border-bottom:1px solid #1c2942;text-align:left}} th{{color:#9fc0ff;font-weight:600}}
 .mv{{font-family:ui-monospace,Consolas,monospace;color:#e7c98a;white-space:nowrap}} code{{background:#1b2740;padding:1px 5px;border-radius:4px;font-size:12.5px}}
 .note{{background:#15233a;border-left:3px solid #39d98a;padding:10px 14px;border-radius:0 8px 8px 0;margin:12px 0}} .warn{{border-left-color:#f0a35e;background:#241d12}}
 .barrow{{display:flex;align-items:center;gap:10px;margin:3px 0;font-size:12px}} .bl{{width:96px;flex:none}} .bt{{flex:1;background:#16223a;border-radius:5px;height:15px;overflow:hidden}}
 .bf{{display:block;height:100%;background:linear-gradient(90deg,#2c6fd6,#39d98a)}} .bn{{width:160px;text-align:right;flex:none;font-variant-numeric:tabular-nums}}
 .vc{{background:#121c2c;border:1px solid #23314a;border-radius:10px;padding:11px 14px;margin:8px 0}} .vh{{margin-bottom:3px}} .vd{{font-size:13.5px}} .vm{{font-size:12px;margin-top:5px}}
</style></head><body>
<div class="hero"><div class="wrap">
<h1>🔥 AprVisual S1 — ARM 效能歷史(Raspberry Pi 5)+ 超頻 2.4 / 3.0 / 3.2GHz</h1>
<p class="mut">switch-level NES 模擬器 · 18 版(2026.05.30→2026.06.19)· Cortex-A76 · 全程 <b>bit-exact</b>(<code>0x9174E19D961CB6E5</code>)· best-3-of-5 · 即時儀表板 <a href="https://baxermux.org/myemu/AprVisual/version/#arm64" style="color:#4ea1ff">/version</a> · x64 版 <a href="perf-history.html" style="color:#4ea1ff">perf-history.html</a></p>
<div class="kpi">
 <div class="k"><div class="big grn">{spd:.2f}×</div><div class="lab">引擎演進(首→末版 @3.2G)</div></div>
 <div class="k"><div class="big">{last['hc']:,}</div><div class="lab">峰值 hc/s @ 3.2GHz(06.19)</div></div>
 <div class="k"><div class="big org">+{(ocgain-1)*100:.0f}%</div><div class="lab">3.2G 超頻 vs 原廠 2.4G</div></div>
 <div class="k"><div class="big">{tmin:.0f}–{tmax:.0f}°C</div><div class="lab">全程溫度(門檻 60°C 未觸)</div></div>
</div></div></div>
<div class="wrap">
<div class="note warn"><b>超頻三級全程穩定 + bit-exact。</b> Pi 5 原廠上限 2.4GHz。<b>3.0GHz 原廠電壓就穩</b>;<b>3.2GHz 需 <code>over_voltage_delta=50000</code>(+50mV)</b>(原廠電壓下 3.2 開機失敗)。三級 18 版全部位元完全相同(checksum 0x9174…),末版 <b>2.4G {stock[last['v']]['hc']:,} → 3.0G {oc30[last['v']]['hc']:,} → 3.2G {last['hc']:,} hc/s(+{(ocgain-1)*100:.0f}% vs 原廠)</b>,近線性加速 → core/L1-bound。<b>3.2G 全程溫度 {tmin:.1f}–{tmax:.1f}°C(主動散熱),完全沒上升,離 60°C 中止門檻與 85°C 降頻點都很遠。</b></div>

<h2>📈 吞吐量:2.4 / 3.0 / 3.2GHz(hc/s · ★=里程碑)</h2>
<div class="card">{chart}</div>
<div class="card">{bars}</div>

<h2>🌡 溫度趨勢(每版測完量一次 · 3.2GHz · 60°C 安全中止)</h2>
<div class="card">{tchart}</div>
<div class="note"><b>溫度持平在 ~{tmin:.0f}–{tmax:.0f}°C</b>,18 版連續燒機沒有累積升溫。最高 {tmax:.1f}°C、零 throttle。安全 guard:測完任一版若 &gt;60°C 自動中止 —— 本次未觸發。</div>

<h2>🛠 每一版改了什麼(數值 @3.2GHz)</h2>
{cards}

<h2>📋 完整數據(2.4 / 3.0 / 3.2GHz)</h2>
<div class="card" style="overflow-x:auto"><table>
<tr><th>版本</th><th>日期</th><th class="num">2.4GHz</th><th class="num">3.0GHz</th><th class="num">3.2GHz</th><th class="num">3.2/2.4</th><th class="num">溫度</th><th class="num">native(B)</th><th>TFM</th></tr>
{trs}</table></div>

<h2>🔬 方法論與限制</h2>
<ul>
<li><b>機器</b>:Raspberry Pi 5(Arm Cortex-A76, ARMv8.2, 4c, 主動散熱)。三組頻率都 best-3-of-5、同 400k hc、cpufreq <b>performance governor 鎖定</b>。</li>
<li><b>超頻</b>:<code>/boot/firmware/config.txt</code> 設 <code>arm_freq</code>;3.2GHz 另加 <code>over_voltage_delta=50000</code>(+50mV)。逐級驗證,每級先確認 bit-exact + 溫度/throttle 才升。</li>
<li><b>溫控</b>:3.2G 這輪每版測完量溫度,&gt;60°C 即中止(本次最高 {tmax:.1f}°C,未觸發)。零 throttle。</li>
<li><b>同一 runtime</b>:全部在 .NET 11 上跑(net10.0 版用 <code>DOTNET_ROLL_FORWARD=Major</code>)→ 隔離引擎演進與 runtime 變更。</li>
<li><b>R-1 / S2-A 來自 commit</b>:05.31(R-1)/06.01(S2-A)的 release tag 是 pre-fork WinForms(Linux build 不了),改用引擎 commit <code>a80dab4</code>/<code>9cc0dc7</code>。其餘 16 版由 release tag build arm64。</li>
<li>vs x64:見 <a href="perf-history.html">x64 (Zen2) 報告</a>(末版 ~138.7K)。超頻 3.2G 後 ARM 末版 {last['hc']:,} ≈ Zen2 的 {last['hc']/138708:.2f}×。即時門檻 42.95M hc/s → 仍 ~{xtimes}× 之遙。</li>
</ul>
<p class="mut" style="margin-top:26px">ARM cycle-level 紀錄 · 全程 bit-exact · 2.4 / 3.0 / 3.2GHz + 溫度趨勢 · 產生於 2026-06-19 · Pi 5 主動散熱</p>
</div></body></html>"""
p=os.path.join(OUT,"perf-history-arm.html")
open(p,"w",encoding="utf-8").write(HTML)
print("WROTE",p,f"({len(HTML)}B) engine {spd:.2f}x  3.2G last {last['hc']} (+{(ocgain-1)*100:.0f}% vs 2.4G)  temp {tmin}-{tmax}C")
