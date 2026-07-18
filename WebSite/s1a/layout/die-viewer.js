/* die-viewer.js — live, zoomable, interactive die-layout viewer for the S1a study.
 *
 * Renders a Visual6502-style silicon layout (polygons from segdefs) and overlays the
 * nodes a mechanism (M1–M7) flags, so an abstract census number becomes a place on the
 * real die. One shared component; each Mx page differs only in the highlight data.
 *
 * Rendering: the polygons are VECTORS, so we re-rasterise them at the current zoom on
 * every view change (with viewport culling), instead of CSS-scaling one fixed bitmap —
 * the layout stays crisp at any magnification. Hit-testing uses a separate fixed-res
 * offscreen buffer (Visual6502's idea; ours encodes the node id in a full 24-bit colour,
 * since the 12-bit original maxes at 4096 nodes).
 *
 * Layout data (segdefs) is CC-BY-NC-SA (Visual 2A03/2C02 / Visual6502) — keep attribution.
 * Highlights: a window.DV_HIGHLIGHTS global (loaded via <script>, so file:// preview works).
 */
(function () {
  "use strict";

  // Backdrop layer colours (subdued, so highlights pop). Index = segdefs layer.
  var LAYER_COLORS = ["#3a2f4a", "#2f3a46", "#46372f", "#4a4636", "#39424e", "#2a2a33"];
  var HIT_PX = 3000;            // fixed offscreen hit-buffer resolution (long side) — for id lookup only
  var MIN_SCALE_MULT = 0.9, MAX_SCALE_MULT = 90;   // zoom range, relative to the fit scale
  var FAST_MIN_PX = 1.3;       // during pan/zoom, skip polygons smaller than this on screen
  var SETTLE_MS = 150;         // after the gesture stops, redraw at full quality

  function el(tag, cls) { var e = document.createElement(tag); if (cls) e.className = cls; return e; }

  function DieViewer(host) {
    this.host = host;
    this.L = window.LAYOUT;
    if (!this.L) { host.textContent = "die-viewer: LAYOUT not loaded"; return; }
    this.b = this.L.bbox;                       // [minx,miny,maxx,maxy]
    this.dieW = this.b[2] - this.b[0];
    this.dieH = this.b[3] - this.b[1];
    this.hl = null; this.activeCats = {};
    this.view = { scale: 1, ox: 0, oy: 0 };     // die -> css-px:  sx = (x-minx)*scale+ox ; sy = (maxy-y)*scale+oy
    this._precompute();
    this._build();
    this._buildHit();
    if (window.DV_HIGHLIGHTS) this._useHighlights(window.DV_HIGHLIGHTS);
    this._wire();
  }

  // per-seg bbox (die coords) for viewport culling
  DieViewer.prototype._precompute = function () {
    var segs = this.L.segs; this.bboxes = new Array(segs.length);
    for (var i = 0; i < segs.length; i++) {
      var s = segs[i], mnx = Infinity, mny = Infinity, mxx = -Infinity, mxy = -Infinity;
      for (var k = 2; k < s.length; k += 2) {
        var x = s[k], y = s[k + 1];
        if (x < mnx) mnx = x; if (x > mxx) mxx = x; if (y < mny) mny = y; if (y > mxy) mxy = y;
      }
      this.bboxes[i] = [mnx, mny, mxx, mxy];
    }
  };

  DieViewer.prototype._build = function () {
    this.host.classList.add("dv-host");
    this.hud = el("div", "dv-hud");
    this.hud.innerHTML = '<span class="dv-chip">' + this.L.chip + '</span>' +
      '<span class="dv-hint" data-en="drag to pan · wheel to zoom · hover a cell" ' +
      'data-zh="拖曳平移 · 滾輪縮放 · 停在 cell 上看資訊">drag to pan · wheel to zoom · hover a cell</span>' +
      '<button class="dv-reset" type="button">reset</button>';
    this.viewport = el("div", "dv-viewport");
    this.canvas = el("canvas", "dv-canvas");
    this.tip = el("div", "dv-tip"); this.tip.style.display = "none";
    this.viewport.appendChild(this.canvas);
    this.viewport.appendChild(this.tip);
    this.legend = el("div", "dv-legend");
    this.host.appendChild(this.hud);
    this.host.appendChild(this.viewport);
    this.host.appendChild(this.legend);
  };

  // offscreen fixed-res hit buffer: node id encoded as 24-bit colour
  DieViewer.prototype._buildHit = function () {
    var s = HIT_PX / Math.max(this.dieW, this.dieH);
    this.hitW = Math.max(1, Math.round(this.dieW * s));
    this.hitH = Math.max(1, Math.round(this.dieH * s));
    this.hitScale = s;
    var cv = document.createElement("canvas"); cv.width = this.hitW; cv.height = this.hitH;
    var ctx = cv.getContext("2d", { willReadFrequently: true });
    ctx.imageSmoothingEnabled = false;
    ctx.fillStyle = "#000"; ctx.fillRect(0, 0, this.hitW, this.hitH);
    var segs = this.L.segs, b = this.b;
    for (var i = 0; i < segs.length; i++) {
      var seg = segs[i], id = seg[0];
      ctx.fillStyle = "#" + ("000000" + id.toString(16)).slice(-6);   // id 0 = reserved => #000000 = "no node"
      ctx.beginPath();
      ctx.moveTo((seg[2] - b[0]) * s, (b[3] - seg[3]) * s);
      for (var k = 4; k < seg.length; k += 2) ctx.lineTo((seg[k] - b[0]) * s, (b[3] - seg[k + 1]) * s);
      ctx.closePath(); ctx.fill();
    }
    this._hitCtx = ctx;
  };

  DieViewer.prototype._useHighlights = function (j) {
    var self = this; this.hl = j; this.nodeCat = {};
    Object.keys(j.categories).forEach(function (key) {
      self.activeCats[key] = true;
      j.categories[key].nodes.forEach(function (n) { (self.nodeCat[n] || (self.nodeCat[n] = [])).push(key); });
    });
    this.segsByNode = {};
    var segs = this.L.segs, flagged = this.nodeCat;
    for (var i = 0; i < segs.length; i++) if (flagged[segs[i][0]]) (this.segsByNode[segs[i][0]] || (this.segsByNode[segs[i][0]] = [])).push(i);
    this._buildLegend();
  };

  // ── view transform ──
  DieViewer.prototype._sx = function (x) { return (x - this.b[0]) * this.view.scale + this.view.ox; };
  DieViewer.prototype._sy = function (y) { return (this.b[3] - y) * this.view.scale + this.view.oy; };
  DieViewer.prototype._toDie = function (sx, sy) {
    return [(sx - this.view.ox) / this.view.scale + this.b[0], this.b[3] - (sy - this.view.oy) / this.view.scale];
  };

  DieViewer.prototype._fit = function () {
    var r = this.viewport.getBoundingClientRect();
    if (!r.width || !r.height) return;
    this.fitScale = Math.min(r.width / this.dieW, r.height / this.dieH);
    this.view.scale = this.fitScale;
    this.view.ox = (r.width - this.dieW * this.view.scale) / 2;
    this.view.oy = (r.height - this.dieH * this.view.scale) / 2;
    this._draw();
  };

  DieViewer.prototype._resizeCanvas = function () {
    var r = this.viewport.getBoundingClientRect();
    var dpr = window.devicePixelRatio || 1;
    this.vpW = r.width; this.vpH = r.height; this.dpr = dpr;
    this.canvas.width = Math.round(r.width * dpr); this.canvas.height = Math.round(r.height * dpr);
    this.canvas.style.width = r.width + "px"; this.canvas.style.height = r.height + "px";
  };

  // redraw the polygons at the current view — crisp at any zoom (vectors, not a scaled bitmap).
  // fast=true (during a pan/zoom gesture): skip sub-pixel polygons and highlight strokes so the
  // interaction stays smooth; a full-quality pass follows when the gesture settles.
  DieViewer.prototype._draw = function (fast) {
    if (!this.vpW) this._resizeCanvas();
    var ctx = this.canvas.getContext("2d");
    ctx.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
    ctx.clearRect(0, 0, this.vpW, this.vpH);
    ctx.fillStyle = "#0b0a0f"; ctx.fillRect(0, 0, this.vpW, this.vpH);

    // visible die-rect (for culling); Y flip: (0,vpH)->min, (vpW,0)->max
    var d0 = this._toDie(0, this.vpH), d1 = this._toDie(this.vpW, 0);
    var vminx = d0[0], vmaxx = d1[0], vminy = d0[1], vmaxy = d1[1];
    var segs = this.L.segs, bb = this.bboxes, self = this, sc = this.view.scale;
    var minDie = fast ? FAST_MIN_PX / sc : 0;    // die-units below which a poly is sub-pixel on screen
    var drawn = 0;

    // pass 1: backdrop, by layer colour
    for (var i = 0; i < segs.length; i++) {
      var box = bb[i];
      if (box[2] < vminx || box[0] > vmaxx || box[3] < vminy || box[1] > vmaxy) continue;   // viewport cull
      if (minDie && (box[2] - box[0]) < minDie && (box[3] - box[1]) < minDie) continue;      // sub-pixel cull (fast)
      var s = segs[i];
      ctx.fillStyle = LAYER_COLORS[s[1]] || "#2a2a33";
      this._path(ctx, s); ctx.fill(); drawn++;
    }
    // pass 2: highlights on top (never size-culled — they are the point; stroke skipped in fast mode)
    if (this.hl) {
      var cats = this.hl.categories, doStroke = !fast;
      ctx.lineWidth = Math.max(0.6, Math.min(2, sc * 40));
      Object.keys(cats).forEach(function (key) {
        if (!self.activeCats[key]) return;
        var c = cats[key];
        ctx.fillStyle = self._rgba(c.color, 0.72);
        ctx.strokeStyle = self._rgba(c.color, 0.95);
        c.nodes.forEach(function (n) {
          var idxs = self.segsByNode[n]; if (!idxs) return;
          for (var j = 0; j < idxs.length; j++) {
            var box2 = bb[idxs[j]];
            if (box2[2] < vminx || box2[0] > vmaxx || box2[3] < vminy || box2[1] > vmaxy) continue;
            self._path(ctx, segs[idxs[j]]); ctx.fill(); if (doStroke) ctx.stroke();
          }
        });
      });
    }
    this._lastDrawn = drawn;
    if (!fast) this._capture();     // snapshot this crisp render for the zoom-gesture blit
  };

  // snapshot the current (full-quality) canvas + the view it was drawn at
  DieViewer.prototype._capture = function () {
    if (!this._cache) this._cache = document.createElement("canvas");
    if (this._cache.width !== this.canvas.width || this._cache.height !== this.canvas.height) {
      this._cache.width = this.canvas.width; this._cache.height = this.canvas.height;
    }
    var cx = this._cache.getContext("2d");
    cx.setTransform(1, 0, 0, 1, 0, 0); cx.clearRect(0, 0, this._cache.width, this._cache.height);
    cx.drawImage(this.canvas, 0, 0);
    this._cacheView = { scale: this.view.scale, ox: this.view.ox, oy: this.view.oy };
  };

  // during a zoom gesture: blit the cached crisp render, transformed to the current view.
  // One drawImage — instant, slightly soft while moving; the settle pass redraws vectors sharp.
  DieViewer.prototype._blitZoom = function () {
    if (!this._cache || !this._cacheView) { this._requestDraw(true); return; }
    var v = this.view, cv = this._cacheView, s = v.scale / cv.scale, dpr = this.dpr;
    var ctx = this.canvas.getContext("2d");
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.clearRect(0, 0, this.canvas.width, this.canvas.height);
    ctx.fillStyle = "#0b0a0f"; ctx.fillRect(0, 0, this.canvas.width, this.canvas.height);
    ctx.imageSmoothingEnabled = true;
    ctx.setTransform(s, 0, 0, s, (v.ox - cv.ox * s) * dpr, (v.oy - cv.oy * s) * dpr);
    ctx.drawImage(this._cache, 0, 0);
  };

  DieViewer.prototype._path = function (ctx, s) {
    ctx.beginPath();
    ctx.moveTo(this._sx(s[2]), this._sy(s[3]));
    for (var k = 4; k < s.length; k += 2) ctx.lineTo(this._sx(s[k]), this._sy(s[k + 1]));
    ctx.closePath();
  };

  // throttle draws to one per frame; fast=true degrades for smoothness during a gesture
  DieViewer.prototype._requestDraw = function (fast) {
    this._pendFast = this._pendFast || !!fast;   // once a frame is marked fast it stays fast
    if (this._raf) return;
    var self = this;
    this._raf = requestAnimationFrame(function () { self._raf = 0; var f = self._pendFast; self._pendFast = false; self._draw(f); });
  };

  // after the gesture stops, redraw once at full quality
  DieViewer.prototype._settleSoon = function () {
    var self = this;
    clearTimeout(this._settleT);
    this._settleT = setTimeout(function () { self._draw(false); }, SETTLE_MS);
  };

  DieViewer.prototype._buildLegend = function () {
    var cats = this.hl.categories, self = this;
    var head = el("div", "dv-legend-head");
    head.innerHTML = '<b>' + (this.hl.mechanism || "") + '</b> <span data-en="detected structures — click to toggle" data-zh="偵測到的結構 — 點色塊開關">detected structures — click to toggle</span>';
    this.legend.appendChild(head);
    Object.keys(cats).forEach(function (key) {
      var c = cats[key], row = el("button", "dv-leg-row"); row.type = "button";
      row.innerHTML = '<span class="dv-sw" style="background:' + c.color + '"></span>' +
        '<span class="dv-leg-lbl">' + c.label + '</span><span class="dv-leg-n">' + c.nodes.length + '</span>';
      row.addEventListener("click", function () {
        self.activeCats[key] = !self.activeCats[key];
        row.classList.toggle("off", !self.activeCats[key]); self._draw();
      });
      self.legend.appendChild(row);
    });
  };

  DieViewer.prototype._wire = function () {
    var self = this, vp = this.viewport, drag = false, lx = 0, ly = 0;
    this._resizeCanvas();
    requestAnimationFrame(function () { self._resizeCanvas(); self._fit(); });

    vp.addEventListener("mousedown", function (e) { drag = true; lx = e.clientX; ly = e.clientY; vp.classList.add("dv-grab"); self.tip.style.display = "none"; });
    window.addEventListener("mouseup", function () { if (!drag) return; drag = false; vp.classList.remove("dv-grab"); self._draw(false); });
    vp.addEventListener("mousemove", function (e) {
      if (drag) {
        self.view.ox += e.clientX - lx; self.view.oy += e.clientY - ly; lx = e.clientX; ly = e.clientY;
        self._requestDraw(true);              // degrade while dragging
      } else self._hover(e);
    });
    vp.addEventListener("mouseleave", function () { self.tip.style.display = "none"; });
    vp.addEventListener("wheel", function (e) {
      e.preventDefault();
      var r = vp.getBoundingClientRect(), mx = e.clientX - r.left, my = e.clientY - r.top;
      var factor = Math.exp(-e.deltaY * 0.0016);
      var lo = self.fitScale * MIN_SCALE_MULT, hi = self.fitScale * MAX_SCALE_MULT;
      var ns = Math.max(lo, Math.min(hi, self.view.scale * factor)), k = ns / self.view.scale;
      self.view.ox = mx - k * (mx - self.view.ox);   // keep cursor point fixed
      self.view.oy = my - k * (my - self.view.oy);
      self.view.scale = ns;
      self._blitZoom(); self._settleSoon();    // instant bitmap blit while zooming, vector-sharp when it stops
    }, { passive: false });
    this.hud.querySelector(".dv-reset").addEventListener("click", function () { self._fit(); });
    window.addEventListener("resize", function () { self._resizeCanvas(); self._draw(); });
  };

  DieViewer.prototype._hover = function (e) {
    var r = this.viewport.getBoundingClientRect(), vx = e.clientX - r.left, vy = e.clientY - r.top;
    var die = this._toDie(vx, vy);
    var hx = Math.round((die[0] - this.b[0]) * this.hitScale), hy = Math.round((this.b[3] - die[1]) * this.hitScale);
    if (hx < 0 || hy < 0 || hx >= this.hitW || hy >= this.hitH) { this.tip.style.display = "none"; return; }
    var px = this._hitCtx.getImageData(hx, hy, 1, 1).data;
    var id = (px[0] << 16) | (px[1] << 8) | px[2];
    if (id === 0) { this.tip.style.display = "none"; return; }
    var name = (this.L.names && this.L.names[id]) || ("node " + id);
    var cats = (this.nodeCat && this.nodeCat[id]) || [], self = this, catHtml = "";
    if (this.hl) catHtml = cats.length
      ? cats.map(function (k) { return '<span class="dv-tip-cat" style="color:' + self.hl.categories[k].color + '">' + self.hl.categories[k].label + '</span>'; }).join("")
      : '<span class="dv-tip-none" data-en="not in this mechanism’s set" data-zh="不在此機制的集合">not flagged</span>';
    this.tip.innerHTML = '<b>' + name + '</b> <span class="dv-tip-id">#' + id + '</span>' + (catHtml ? '<br>' + catHtml : '');
    this.tip.style.display = "block";
    this.tip.style.left = Math.min(vx + 14, this.vpW - 300) + "px";
    this.tip.style.top = (vy + 14) + "px";
  };

  DieViewer.prototype._rgba = function (hex, a) {
    var n = parseInt(hex.slice(1), 16);
    return "rgba(" + ((n >> 16) & 255) + "," + ((n >> 8) & 255) + "," + (n & 255) + "," + a + ")";
  };

  function init() { var h = document.querySelectorAll(".die-viewer"); for (var i = 0; i < h.length; i++) new DieViewer(h[i]); }
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", init); else init();
  window.DieViewer = DieViewer;
})();
