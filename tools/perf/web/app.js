"use strict";
const PLATFORMS = ["x64", "arm64"];
const $ = (id) => document.getElementById(id);
const esc = (s) => String(s == null ? "" : s).replace(/[&<>]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;" }[c]));
const fmt = (n) => (n == null ? "—" : Number(n).toLocaleString("en-US"));

function lineChart(rows, pick, opts) {
  opts = opts || {};
  const w = 920, h = 270, pad = 54;
  const vals = rows.map(pick).map(v => v == null ? null : Number(v));
  const ok = vals.filter(v => v != null);
  if (!ok.length) return '<div class="mut">無此指標資料</div>';
  const vmin = Math.min(...ok), vmax = Math.max(...ok), span = (vmax - vmin) || 1;
  const lo = vmin - span * 0.12, hi = vmax + span * 0.10;
  const n = rows.length;
  const X = (i) => pad + i * (w - 2 * pad) / (n - 1);
  const Y = (v) => h - pad - (v - lo) * (h - 2 * pad) / (hi - lo);
  const color = opts.color || "#39d98a";
  let grid = "";
  for (let k = 0; k < 5; k++) { const gv = lo + (hi - lo) * k / 4, gy = Y(gv); grid += `<line x1="${pad}" y1="${gy.toFixed(1)}" x2="${w - pad}" y2="${gy.toFixed(1)}" stroke="#1d2942"/><text x="${pad - 6}" y="${(gy + 4).toFixed(1)}" text-anchor="end" font-size="10" fill="#7f93b3">${fmt(Math.round(gv))}</text>`; }
  const pts = vals.map((v, i) => v == null ? null : `${X(i).toFixed(1)},${Y(v).toFixed(1)}`).filter(Boolean).join(" ");
  let dots = "";
  vals.forEach((v, i) => { if (v == null) return; const big = opts.mark && rows[i].milestone; dots += `<circle cx="${X(i).toFixed(1)}" cy="${Y(v).toFixed(1)}" r="${big ? 4.6 : 2.8}" fill="${big ? "#ffd24a" : color}" stroke="#0d1420" stroke-width="${big ? 1.4 : 0}"><title>${esc(rows[i].version)}: ${fmt(v)}</title></circle>`; });
  let xl = "";
  rows.forEach((r, i) => { xl += `<text x="${X(i).toFixed(1)}" y="${(h - pad + 15).toFixed(1)}" text-anchor="end" font-size="9" fill="#7f93b3" transform="rotate(-42 ${X(i).toFixed(1)} ${(h - pad + 15).toFixed(1)})">${esc(r.version.replace("2026.", ""))}</text>`; });
  return `<svg viewBox="0 0 ${w} ${h}">${grid}<polyline points="${pts}" fill="none" stroke="${color}" stroke-width="2.6"/>${dots}${xl}</svg>`;
}

function render(doc) {
  const V = doc.versions, first = V[0].metrics, last = V[V.length - 1].metrics;
  const spd = (last.hc_s_best3 / first.hc_s_best3);
  const be = V.filter(v => v.metrics.bit_exact).length;
  $("metaline").innerHTML = `${esc(doc.cpu)} · 模式 <b>${esc(doc.mode)}</b> · golden <code>${esc(doc.golden_checksum)}</code> · 產生於 ${esc(doc.generated)} · ${V.length} 版`;
  $("kpi").innerHTML = `
    <div class="k"><div class="big grn">${spd.toFixed(2)}×</div><div class="lab">引擎演進(首→末版)</div></div>
    <div class="k"><div class="big">${fmt(last.hc_s_best3)}</div><div class="lab">峰值 hc/s(${esc(V[V.length-1].version)})</div></div>
    <div class="k"><div class="big">~${Math.round(doc.realtime_hc_s / last.hc_s_best3)}×</div><div class="lab">距 NES 即時(${fmt(doc.realtime_hc_s)} hc/s)</div></div>
    <div class="k"><div class="big ${be===V.length?'grn':'bad'}">${be}/${V.length}</div><div class="lab">版版 bit-exact</div></div>`;

  $("chart-boost").innerHTML = lineChart(V, v => v.metrics.hc_s_best3, { color: "#39d98a", mark: true });
  $("chart-cyc").innerHTML = lineChart(V, v => v.metrics.cyc_per_hc_locked, { color: "#f0a35e" });

  const maxb = Math.max(...V.map(v => v.metrics.hc_s_best3));
  $("bars").innerHTML = V.map(v => {
    const m = v.metrics;
    return `<div class="barrow"><span class="bl mv">${esc(v.version)} ${v.milestone ? "★" : ""}</span><span class="bt"><span class="bf" style="width:${(100 * m.hc_s_best3 / maxb).toFixed(1)}%"></span></span><span class="bn">${fmt(m.hc_s_best3)} <span class="mut">(${(m.hc_s_best3 / first.hc_s_best3).toFixed(2)}×)</span></span></div>`;
  }).join("");

  let prev = null;
  $("cards").innerHTML = V.map(v => {
    const m = v.metrics;
    let d = "";
    if (prev) d = m.hc_s_best3 > prev * 1.012 ? `<span class="grn">+${(100 * (m.hc_s_best3 - prev) / prev).toFixed(1)}%</span>` : `<span class="mut">≈持平</span>`;
    prev = m.hc_s_best3;
    return `<div class="vc ${v.milestone ? "mile" : ""}"><div class="vh"><span class="mv">${esc(v.version)}</span> <span class="mut">${esc(v.date)} · ${esc(v.tfm)}${v.commit ? " · " + esc(v.commit) : ""}</span> <b>${esc(v.title)}</b></div><div class="vd">${esc(v.desc)}</div><div class="vm">${fmt(m.hc_s_best3)} hc/s ${d} · cyc/hc ${fmt(m.cyc_per_hc_locked)} · native ${fmt(m.native_size)} B · ${m.bit_exact ? "bit-exact ✓" : '<span class="bad">checksum 不符 ✗</span>'}</div></div>`;
  }).join("");

  $("table").innerHTML = `<tr><th>版本</th><th>日期</th><th class="num">hc/s</th><th class="num">vs首版</th><th class="num">cyc/hc</th><th class="num">CV%</th><th class="num">IL</th><th class="num">native</th><th>TFM</th><th>checksum</th></tr>` +
    V.map(v => { const m = v.metrics; return `<tr><td class="mv">${esc(v.version)}${v.milestone ? " ★" : ""}</td><td class="mut">${esc(v.date)}</td><td class="num">${fmt(m.hc_s_best3)}</td><td class="num">${(m.hc_s_best3 / first.hc_s_best3).toFixed(2)}×</td><td class="num">${fmt(m.cyc_per_hc_locked)}</td><td class="num">${m.hc_s_cv_pct}</td><td class="num">${fmt(m.il_size)}</td><td class="num">${fmt(m.native_size)}</td><td class="mut">${esc(v.tfm)}</td><td class="mut">${m.bit_exact ? "✓" : '<span class="bad">✗</span>'}</td></tr>`; }).join("");

  const base = location.href.replace(/[^/]*$/, "");
  const p = doc.platform;
  $("access").innerHTML = `
    <b>① 純 JSON</b> <code><a href="${p}/data.json">${base}${p}/data.json</a></code>
    <b>② PHP API</b>(帶 CORS)
    <code><a href="api.php?platform=${p}">api.php?platform=${p}</a>            # 整份</code>
    <code>api.php?platform=${p}&amp;version=${esc(V[V.length-1].version)}   # 單版</code>
    <code><a href="api.php?platform=${p}&metric=hc_s_best3">api.php?platform=${p}&amp;metric=hc_s_best3</a>   # 畫圖用 [{version,value}]</code>
    <code><a href="api.php?platform=${p}&latest=1">api.php?platform=${p}&amp;latest=1</a>          # 最新摘要</code>
    <code><a href="api.php?platform=${p}&format=csv">api.php?platform=${p}&amp;format=csv</a>        # CSV 匯出</code>
    <b>③ HTML</b> 本頁:切換平台用上方分頁,或網址加 <span class="inl">#${p}</span>`;

  $("footer").innerHTML = `AprVisual 各版效能檢測 · 全程 bit-exact · 資料 schema v${doc.schema} · ${esc(doc.generated)}`;
}

async function loadPlatform(p) {
  document.querySelectorAll(".tab").forEach(t => t.classList.toggle("on", t.dataset.p === p));
  location.hash = p;
  $("content").hidden = true; $("status").textContent = "載入中…";
  try {
    const res = await fetch(`${p}/data.json?_=${Date.now()}`, { cache: "no-store" });
    if (!res.ok) throw new Error(res.status);
    const doc = await res.json();
    render(doc);
    $("status").textContent = ""; $("content").hidden = false;
  } catch (e) {
    $("status").innerHTML = p === "arm64"
      ? "🚧 arm64(Raspberry Pi 5)區資料尚未產生 —— Pi 環境就緒後補上。"
      : `⚠ 載入 ${esc(p)}/data.json 失敗(${esc(e.message)})。需透過 HTTP(非 file://)開啟。`;
  }
}

document.querySelectorAll(".tab").forEach(t => t.addEventListener("click", () => loadPlatform(t.dataset.p)));
const init = PLATFORMS.includes(location.hash.slice(1)) ? location.hash.slice(1) : "x64";
loadPlatform(init);
