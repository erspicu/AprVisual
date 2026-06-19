# -*- coding: utf-8 -*-
# AprVisual S1 performance-history report (self-contained HTML + inline SVG) from measured CSVs.
import csv, os, html

ROOT=r"C:\ai_project\AprVisual"; TEMP=os.path.join(ROOT,"temp"); OUT=os.path.join(ROOT,"MD","REPORT")
os.makedirs(OUT,exist_ok=True)
def load(n):
    with open(os.path.join(TEMP,n),encoding="utf-8-sig") as f: return list(csv.DictReader(f))
locked={r["Version"]:r for r in load("version_perf_locked.csv")}
boost ={r["Version"]:r for r in load("version_perf_final.csv")}   # for the "Change" annotation
bt    ={r["Version"]:r for r in load("version_boost.csv")}        # clean cool-machine boost (top-3 of 5, --pin)
size  ={r["Version"]:r for r in load("version_size.csv")}
order=["2026.05.30","2026.05.31","2026.06.01","2026.06.03","2026.06.04","2026.06.05","2026.06.07","2026.06.07b","2026.06.08","2026.06.09","2026.06.09b","2026.06.09c","2026.06.09d","2026.06.09e","2026.06.11","2026.06.12","2026.06.18","2026.06.19"]

# rich per-version descriptions (what changed)
DESC={
"2026.05.30":("基線版本(R-1 之前)","S1 fork 首個打包的 benchmark:SoA 非託管陣列(NodeStates/NodeInfo/TransistorList)、雙緩衝事件驅動 settle、256-entry FlagsToState LUT、3-class fast-path 雛形。後續所有優化的起跑點。"),
"2026.05.31":("R-1 動態 singleton fast-path ⭐","偵測「節點的導通群組可證明只有它自己」(佔約 70% 的 pop),直接 O(1) 解析、跳過整套群組走訪。<b>單一最大的勝利(−18% cyc/hc)</b>。"),
"2026.06.01":("S2-A 內嵌節點鄰接","把每個節點的 channel/gate 資料直接內嵌進 16-byte NodeInfo struct,消掉「載入 NodeInfo → 再 pointer-chase 進 TransistorList」那一跳相依載入。"),
"2026.06.03":("重構 + 官網上線","邏輯抽進 inline helper:ProcessQueue 自身 IL 從 359→130(整體靠 JIT inline 成一個方法)。GitHub Pages 文章站開張。"),
"2026.06.04":("hot-path 微優化","把 NodeConnections 移出 BFS 主迴圈等記憶體佈局微調。"),
"2026.06.05":("hot-path 微優化","延續佈局/分支微調。"),
"2026.06.07":("P-2/P-3/P-4 事件數剪枝 ⭐","引入「在節點可證明本波不會變時,跳過 enqueue 重算」的剪枝家族 —— 從源頭刪掉大量無謂的 node 重算。"),
"2026.06.07b":("剪枝定版 + 遮罩合併","P-2/3/4 收斂;PruneUnsafe + TurnOffSkip 合併成一個 bit-packed PruneMask(未來加剪枝只多一個 bit)。<b>較 06.07 −16% cyc/hc</b>。"),
"2026.06.08":("剖析工具(C# ≈ 持平)","settle-pass / cond 剖析器(DEBUG-only)。C# hot path native 幾乎不變。"),
"2026.06.09":("&&-條件重排 + 供電折入起步","SetNodeState 的 &&-子句依剖析選擇度重排(最常 false 的測試擺最前)。進入 .NET 11 時代。"),
"2026.06.09b":("供電跳過折入遮罩","把 supply-skip 折進 PruneMask,turn-off 入列檢查變一致、不需特別的供電守衛。"),
"2026.06.09c":("BFS/DFS 實驗 + 診斷","(native 與前版相同 → 沒改 C# hot path,屬實驗/診斷。)"),
"2026.06.09d":("小改","(native 相同 → 未動 C# hot path。)"),
"2026.06.09e":("小改","(native 相同 → 未動 C# hot path。)"),
"2026.06.11":("range-prune + 自我捕捉 locality ⭐","class-major 自動重編號 → 把剪枝遮罩查表變成 ID 區間比較;locality 鍵改成載入時「真實首次彈出順序」自我捕捉(那條<i>復活的</i> RCM 死路)。"),
"2026.06.12":("B1 兩節點成對路徑 ⭐","可證明只有兩個節點的導通群組就地解析(佔全部群組走訪的 77%),跳過 _groupBuf/_inGroup 機制。<b>native 變大(3,807→5,381)卻更快</b> —— 刪的是相依載入鏈,不是指令數。"),
"2026.06.18":(".NET 11 + 下降寫回拆分","turn-off 扇出改走載入時預先過濾的端點清單(低於剪枝邊界的 id 已剔除);runtime 移到 .NET 11。"),
"2026.06.19":("disasm 驅動 codegen 微優化","讀 JIT x64 找出的 5 個 bit-exact 優化:cls2/fast-path 的 NodeStates hoist、SetNodeState missed-CSE 修復、pop-loop 不變量 hoist、BFS transList lazy-load 消 spill。<b>native 5,579→5,314(更小)</b>。"),
}
MILE={"2026.05.31","2026.06.07b","2026.06.11","2026.06.12"}  # starred milestones for the chart markers

rows=[]
for v in order:
    rows.append(dict(v=v,date=locked[v]["Date"],lcyc=int(locked[v]["LockedCycPerHc"]),lhc=int(locked[v]["LockedHcS"]),
                     bhc=int(bt[v]["BoostTop3Avg"]),bmax=int(bt[v]["BoostMax"]),il=int(size[v]["IL"]),native=int(size[v]["Native"])))
first,last=rows[0],rows[-1]
cyc_red=100*(first["lcyc"]-last["lcyc"])/first["lcyc"]
hc_gain=100*(last["bhc"]-first["bhc"])/first["bhc"]
spd=last["bhc"]/first["bhc"]

def line_chart(values,color,w=920,h=270,pad=50,fmt=lambda x:f"{x:,}",mark=None):
    n=len(values);vmin=min(values);vmax=max(values);span=(vmax-vmin)or 1
    lo=vmin-span*0.10;hi=vmax+span*0.14
    X=lambda i:pad+i*(w-2*pad)/(n-1); Y=lambda val:h-pad-(val-lo)*(h-2*pad)/(hi-lo)
    # area fill under the line (firepower)
    area=f'{X(0):.1f},{Y(lo):.1f} '+" ".join(f"{X(i):.1f},{Y(v):.1f}" for i,v in enumerate(values))+f' {X(n-1):.1f},{Y(lo):.1f}'
    pts=" ".join(f"{X(i):.1f},{Y(v):.1f}" for i,v in enumerate(values))
    dots="";
    for i,v in enumerate(values):
        big = mark and order[i] in mark
        dots+=f'<circle cx="{X(i):.1f}" cy="{Y(v):.1f}" r="{4.6 if big else 3}" fill="{"#ffd24a" if big else color}" stroke="#0d1420" stroke-width="{1.5 if big else 0}"><title>{order[i]}: {fmt(v)}</title></circle>'
    grid=""
    for k in range(5):
        gv=lo+(hi-lo)*k/4;gy=Y(gv)
        grid+=f'<line x1="{pad}" y1="{gy:.1f}" x2="{w-pad}" y2="{gy:.1f}" stroke="#1d2942"/><text x="{pad-6}" y="{gy+4:.1f}" text-anchor="end" font-size="10" fill="#7f93b3">{fmt(int(gv))}</text>'
    xl=""
    for i in range(n):
        xl+=f'<text x="{X(i):.1f}" y="{h-pad+15:.1f}" text-anchor="end" font-size="9" fill="#7f93b3" transform="rotate(-42 {X(i):.1f} {h-pad+15:.1f})">{order[i].replace("2026.","")}</text>'
    return f'<svg viewBox="0 0 {w} {h}" width="100%" style="max-width:{w}px"><defs><linearGradient id="g{color[1:]}" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="{color}" stop-opacity="0.28"/><stop offset="1" stop-color="{color}" stop-opacity="0"/></linearGradient></defs>{grid}<polygon points="{area}" fill="url(#g{color[1:]})"/><polyline points="{pts}" fill="none" stroke="{color}" stroke-width="2.6"/>{dots}{xl}</svg>'

chart_cyc=line_chart([r["lcyc"] for r in rows],"#4ea1ff",mark=MILE)
chart_hc =line_chart([r["bhc"] for r in rows],"#39d98a",mark=MILE)
chart_nat=line_chart([r["native"] for r in rows],"#f0a35e")

# firepower: horizontal throughput growth bars
maxb=max(r["bhc"] for r in rows)
bars=""
for r in rows:
    w=100*r["bhc"]/maxb; mult=r["bhc"]/first["bhc"]; star="★" if r["v"] in MILE else ""
    bars+=f'<div class="barrow"><span class="bl mv">{r["v"]} {star}</span><span class="bt"><span class="bf" style="width:{w:.1f}%"></span></span><span class="bn">{r["bhc"]:,} <span class="mut">({mult:.2f}×)</span></span></div>'

# per-version narrative cards
prev=None; cards=""
for r in rows:
    d=DESC[r["v"]]; star="background:linear-gradient(90deg,#1c2c1a,#121c2c)" if r["v"] in MILE else ""
    dcy = "" if prev is None else (f'<span style="color:#39d98a">↓{prev["lcyc"]-r["lcyc"]:,}</span>' if r["lcyc"]<prev["lcyc"] else f'<span class="mut">≈</span>')
    dnat= "" if prev is None else (f'native {prev["native"]:,}→{r["native"]:,}')
    cards+=f'<div class="vc" style="{star}"><div class="vh"><span class="mv">{r["v"]}</span> <span class="mut">{r["date"]}</span> <b>{d[0]}</b></div><div class="vd">{d[1]}</div><div class="vm mut">cyc/hc {r["lcyc"]:,} {dcy} &nbsp;·&nbsp; {dnat} &nbsp;·&nbsp; boost {r["bhc"]:,} hc/s</div></div>'
    prev=r

# data table
trs="";prev=None
for r in rows:
    dnat="" if prev is None else (f'<span style="color:#39d98a">▼{prev["native"]-r["native"]}</span>' if r["native"]<prev["native"] else (f'<span style="color:#f06a6a">▲{r["native"]-prev["native"]}</span>' if r["native"]>prev["native"] else '<span class="mut">=</span>'))
    trs+=f'<tr><td class="mv">{r["v"]}</td><td class="mut">{r["date"]}</td><td class="num">{r["lcyc"]:,}</td><td class="num">{r["lhc"]:,}</td><td class="num">{r["bhc"]:,}</td><td class="num">{r["native"]:,} {dnat}</td><td class="num">{r["il"]}</td></tr>'
    prev=r

xtimes=42954552//last["bhc"]
HTML=f"""<!doctype html><html lang="zh-Hant"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>AprVisual S1 — 效能改善歷史</title><style>
 body{{margin:0;background:#0d1420;color:#cdd9ec;font:15px/1.65 -apple-system,Segoe UI,Roboto,"Microsoft JhengHei",sans-serif}}
 .wrap{{max-width:1000px;margin:0 auto;padding:0 20px 90px}}
 .hero{{background:radial-gradient(120% 140% at 0% 0%,#16335a 0%,#0d1420 55%);border-bottom:1px solid #23314a;padding:42px 0 30px;margin-bottom:8px}}
 .hero .wrap{{padding-bottom:0}} h1{{font-size:27px;margin:0 0 6px;color:#fff}}
 h2{{font-size:19px;margin:40px 0 12px;color:#fff;border-bottom:1px solid #23314a;padding-bottom:6px}}
 .mut{{color:#7f93b3}} .num{{text-align:right;font-variant-numeric:tabular-nums}}
 .kpi{{display:flex;gap:14px;flex-wrap:wrap;margin-top:22px}}
 .kpi .k{{flex:1;min-width:150px;background:#121c2c;border:1px solid #23314a;border-radius:12px;padding:14px 16px}}
 .k .big{{font-size:30px;font-weight:800;color:#fff;line-height:1.1}} .k .lab{{font-size:12px;color:#7f93b3;margin-top:4px}}
 .accent{{color:#4ea1ff}} .grn{{color:#39d98a}}
 .card{{background:#121c2c;border:1px solid #23314a;border-radius:12px;padding:16px 18px;margin:14px 0}}
 table{{width:100%;border-collapse:collapse;font-size:13px}} th,td{{padding:6px 8px;border-bottom:1px solid #1c2942;text-align:left}}
 th{{color:#9fc0ff;font-weight:600}} .mv{{font-family:ui-monospace,Consolas,monospace;color:#e7c98a;white-space:nowrap}}
 code{{background:#1b2740;padding:1px 5px;border-radius:4px;font-size:12.5px}}
 .note{{background:#15233a;border-left:3px solid #4ea1ff;padding:10px 14px;border-radius:0 8px 8px 0;margin:12px 0}} .warn{{border-left-color:#f0a35e;background:#241d12}}
 .barrow{{display:flex;align-items:center;gap:10px;margin:3px 0;font-size:12px}} .bl{{width:96px;flex:none}} .bt{{flex:1;background:#16223a;border-radius:5px;height:15px;overflow:hidden}}
 .bf{{display:block;height:100%;background:linear-gradient(90deg,#2c6fd6,#39d98a)}} .bn{{width:150px;text-align:right;flex:none;font-variant-numeric:tabular-nums}}
 .vc{{background:#121c2c;border:1px solid #23314a;border-radius:10px;padding:11px 14px;margin:8px 0}}
 .vh{{margin-bottom:3px}} .vd{{font-size:13.5px}} .vm{{font-size:12px;margin-top:5px}}
</style></head><body>

<div class="hero"><div class="wrap">
<h1>🔥 AprVisual S1 — 效能改善歷史</h1>
<p class="mut">switch-level NES(2A03+2C02)模擬器 · 18 個發布版(2026.05.30 → 2026.06.19)· 全程 <b>bit-exact</b>(checksum <code>0x9174E19D961CB6E5</code>)· Ryzen 7 3700X (Zen2)</p>
<div class="kpi">
 <div class="k"><div class="big grn">{spd:.2f}×</div><div class="lab">更快(throughput,首版→末版)</div></div>
 <div class="k"><div class="big accent">−{cyc_red:.0f}%</div><div class="lab">每 half-cycle 的 CPU cycle(鎖頻)</div></div>
 <div class="k"><div class="big">{first['bhc']:,}<span style="font-size:16px;color:#7f93b3"> → </span>{last['bhc']:,}</div><div class="lab">hc/s(boost)</div></div>
 <div class="k"><div class="big">18</div><div class="lab">發布版,版版 bit-exact</div></div>
</div></div></div>

<div class="wrap">

<div class="note">一句話:在<b>完全不改變輸出(每版 checksum 相同、模擬工作量完全一致)</b>的前提下,引擎被連續打磨成 <b>{spd:.2f} 倍快</b>、
每個 half-cycle 的 CPU 成本<b>砍掉約一半</b>。下面用「實際 CPU cycle」(抗熱噪)逐版量化,並說明每一版改了什麼。</div>

<h2>📉 Hotpath cycles/hc(越低越好 · 鎖頻 3.6GHz · ★=里程碑)</h2>
<div class="card">{chart_cyc}</div>

<h2>📈 Throughput 成長(hc/s · 一路往右上)</h2>
<div class="card">{bars}</div>
<div class="card">{chart_hc}</div>
<p class="mut">boost hc/s = <b>涼機、<code>--pin</code>、每版 5 次去最低 2、取前 3 平均</b>(乾淨,interleaved 均攤熱漂移)。歷代峰值最高:<b>06.19 = {last['bmax']:,} hc/s</b>。</p>

<h2>🛠 每一版改了什麼</h2>
{cards}

<h2>🧱 ProcessQueue 機械碼大小(bytes)— 哪些版真的動了 C#</h2>
<div class="card">{chart_nat}</div>
<div class="note warn"><b>關鍵洞察:碼變小 ≠ 變快。</b> 最早 05.30 的 native 最小(2,985 B)卻最慢;06.12 的 B1 讓 native <b>變大</b>(3,807→5,381 B)卻更快(刪的是相依載入鏈)。
而 <b>06.09b/c/d/e 的 native 完全相同(3,860 B)</b> → 這幾版沒動 C# hot path(Rust/診斷/小改),它們之間的 cyc/hc 差異純屬量測噪音。</div>

<h2>🎯 量測噪音底線(誠實揭露)</h2>
<p>06.09b/c/d/e 的 C# hot path <b>位元相同</b>(native 全 = 3,860 B),鎖頻下卻量到 cyc/hc <b>33,661 ~ 35,951</b>(約 7% 散布)。
這給出本量測的<b>相鄰版本噪音底線 ≈ ±5-7%</b> —— memory-bound code 即使鎖頻,cycle 仍受系統干擾/快取狀態影響。
<b>判讀原則:相鄰版差異在 ~5% 內視為持平,只有大跳點(★)是真改善。</b></p>

<h2>✅ 18 vs 19:有退步嗎?沒有</h2>
<p>06.19 全是 bit-exact codegen 微優化,native <b>5,579→5,314 B(更小)</b>。專門的<b>鎖頻 20 輪 interleaved-paired</b>:median 18=32,317 / 19=32,205 cyc/hc,
<b>19 反而快 0.35%、paired 12/20 勝</b> → 非退步,中性偏微正。
<b>兩個獨立方法一致確認</b>:鎖頻 cycle paired(19 −0.35%、12/20)+ 涼機 boost wall-clock 20 輪 paired(19 +0.61%、13/20)+ 本表的涼機 top-3 平均(18=137,226 / <b>19=138,708,+1.08%</b>)。三者都說 19 略快。</p>

<h2>📋 完整數據</h2>
<div class="card" style="overflow-x:auto"><table>
<tr><th>版本</th><th>日期</th><th class="num">cyc/hc 鎖頻</th><th class="num">hc/s 鎖頻</th><th class="num">hc/s boost</th><th class="num">native (B)</th><th class="num">IL</th></tr>
{trs}</table></div>

<h2>🔬 方法論與限制</h2>
<ul>
<li><b>cycle</b>:<code>QueryProcessCycleTime</code>(Win32,程序實際 CPU cycle,頻率/排程無關)+ 鎖頻 3.6GHz(<code>PERFBOOSTMODE 0</code>)消掉「memory latency 換算 cycle 隨頻率變」的二階效應。</li>
<li><b>load 扣除</b>:<code>cyc/hc=(cycles@400k−cycles@40k)/360000</code>,隔離純 hot-loop(netlist 組裝/power-on 與 hc 無關)。</li>
<li><b>bit-exact 基準</b>:18 版在 400k 同 checksum → 工作量(事件數)完全相同,cyc/hc 差異純為「每事件成本」優化。</li>
<li><b>噪音</b>:鎖頻 cyc/hc 相鄰 ±5-7%(見上),單機單顆 Zen2。boost hc/s 為涼機、<code>--pin</code>、每版 5 次去最低 2 取前 3 平均(interleaved,已壓掉熱漂移)。</li>
<li><b>未納入</b>:Rust 引擎(本報告只測 C# 發布二進位);per-instruction IBS load 延遲(uProf IBS 不歸因 .NET JIT 程式碼)。</li>
<li><b>距即時</b>:NES NTSC 需 42,954,552 hc/s → 末版仍約 <b>{xtimes}×</b> 之遙(這正是持續優化的理由)。</li>
</ul>
<p class="mut" style="margin-top:26px">cycle-level 紀錄 · 全程 bit-exact · 產生於 2026-06-19 · 量測腳本 temp/measure_versions.ps1 + QueryProcessCycleTime 鎖頻量測</p>
</div></body></html>"""
p=os.path.join(OUT,"perf-history.html")
open(p,"w",encoding="utf-8").write(HTML)
print("WROTE",p,f"({len(HTML)} bytes)  speedup {spd:.2f}x  cyc -{cyc_red:.1f}%")
