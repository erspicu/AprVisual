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

def key_of(t):
    return f"{t['suite']}/{t['rom']}".replace("/", "__")

cat = json.load(open(CATALOG, encoding="utf-8"))
rows, engine_ver = [], ""
counts = {"pass": 0, "fail": 0, "timeout": 0, "pending": 0}

os.makedirs(os.path.join(REPORT_DIR, "screenshots"), exist_ok=True)

for t in cat["tests"]:
    k = key_of(t)
    jpath = os.path.join(OUT_DIR, "results", k + ".json")
    shot_src = os.path.join(OUT_DIR, "screenshots", t["suite"].replace("/", os.sep), t["rom"].replace(".nes", ".png"))
    shot_rel = f"screenshots/{t['suite']}/{t['rom'].replace('.nes', '.png')}"

    row = {"suite": t["suite"], "rom": t["rom"], "class": t["class"],
           "status": "pending", "exit_code": -1, "result_text": "", "screenshot": "",
           "detection": "", "frames": 0, "simSeconds": 0, "wallSeconds": 0, "resetCount": 0}
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
                       resetCount=r.get("resetCount", 0))
            engine_ver = r.get("engineVersion", engine_ver) or engine_ver
        except Exception as e:
            row["result_text"] = f"(result json unreadable: {e})"
    if os.path.isfile(shot_src):
        dst = os.path.join(REPORT_DIR, "screenshots", t["suite"].replace("/", os.sep), t["rom"].replace(".nes", ".png"))
        os.makedirs(os.path.dirname(dst), exist_ok=True)
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
.footer{text-align:center;color:var(--dim);font-size:.73rem;margin-top:2rem;padding-top:1rem;border-top:1px solid var(--border)}
</style>
</head>
<body>
<div class="header">
  <h1>AprVisual.S1 Switch-Level Test Report</h1>
  <div class="sub">transistor/switch-level NES simulation (Visual2A03 + Visual2C02 netlists) &mdash; every result below is computed by
  propagating individual transistor state changes, not by a behavioral emulator.</div>
  <div class="sub" id="build-info"></div>
</div>
<div class="stats">
  <div class="stat pass"><div class="num" id="n-pass">0</div><div class="lbl">Passed</div></div>
  <div class="stat fail"><div class="num" id="n-fail">0</div><div class="lbl">Failed</div></div>
  <div class="stat timeout"><div class="num" id="n-to">0</div><div class="lbl">Timeout</div></div>
  <div class="stat pending"><div class="num" id="n-pend">0</div><div class="lbl">Pending</div></div>
  <div class="stat"><div class="num" id="n-total">0</div><div class="lbl">Total</div></div>
</div>
<div class="progress-wrap">
  <div class="progress-bar"><div class="p" id="bar-p"></div><div class="f" id="bar-f"></div><div class="t" id="bar-t"></div></div>
  <div class="progress-text" id="progress-text"></div>
</div>
<div class="note"><strong>Scope:</strong> 83 NROM / NTSC test ROMs judged via the blargg <code>$6000</code> protocol
 (classes A, A-r) or on-screen CRC (class C). Screen-text ROMs (46, class B) are a later phase; apu_mixer (4) and apu_reset (6) are excluded/deferred — auditory verdict / slow APU reset cycles.
 Detection runs once per simulated frame; a single test takes minutes-to-an-hour of wall time at switch level.</div>
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
<div class="footer">Generated by AprVisual.S1 test runner (tools/testrom/) &mdash;
 <a href="https://github.com/erspicu/AprVisual">github.com/erspicu/AprVisual</a></div>
<script>
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
if(t.frames)html+='<div class="meta">'+t.frames+' frames \\u00b7 sim '+t.simSeconds.toFixed(1)+'s \\u00b7 wall '+Math.round(t.wallSeconds/60)+'min'+(t.resetCount?' \\u00b7 '+t.resetCount+' reset':'')+'</div>';
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
