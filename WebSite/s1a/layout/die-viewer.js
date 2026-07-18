/* die-viewer.js — live, zoomable, interactive die-layout viewer for the S1a study.
 *
 * Renders a Visual6502-style silicon layout (polygons from segdefs) and overlays the
 * nodes a mechanism (M1–M7) flags, so an abstract census number becomes a place on the
 * real die. One shared component; each Mx page differs only in the highlight data.
 *
 * Rendering: the polygons are VECTORS, so we re-rasterise them at the current zoom on
 * every view change (with viewport culling) — crisp at any magnification. Hit-testing uses
 * a separate fixed-res offscreen buffer (Visual6502's idea; ours encodes the node id in a
 * full 24-bit colour, since the 12-bit original maxes at 4096 nodes). During a zoom gesture
 * a cached bitmap is blitted (instant), then the vectors redraw sharp on settle.
 *
 * Chip switch: a page can offer both dies (data-chips="2C02,2A03"); the other chip's layout
 * + node files are lazy-loaded on demand and cached. A loading overlay covers the initial
 * build and each switch.
 *
 * Layout data (segdefs) is CC-BY-NC-SA (Visual 2A03/2C02 / Visual6502) — keep attribution.
 * Highlights: a window.DV_HIGHLIGHTS global (loaded via <script>, so file:// preview works).
 */
(function () {
  "use strict";

  var LAYER_COLORS = ["#3a2f4a", "#2f3a46", "#46372f", "#4a4636", "#39424e", "#2a2a33"];
  var HIT_PX = 3000;
  var MIN_SCALE_MULT = 0.9, MAX_SCALE_MULT = 90;
  var FAST_MIN_PX = 1.3, SETTLE_MS = 150;
  var CACHE = { L: {}, H: {} };     // chip -> LAYOUT ; (mech|chip) -> HIGHLIGHTS
  var CHIP_LABEL = { "2C02": "PPU (2C02)", "2A03": "CPU (2A03)" };

  function el(tag, cls) { var e = document.createElement(tag); if (cls) e.className = cls; return e; }
  function raf2(fn) { requestAnimationFrame(function () { requestAnimationFrame(fn); }); }
  function loadScript(src, cb) {
    var s = document.createElement("script"); s.src = src;
    s.onload = cb; s.onerror = function () { cb(new Error("failed: " + src)); };
    document.head.appendChild(s);
  }

  function injectCSS() {
    if (document.getElementById("dv-css")) return;
    var s = el("style"); s.id = "dv-css";
    s.textContent =
      ".dv-loading{position:absolute;inset:0;z-index:15;display:flex;flex-direction:column;align-items:center;" +
      "justify-content:center;gap:13px;background:rgba(11,10,15,.9);color:#c8bcd8;font-size:13px;transition:opacity .3s;}" +
      ".dv-loading.hide{opacity:0;pointer-events:none;}" +
      ".dv-spin{width:34px;height:34px;border:3px solid #2e2634;border-top-color:#c792ea;border-radius:50%;animation:dv-rot .8s linear infinite;}" +
      "@keyframes dv-rot{to{transform:rotate(360deg);}}" +
      ".dv-chips{display:inline-flex;gap:4px;margin-left:2px;}" +
      ".dv-chipbtn{background:#171420;color:#968ba1;border:1px solid #2e2634;border-radius:6px;padding:3px 10px;cursor:pointer;font-size:12px;}" +
      ".dv-chipbtn.on{background:#221a2e;border-color:#c792ea;color:#e8d9f7;font-weight:600;}" +
      ".dv-chipbtn:hover{color:#dcd7e3;}";
    document.head.appendChild(s);
  }

  function DieViewer(host) {
    this.host = host; injectCSS();
    this.mech = host.getAttribute("data-mechanism") || "";
    this.chips = (host.getAttribute("data-chips") || "").split(",").map(function (s) { return s.trim(); }).filter(Boolean);
    // cache whatever was loaded via <script> up front
    if (window.LAYOUT) CACHE.L[window.LAYOUT.chip] = window.LAYOUT;
    this.chip = (window.LAYOUT && window.LAYOUT.chip) || this.chips[0] || "2C02";
    if (this.chips.length && this.chips.indexOf(this.chip) < 0) this.chips.unshift(this.chip);
    if (!this.chips.length) this.chips = [this.chip];
    if (window.DV_HIGHLIGHTS) CACHE.H[this.mech + "|" + this.chip] = window.DV_HIGHLIGHTS;
    if (!CACHE.L[this.chip]) { host.textContent = "die-viewer: LAYOUT not loaded"; return; }
    this.view = { scale: 1, ox: 0, oy: 0 }; this.activeCats = {};
    this._buildShell();
    this._wireEvents();
    var self = this; this._setLoading(true);
    raf2(function () { self._boot(); });
  }

  // ── shell (fast; heavy work deferred to _boot so the loader paints) ──
  DieViewer.prototype._buildShell = function () {
    this.host.classList.add("dv-host");
    this.hud = el("div", "dv-hud");
    this.chipLabel = el("span", "dv-chip");
    this.hud.appendChild(this.chipLabel);
    if (this.chips.length > 1) {
      this.chipBtns = {};
      var wrap = el("span", "dv-chips"), self = this;
      this.chips.forEach(function (c) {
        var b = el("button", "dv-chipbtn"); b.type = "button"; b.textContent = CHIP_LABEL[c] || c;
        if (c === self.chip) b.classList.add("on");
        b.addEventListener("click", function () { self._switchChip(c); });
        self.chipBtns[c] = b; wrap.appendChild(b);
      });
      this.hud.appendChild(wrap);
    }
    var hint = el("span", "dv-hint");
    hint.setAttribute("data-en", "drag to pan · wheel to zoom · hover a cell");
    hint.setAttribute("data-zh", "拖曳平移 · 滾輪縮放 · 停在 cell 上看資訊");
    hint.textContent = "drag to pan · wheel to zoom · hover a cell";
    this.hud.appendChild(hint);
    this.reset = el("button", "dv-reset"); this.reset.type = "button"; this.reset.textContent = "reset";
    this.hud.appendChild(this.reset);

    this.viewport = el("div", "dv-viewport");
    this.canvas = el("canvas", "dv-canvas");
    this.tip = el("div", "dv-tip"); this.tip.style.display = "none";
    this.loader = el("div", "dv-loading");
    this.loader.innerHTML = '<div class="dv-spin"></div><span data-en="rendering the die…" data-zh="正在渲染晶粒…">rendering the die…</span>';
    this.viewport.appendChild(this.canvas);
    this.viewport.appendChild(this.tip);
    this.viewport.appendChild(this.loader);
    this.legend = el("div", "dv-legend");
    this.host.appendChild(this.hud);
    this.host.appendChild(this.viewport);
    this.host.appendChild(this.legend);
  };

  DieViewer.prototype._setLoading = function (on) {
    if (!this.loader) return;
    this.loader.classList.toggle("hide", !on);
    // re-apply language on the loader/hud text nodes
    var lang = document.body.className.indexOf("lang-zh") >= 0 ? "zh" : "en";
    this.host.querySelectorAll("[data-" + lang + "]").forEach(function (e) { var t = e.getAttribute("data-" + lang); if (t) e.textContent = t; });
  };

  // ── boot / re-boot with the current chip's data ──
  DieViewer.prototype._boot = function () {
    this.L = CACHE.L[this.chip];
    this.b = this.L.bbox; this.dieW = this.b[2] - this.b[0]; this.dieH = this.b[3] - this.b[1];
    this.hl = CACHE.H[this.mech + "|" + this.chip] || null;
    this.chipLabel.textContent = this.L.chip;
    this.view = { scale: 1, ox: 0, oy: 0 };
    this._cache = null; this._cacheView = null;
    this._precompute();
    this._buildHit();
    this.activeCats = {}; this.legend.innerHTML = "";
    if (this.hl) this._useHighlights(this.hl); else this._noHl();
    this._resizeCanvas();
    this._fit();
    this._setLoading(false);
  };

  DieViewer.prototype._noHl = function () {
    this.nodeCat = {}; this.segsByNode = {};
    var head = el("div", "dv-legend-head");
    head.innerHTML = '<span data-en="no structures for this mechanism on this die" data-zh="此機制在這顆晶粒上沒有結構">no structures for this mechanism on this die</span>';
    this.legend.appendChild(head); this._setLoading(false);
  };

  DieViewer.prototype._switchChip = function (chip) {
    if (chip === this.chip || !this.chips.length) return;
    var self = this; this._setLoading(true);
    Object.keys(this.chipBtns).forEach(function (c) { self.chipBtns[c].classList.toggle("on", c === chip); });
    this._ensureChip(chip, function (err) {
      if (err) { self.loader.innerHTML = '<span>load failed: ' + err.message + '</span>'; return; }
      self.chip = chip; raf2(function () { self._boot(); });
    });
  };

  // lazy-load a chip's layout + this mechanism's node file, caching the parsed globals
  DieViewer.prototype._ensureChip = function (chip, cb) {
    var lc = chip.toLowerCase(), hk = this.mech + "|" + chip, need = [], self = this;
    if (!CACHE.L[chip]) need.push({ src: "layout/" + lc + "_layout.js", grab: function () { CACHE.L[chip] = window.LAYOUT; } });
    if (this.mech && !CACHE.H[hk]) need.push({ src: "layout/" + self.mech + "_" + lc + "_nodes.js", grab: function () { CACHE.H[hk] = window.DV_HIGHLIGHTS; } });
    (function step() {
      if (!need.length) return cb();
      var n = need.shift();
      loadScript(n.src, function (e) { if (e) { if (n.src.indexOf("_nodes") >= 0) { CACHE.H[hk] = null; return step(); } return cb(e); } n.grab(); step(); });
    })();
  };

  // ── the rest is the renderer (unchanged in behaviour) ──
  DieViewer.prototype._precompute = function () {
    var segs = this.L.segs; this.bboxes = new Array(segs.length);
    for (var i = 0; i < segs.length; i++) {
      var s = segs[i], mnx = Infinity, mny = Infinity, mxx = -Infinity, mxy = -Infinity;
      for (var k = 2; k < s.length; k += 2) { var x = s[k], y = s[k + 1]; if (x < mnx) mnx = x; if (x > mxx) mxx = x; if (y < mny) mny = y; if (y > mxy) mxy = y; }
      this.bboxes[i] = [mnx, mny, mxx, mxy];
    }
  };

  DieViewer.prototype._buildHit = function () {
    var s = HIT_PX / Math.max(this.dieW, this.dieH);
    this.hitW = Math.max(1, Math.round(this.dieW * s)); this.hitH = Math.max(1, Math.round(this.dieH * s)); this.hitScale = s;
    var cv = document.createElement("canvas"); cv.width = this.hitW; cv.height = this.hitH;
    var ctx = cv.getContext("2d", { willReadFrequently: true });
    ctx.imageSmoothingEnabled = false; ctx.fillStyle = "#000"; ctx.fillRect(0, 0, this.hitW, this.hitH);
    var segs = this.L.segs, b = this.b;
    for (var i = 0; i < segs.length; i++) {
      var seg = segs[i];
      ctx.fillStyle = "#" + ("000000" + seg[0].toString(16)).slice(-6);
      ctx.beginPath(); ctx.moveTo((seg[2] - b[0]) * s, (b[3] - seg[3]) * s);
      for (var k = 4; k < seg.length; k += 2) ctx.lineTo((seg[k] - b[0]) * s, (b[3] - seg[k + 1]) * s);
      ctx.closePath(); ctx.fill();
    }
    this._hitCtx = ctx;
  };

  DieViewer.prototype._useHighlights = function (j) {
    var self = this; this.hl = j; this.nodeCat = {};
    Object.keys(j.categories).forEach(function (key) {
      self.activeCats[key] = !j.categories[key].off;
      j.categories[key].nodes.forEach(function (n) { (self.nodeCat[n] || (self.nodeCat[n] = [])).push(key); });
    });
    this.segsByNode = {};
    var segs = this.L.segs, flagged = this.nodeCat;
    for (var i = 0; i < segs.length; i++) if (flagged[segs[i][0]]) (this.segsByNode[segs[i][0]] || (this.segsByNode[segs[i][0]] = [])).push(i);
    this._buildLegend();
  };

  DieViewer.prototype._sx = function (x) { return (x - this.b[0]) * this.view.scale + this.view.ox; };
  DieViewer.prototype._sy = function (y) { return (this.b[3] - y) * this.view.scale + this.view.oy; };
  DieViewer.prototype._toDie = function (sx, sy) { return [(sx - this.view.ox) / this.view.scale + this.b[0], this.b[3] - (sy - this.view.oy) / this.view.scale]; };

  DieViewer.prototype._fit = function () {
    var r = this.viewport.getBoundingClientRect(); if (!r.width || !r.height) return;
    this.fitScale = Math.min(r.width / this.dieW, r.height / this.dieH);
    this.view.scale = this.fitScale;
    this.view.ox = (r.width - this.dieW * this.view.scale) / 2; this.view.oy = (r.height - this.dieH * this.view.scale) / 2;
    this._draw();
  };

  DieViewer.prototype._resizeCanvas = function () {
    var r = this.viewport.getBoundingClientRect(), dpr = window.devicePixelRatio || 1;
    this.vpW = r.width; this.vpH = r.height; this.dpr = dpr;
    this.canvas.width = Math.round(r.width * dpr); this.canvas.height = Math.round(r.height * dpr);
    this.canvas.style.width = r.width + "px"; this.canvas.style.height = r.height + "px";
  };

  DieViewer.prototype._draw = function (fast) {
    if (!this.vpW) this._resizeCanvas();
    var ctx = this.canvas.getContext("2d");
    ctx.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
    ctx.clearRect(0, 0, this.vpW, this.vpH); ctx.fillStyle = "#0b0a0f"; ctx.fillRect(0, 0, this.vpW, this.vpH);
    var d0 = this._toDie(0, this.vpH), d1 = this._toDie(this.vpW, 0);
    var vminx = d0[0], vmaxx = d1[0], vminy = d0[1], vmaxy = d1[1];
    var segs = this.L.segs, bb = this.bboxes, self = this, sc = this.view.scale;
    var minDie = fast ? FAST_MIN_PX / sc : 0, drawn = 0;
    for (var i = 0; i < segs.length; i++) {
      var box = bb[i];
      if (box[2] < vminx || box[0] > vmaxx || box[3] < vminy || box[1] > vmaxy) continue;
      if (minDie && (box[2] - box[0]) < minDie && (box[3] - box[1]) < minDie) continue;
      var s = segs[i]; ctx.fillStyle = LAYER_COLORS[s[1]] || "#2a2a33"; this._path(ctx, s); ctx.fill(); drawn++;
    }
    if (this.hl) {
      var cats = this.hl.categories, doStroke = !fast; ctx.lineWidth = Math.max(0.6, Math.min(2, sc * 40));
      Object.keys(cats).forEach(function (key) {
        if (!self.activeCats[key]) return;
        var c = cats[key]; ctx.fillStyle = self._rgba(c.color, 0.72); ctx.strokeStyle = self._rgba(c.color, 0.95);
        c.nodes.forEach(function (n) {
          var idxs = self.segsByNode[n]; if (!idxs) return;
          for (var j = 0; j < idxs.length; j++) {
            var b2 = bb[idxs[j]]; if (b2[2] < vminx || b2[0] > vmaxx || b2[3] < vminy || b2[1] > vmaxy) continue;
            self._path(ctx, segs[idxs[j]]); ctx.fill(); if (doStroke) ctx.stroke();
          }
        });
      });
    }
    this._lastDrawn = drawn; if (!fast) this._capture();
  };

  DieViewer.prototype._capture = function () {
    if (!this._cache) this._cache = document.createElement("canvas");
    if (this._cache.width !== this.canvas.width || this._cache.height !== this.canvas.height) { this._cache.width = this.canvas.width; this._cache.height = this.canvas.height; }
    var cx = this._cache.getContext("2d"); cx.setTransform(1, 0, 0, 1, 0, 0); cx.clearRect(0, 0, this._cache.width, this._cache.height); cx.drawImage(this.canvas, 0, 0);
    this._cacheView = { scale: this.view.scale, ox: this.view.ox, oy: this.view.oy };
  };

  DieViewer.prototype._blitZoom = function () {
    if (!this._cache || !this._cacheView) { this._requestDraw(true); return; }
    var v = this.view, cv = this._cacheView, s = v.scale / cv.scale, dpr = this.dpr, ctx = this.canvas.getContext("2d");
    ctx.setTransform(1, 0, 0, 1, 0, 0); ctx.clearRect(0, 0, this.canvas.width, this.canvas.height);
    ctx.fillStyle = "#0b0a0f"; ctx.fillRect(0, 0, this.canvas.width, this.canvas.height); ctx.imageSmoothingEnabled = true;
    ctx.setTransform(s, 0, 0, s, (v.ox - cv.ox * s) * dpr, (v.oy - cv.oy * s) * dpr); ctx.drawImage(this._cache, 0, 0);
  };

  DieViewer.prototype._path = function (ctx, s) {
    ctx.beginPath(); ctx.moveTo(this._sx(s[2]), this._sy(s[3]));
    for (var k = 4; k < s.length; k += 2) ctx.lineTo(this._sx(s[k]), this._sy(s[k + 1]));
    ctx.closePath();
  };

  DieViewer.prototype._requestDraw = function (fast) {
    this._pendFast = this._pendFast || !!fast; if (this._raf) return;
    var self = this; this._raf = requestAnimationFrame(function () { self._raf = 0; var f = self._pendFast; self._pendFast = false; self._draw(f); });
  };
  DieViewer.prototype._settleSoon = function () { var self = this; clearTimeout(this._settleT); this._settleT = setTimeout(function () { self._draw(false); }, SETTLE_MS); };

  DieViewer.prototype._buildLegend = function () {
    var cats = this.hl.categories, self = this;
    var head = el("div", "dv-legend-head");
    head.innerHTML = '<b>' + (this.hl.mechanism || "") + '</b> <span data-en="detected structures — click to toggle" data-zh="偵測到的結構 — 點色塊開關">detected structures — click to toggle</span>';
    this.legend.appendChild(head);
    Object.keys(cats).forEach(function (key) {
      var c = cats[key], row = el("button", "dv-leg-row"); row.type = "button";
      if (!self.activeCats[key]) row.classList.add("off");
      row.innerHTML = '<span class="dv-sw" style="background:' + c.color + '"></span><span class="dv-leg-lbl">' + c.label + '</span><span class="dv-leg-n">' + c.nodes.length + '</span>';
      row.addEventListener("click", function () { self.activeCats[key] = !self.activeCats[key]; row.classList.toggle("off", !self.activeCats[key]); self._draw(); });
      self.legend.appendChild(row);
    });
  };

  DieViewer.prototype._wireEvents = function () {
    var self = this, vp = this.viewport, drag = false, lx = 0, ly = 0;
    vp.addEventListener("mousedown", function (e) { drag = true; lx = e.clientX; ly = e.clientY; vp.classList.add("dv-grab"); self.tip.style.display = "none"; });
    window.addEventListener("mouseup", function () { if (!drag) return; drag = false; vp.classList.remove("dv-grab"); self._draw(false); });
    vp.addEventListener("mousemove", function (e) {
      if (drag) { self.view.ox += e.clientX - lx; self.view.oy += e.clientY - ly; lx = e.clientX; ly = e.clientY; self._requestDraw(true); }
      else self._hover(e);
    });
    vp.addEventListener("mouseleave", function () { self.tip.style.display = "none"; });
    vp.addEventListener("wheel", function (e) {
      e.preventDefault();
      var r = vp.getBoundingClientRect(), mx = e.clientX - r.left, my = e.clientY - r.top;
      var factor = Math.exp(-e.deltaY * 0.0016), lo = self.fitScale * MIN_SCALE_MULT, hi = self.fitScale * MAX_SCALE_MULT;
      var ns = Math.max(lo, Math.min(hi, self.view.scale * factor)), k = ns / self.view.scale;
      self.view.ox = mx - k * (mx - self.view.ox); self.view.oy = my - k * (my - self.view.oy); self.view.scale = ns;
      self._blitZoom(); self._settleSoon();
    }, { passive: false });
    this.reset.addEventListener("click", function () { self._fit(); });
    window.addEventListener("resize", function () { if (self.vpW) { self._resizeCanvas(); self._draw(); } });
  };

  DieViewer.prototype._hover = function (e) {
    if (!this._hitCtx) return;
    var r = this.viewport.getBoundingClientRect(), vx = e.clientX - r.left, vy = e.clientY - r.top;
    var die = this._toDie(vx, vy);
    var hx = Math.round((die[0] - this.b[0]) * this.hitScale), hy = Math.round((this.b[3] - die[1]) * this.hitScale);
    if (hx < 0 || hy < 0 || hx >= this.hitW || hy >= this.hitH) { this.tip.style.display = "none"; return; }
    var px = this._hitCtx.getImageData(hx, hy, 1, 1).data, id = (px[0] << 16) | (px[1] << 8) | px[2];
    if (id === 0) { this.tip.style.display = "none"; return; }
    var name = (this.L.names && this.L.names[id]) || ("node " + id);
    var cats = (this.nodeCat && this.nodeCat[id]) || [], self = this, catHtml = "";
    if (this.hl) catHtml = cats.length
      ? cats.map(function (k) { return '<span class="dv-tip-cat" style="color:' + self.hl.categories[k].color + '">' + self.hl.categories[k].label + '</span>'; }).join("")
      : '<span class="dv-tip-none" data-en="not in this mechanism’s set" data-zh="不在此機制的集合">not flagged</span>';
    this.tip.innerHTML = '<b>' + name + '</b> <span class="dv-tip-id">#' + id + '</span>' + (catHtml ? '<br>' + catHtml : '');
    this.tip.style.display = "block"; this.tip.style.left = Math.min(vx + 14, this.vpW - 300) + "px"; this.tip.style.top = (vy + 14) + "px";
  };

  DieViewer.prototype._rgba = function (hex, a) { var n = parseInt(hex.slice(1), 16); return "rgba(" + ((n >> 16) & 255) + "," + ((n >> 8) & 255) + "," + (n & 255) + "," + a + ")"; };

  function init() { var h = document.querySelectorAll(".die-viewer"); for (var i = 0; i < h.length; i++) new DieViewer(h[i]); }
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", init); else init();
  window.DieViewer = DieViewer;
})();
