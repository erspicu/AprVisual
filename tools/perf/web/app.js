"use strict";
const PLATFORMS = ["x64", "arm64"];
const $ = (id) => document.getElementById(id);
const esc = (s) => String(s == null ? "" : s).replace(/[&<>]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;" }[c]));
const fmt = (n) => (n == null ? "—" : Number(n).toLocaleString("en-US"));

const I18N = {
  zh: {
    htmlLang: "zh-Hant",
    title: "🔥 AprVisual S1 — 各 release 效能檢測",
    sub: "switch-level NES(2A03+2C02)模擬器 · 逐版效能歷史 · 全程 <b>bit-exact</b>",
    hBoost: "📈 吞吐量 boost(hc/s · 越高越好 · ★=里程碑)",
    hCyc: "⏱ 熱路徑週期(cyc/hc · 鎖頻 · 越低越好)",
    hChanges: "🛠 每一版改了什麼", hTable: "📋 完整數據", hAccess: "🔌 存取方式",
    kEvo: "引擎演進(首→末版)", kPeak: "峰值 hc/s", kRt: "距 NES 即時", kBe: "版版 bit-exact",
    meta: (d) => `${esc(d.cpu)} · 模式 <b>${esc(d.mode)}</b> · golden <code>${esc(d.golden_checksum)}</code> · 產生於 ${esc(d.generated)} · ${d.versions.length} 版`,
    rtUnit: (x) => `~${x}× 即時`,
    th: ["版本", "日期", "hc/s", "vs首版", "cyc/hc", "CV%", "IL", "native", "TFM", "checksum"],
    flat: "≈持平", bitok: "bit-exact ✓", bitbad: '<span class="bad">checksum 不符 ✗</span>',
    loading: "載入中…",
    arm64soon: "🚧 arm64(Raspberry Pi 5)區資料尚未產生 —— Pi 環境就緒後補上。",
    loadfail: (p, e) => `⚠ 載入 ${esc(p)}/data.json 失敗(${esc(e)})。需透過 HTTP(非 file://)開啟。`,
    acc: { json: "純 JSON", api: "PHP API(帶 CORS)", full: "整份", ver: "單版", metric: "畫圖用 [{version,value}]", latest: "最新摘要", csv: "CSV 匯出", html: "HTML", htmlDesc: "本頁:切換平台用上方分頁,或網址加" },
    footer: (d) => `AprVisual 各版效能檢測 · 全程 bit-exact · 資料 schema v${d.schema} · ${esc(d.generated)}`,
  },
  en: {
    htmlLang: "en",
    title: "🔥 AprVisual S1 — per-release performance",
    sub: "switch-level NES (2A03+2C02) simulator · per-version history · <b>bit-exact</b> throughout",
    hBoost: "📈 boost throughput (hc/s · higher = better · ★ = milestone)",
    hCyc: "⏱ hot-path cycles (cyc/hc · clock-locked · lower = better)",
    hChanges: "🛠 what each version changed", hTable: "📋 full data", hAccess: "🔌 access methods",
    kEvo: "engine evolution (first→last)", kPeak: "peak hc/s", kRt: "from NES real-time", kBe: "all bit-exact",
    meta: (d) => `${esc(d.cpu)} · mode <b>${esc(d.mode)}</b> · golden <code>${esc(d.golden_checksum)}</code> · generated ${esc(d.generated)} · ${d.versions.length} versions`,
    rtUnit: (x) => `~${x}× real-time`,
    th: ["version", "date", "hc/s", "vs first", "cyc/hc", "CV%", "IL", "native", "TFM", "checksum"],
    flat: "≈flat", bitok: "bit-exact ✓", bitbad: '<span class="bad">checksum mismatch ✗</span>',
    loading: "loading…",
    arm64soon: "🚧 arm64 (Raspberry Pi 5) data not produced yet — coming once the Pi is ready.",
    loadfail: (p, e) => `⚠ failed to load ${esc(p)}/data.json (${esc(e)}). Serve over HTTP (not file://).`,
    acc: { json: "raw JSON", api: "PHP API (CORS)", full: "full", ver: "single version", metric: "for charts [{version,value}]", latest: "latest summary", csv: "CSV export", html: "HTML", htmlDesc: "this page — switch platform with the tabs above, or add" },
    footer: (d) => `AprVisual per-release performance · bit-exact throughout · data schema v${d.schema} · ${esc(d.generated)}`,
  },
};

const state = { lang: localStorage.getItem("apv_lang") || "zh", platform: "x64", doc: null };
const T = () => I18N[state.lang];
const vtitle = (v) => state.lang === "en" ? (v.title_en || v.title) : v.title;
const vdesc = (v) => state.lang === "en" ? (v.desc_en || v.desc) : v.desc;

function lineChart(rows, pick, opts) {
  opts = opts || {};
  const w = 920, h = 270, pad = 54;
  const vals = rows.map(pick).map(v => v == null ? null : Number(v));
  const ok = vals.filter(v => v != null);
  if (!ok.length) return '<div class="mut">—</div>';
  const vmin = Math.min(...ok), vmax = Math.max(...ok), span = (vmax - vmin) || 1;
  const lo = vmin - span * 0.12, hi = vmax + span * 0.10, n = rows.length;
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

function applyStatic() {
  const t = T();
  document.documentElement.lang = t.htmlLang;
  $("title").innerHTML = t.title;
  $("sub").innerHTML = t.sub;
  $("h-boost").textContent = t.hBoost; $("h-cyc").textContent = t.hCyc;
  $("h-changes").textContent = t.hChanges; $("h-table").textContent = t.hTable; $("h-access").textContent = t.hAccess;
  document.querySelectorAll(".lng").forEach(b => b.classList.toggle("on", b.dataset.l === state.lang));
  document.querySelectorAll(".tab").forEach(b => b.classList.toggle("on", b.dataset.p === state.platform));
}

function render(doc) {
  const t = T(), V = doc.versions, first = V[0].metrics, last = V[V.length - 1].metrics;
  const spd = last.hc_s_best3 / first.hc_s_best3, be = V.filter(v => v.metrics.bit_exact).length;
  $("metaline").innerHTML = t.meta(doc);
  $("kpi").innerHTML = `
    <div class="k"><div class="big grn">${spd.toFixed(2)}×</div><div class="lab">${t.kEvo}</div></div>
    <div class="k"><div class="big">${fmt(last.hc_s_best3)}</div><div class="lab">${t.kPeak} (${esc(V[V.length-1].version)})</div></div>
    <div class="k"><div class="big">~${Math.round(doc.realtime_hc_s / last.hc_s_best3)}×</div><div class="lab">${t.kRt}</div></div>
    <div class="k"><div class="big ${be===V.length?'grn':'bad'}">${be}/${V.length}</div><div class="lab">${t.kBe}</div></div>`;

  $("chart-boost").innerHTML = lineChart(V, v => v.metrics.hc_s_best3, { color: "#39d98a", mark: true });
  $("chart-cyc").innerHTML = lineChart(V, v => v.metrics.cyc_per_hc_locked, { color: "#f0a35e" });

  const maxb = Math.max(...V.map(v => v.metrics.hc_s_best3));
  $("bars").innerHTML = V.map(v => `<div class="barrow"><span class="bl mv">${esc(v.version)} ${v.milestone ? "★" : ""}</span><span class="bt"><span class="bf" style="width:${(100 * v.metrics.hc_s_best3 / maxb).toFixed(1)}%"></span></span><span class="bn">${fmt(v.metrics.hc_s_best3)} <span class="mut">(${(v.metrics.hc_s_best3 / first.hc_s_best3).toFixed(2)}×)</span></span></div>`).join("");

  let prev = null;
  $("cards").innerHTML = V.map(v => {
    const m = v.metrics; let d = "";
    if (prev) d = m.hc_s_best3 > prev * 1.012 ? `<span class="grn">+${(100 * (m.hc_s_best3 - prev) / prev).toFixed(1)}%</span>` : `<span class="mut">${t.flat}</span>`;
    prev = m.hc_s_best3;
    return `<div class="vc ${v.milestone ? "mile" : ""}"><div class="vh"><span class="mv">${esc(v.version)}</span> <span class="mut">${esc(v.date)} · ${esc(v.tfm)}${v.commit ? " · " + esc(v.commit) : ""}</span> <b>${esc(vtitle(v))}</b></div><div class="vd">${esc(vdesc(v))}</div><div class="vm">${fmt(m.hc_s_best3)} hc/s ${d} · cyc/hc ${fmt(m.cyc_per_hc_locked)} · native ${fmt(m.native_size)} B · ${m.bit_exact ? t.bitok : t.bitbad}</div></div>`;
  }).join("");

  $("table").innerHTML = `<tr>${t.th.map((h, i) => `<th class="${i >= 2 && i <= 7 ? "num" : ""}">${esc(h)}</th>`).join("")}</tr>` +
    V.map(v => { const m = v.metrics; return `<tr><td class="mv">${esc(v.version)}${v.milestone ? " ★" : ""}</td><td class="mut">${esc(v.date)}</td><td class="num">${fmt(m.hc_s_best3)}</td><td class="num">${(m.hc_s_best3 / first.hc_s_best3).toFixed(2)}×</td><td class="num">${fmt(m.cyc_per_hc_locked)}</td><td class="num">${m.hc_s_cv_pct}</td><td class="num">${fmt(m.il_size)}</td><td class="num">${fmt(m.native_size)}</td><td class="mut">${esc(v.tfm)}</td><td class="mut">${m.bit_exact ? "✓" : '<span class="bad">✗</span>'}</td></tr>`; }).join("");

  const base = location.href.replace(/[#?].*$/, "").replace(/[^/]*$/, ""), p = doc.platform, a = t.acc;
  $("access").innerHTML = `
    <b>① ${a.json}</b>
    <code><a href="${p}/data.json">${esc(base)}${p}/data.json</a></code>
    <b>② ${a.api}</b>
    <code><a href="api.php?platform=${p}">api.php?platform=${p}</a>   # ${a.full}</code>
    <code>api.php?platform=${p}&amp;version=${esc(V[V.length-1].version)}   # ${a.ver}</code>
    <code><a href="api.php?platform=${p}&metric=hc_s_best3">api.php?platform=${p}&amp;metric=hc_s_best3</a>   # ${a.metric}</code>
    <code><a href="api.php?platform=${p}&latest=1">api.php?platform=${p}&amp;latest=1</a>   # ${a.latest}</code>
    <code><a href="api.php?platform=${p}&format=csv">api.php?platform=${p}&amp;format=csv</a>   # ${a.csv}</code>
    <b>③ ${a.html}</b> ${a.htmlDesc} <span class="inl">#${p}</span>`;

  $("footer").innerHTML = t.footer(doc);
}

async function loadPlatform(p) {
  state.platform = p; location.hash = p;
  document.querySelectorAll(".tab").forEach(b => b.classList.toggle("on", b.dataset.p === p));
  $("content").hidden = true; $("status").textContent = T().loading;
  try {
    const res = await fetch(`${p}/data.json?_=${Date.now()}`, { cache: "no-store" });
    if (!res.ok) throw new Error(res.status);
    state.doc = await res.json();
    render(state.doc);
    $("status").textContent = ""; $("content").hidden = false;
  } catch (e) {
    state.doc = null;
    $("status").innerHTML = p === "arm64" ? T().arm64soon : T().loadfail(p, e.message);
  }
}

function setLang(l) {
  state.lang = l; localStorage.setItem("apv_lang", l);
  applyStatic();
  if (state.doc) render(state.doc);
  else $("status").innerHTML = state.platform === "arm64" ? T().arm64soon : $("status").innerHTML;
}

document.querySelectorAll(".lng").forEach(b => b.addEventListener("click", () => setLang(b.dataset.l)));
document.querySelectorAll(".tab").forEach(b => b.addEventListener("click", () => loadPlatform(b.dataset.p)));
state.platform = PLATFORMS.includes(location.hash.slice(1)) ? location.hash.slice(1) : "x64";
applyStatic();
loadPlatform(state.platform);
