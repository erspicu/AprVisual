/* die-viewer.js — live, zoomable, interactive die-layout viewer for the S1a study.
 *
 * Renders a Visual6502-style silicon layout (polygons from segdefs) on canvas and
 * overlays the nodes a mechanism (M1–M7) flags, so an abstract census number becomes
 * a place on the real die. One shared component; each Mx page differs only in which
 * highlight JSON it loads (the polygons live in the shared LAYOUT).
 *
 * Rendering technique adapted from Visual6502's wires.js (Brian & Barry Silverman,
 * MIT) — the stacked-canvas backdrop + 24-bit hit buffer + CSS-transform zoom/pan.
 * Ours differs in: full-byte node encoding (Visual6502's 12-bit maxes at 4096 nodes;
 * the 2C02 has more), a category-highlight overlay, and drag/wheel navigation.
 *
 * Layout data (segdefs) is CC-BY-NC-SA (Visual 2A03/2C02 / Visual6502) — keep attribution.
 *
 * Usage: include <chip>_layout.js (defines global LAYOUT) then this file, and add
 *   <div class="die-viewer" data-highlights="m4_2c02_nodes.json"></div>
 */
(function () {
  "use strict";

  // Backdrop layer colours (subdued, so highlights pop). Index = segdefs layer.
  //   0 diffusion · 1 grounded-diff · 2 powered-diff · 3 poly · 4 metal · 5 (contacts/misc)
  var LAYER_COLORS = ["#3a2f4a", "#2f3a46", "#46372f", "#4a4636", "#39424e", "#2a2a33"];
  var LAYER_STROKE = { 3: "rgba(200,180,120,0.10)" }; // faint poly outline for legibility
  var RENDER_PX = 2600;            // fixed render resolution (long side); CSS scales this
  var MIN_ZOOM = 0.15, MAX_ZOOM = 14;

  function el(tag, cls, css) {
    var e = document.createElement(tag);
    if (cls) e.className = cls;
    if (css) e.setAttribute("style", css);
    return e;
  }

  function DieViewer(host) {
    this.host = host;
    this.L = window.LAYOUT;
    if (!this.L) { host.textContent = "die-viewer: LAYOUT not loaded"; return; }
    this.hl = null;            // highlight data (categories)
    this.activeCats = {};      // category key -> bool (legend toggles)
    this.zoom = 1; this.panX = 0; this.panY = 0;
    this._build();
    this._computeTransform();
    this._renderBackdrop();
    this._renderHitBuffer();
    // Highlights: prefer an embedded global (window.DV_HIGHLIGHTS, loaded via <script> so
    // file:// preview works); fall back to fetching a JSON url from data-highlights.
    if (window.DV_HIGHLIGHTS) this._useHighlights(window.DV_HIGHLIGHTS);
    else {
      var url = host.getAttribute("data-highlights");
      if (url) this._loadHighlights(url); else this._renderHilite();
    }
    this._wire();
  }

  DieViewer.prototype._build = function () {
    var L = this.L;
    var minx = L.bbox[0], miny = L.bbox[1], maxx = L.bbox[2], maxy = L.bbox[3];
    var dieW = maxx - minx, dieH = maxy - miny;
    this.scale = RENDER_PX / Math.max(dieW, dieH);
    this.cw = Math.round(dieW * this.scale);
    this.ch = Math.round(dieH * this.scale);

    this.host.classList.add("dv-host");
    this.viewport = el("div", "dv-viewport");
    this.stack = el("div", "dv-stack");
    this.stack.style.width = this.cw + "px";
    this.stack.style.height = this.ch + "px";

    this.bg = el("canvas", "dv-canvas");
    this.hi = el("canvas", "dv-canvas");
    this.hit = el("canvas", "dv-canvas");          // hidden hit buffer
    [this.bg, this.hi, this.hit].forEach(function (c) { c.width = 0; }); // set below
    this.bg.width = this.hi.width = this.hit.width = this.cw;
    this.bg.height = this.hi.height = this.hit.height = this.ch;
    this.hit.style.display = "none";

    this.stack.appendChild(this.bg);
    this.stack.appendChild(this.hi);
    this.stack.appendChild(this.hit);
    this.viewport.appendChild(this.stack);

    this.tip = el("div", "dv-tip"); this.tip.style.display = "none";
    this.legend = el("div", "dv-legend");
    this.hud = el("div", "dv-hud");
    this.hud.innerHTML = '<span class="dv-chip">' + this.L.chip + '</span>' +
      '<span class="dv-hint" data-en="drag to pan · wheel to zoom · hover a cell" ' +
      'data-zh="拖曳平移 · 滾輪縮放 · 停在 cell 上看資訊">drag to pan · wheel to zoom · hover a cell</span>' +
      '<button class="dv-reset" type="button">reset</button>';

    this.viewport.appendChild(this.tip);
    this.host.appendChild(this.hud);
    this.host.appendChild(this.viewport);
    this.host.appendChild(this.legend);
  };

  // die coord -> render-canvas px (Y flipped: die is Y-up, canvas is Y-down)
  DieViewer.prototype._computeTransform = function () {
    var b = this.L.bbox, s = this.scale;
    this._tx = function (x) { return (x - b[0]) * s; };
    this._ty = function (y) { return (b[3] - y) * s; };
  };

  DieViewer.prototype._poly = function (ctx, coords) {
    ctx.beginPath();
    ctx.moveTo(this._tx(coords[0]), this._ty(coords[1]));
    for (var i = 2; i < coords.length; i += 2) ctx.lineTo(this._tx(coords[i]), this._ty(coords[i + 1]));
    ctx.closePath();
  };

  DieViewer.prototype._renderBackdrop = function () {
    var ctx = this.bg.getContext("2d");
    ctx.fillStyle = "#0b0a0f"; ctx.fillRect(0, 0, this.cw, this.ch);
    var segs = this.L.segs;
    for (var i = 0; i < segs.length; i++) {
      var s = segs[i], layer = s[1];
      ctx.fillStyle = LAYER_COLORS[layer] || "#2a2a33";
      // s is [node,layer,coords...]; draw the polygon inline (avoids a slice per seg)
      ctx.beginPath();
      ctx.moveTo(this._tx(s[2]), this._ty(s[3]));
      for (var k = 4; k < s.length; k += 2) ctx.lineTo(this._tx(s[k]), this._ty(s[k + 1]));
      ctx.closePath();
      ctx.fill();
      if (LAYER_STROKE[layer]) { ctx.strokeStyle = LAYER_STROKE[layer]; ctx.lineWidth = 1; ctx.stroke(); }
    }
  };

  // hit buffer: paint every node's polygons in a colour that encodes its id (24-bit).
  DieViewer.prototype._renderHitBuffer = function () {
    var ctx = this.hit.getContext("2d");
    ctx.imageSmoothingEnabled = false;
    ctx.fillStyle = "#000000"; ctx.fillRect(0, 0, this.cw, this.ch);
    var segs = this.L.segs;
    for (var i = 0; i < segs.length; i++) {
      var s = segs[i], id = s[0];
      // id 0 is reserved/EMPTY in the netlist, so #000000 safely means "no node"
      var hex = "#" + ("000000" + id.toString(16)).slice(-6);
      ctx.fillStyle = hex;
      ctx.beginPath();
      ctx.moveTo(this._tx(s[2]), this._ty(s[3]));
      for (var k = 4; k < s.length; k += 2) ctx.lineTo(this._tx(s[k]), this._ty(s[k + 1]));
      ctx.closePath();
      ctx.fill();
    }
    this._hitCtx = ctx;
  };

  DieViewer.prototype._loadHighlights = function (url) {
    var self = this;
    fetch(url).then(function (r) { return r.json(); })
      .then(function (j) { self._useHighlights(j); })
      .catch(function (e) { self.legend.textContent = "highlights load failed: " + e; });
  };

  DieViewer.prototype._useHighlights = function (j) {
    var self = this;
    this.hl = j;
    // index node -> [category keys] for the tooltip
    this.nodeCat = {};
    Object.keys(j.categories).forEach(function (key) {
      self.activeCats[key] = true;
      j.categories[key].nodes.forEach(function (n) {
        (self.nodeCat[n] || (self.nodeCat[n] = [])).push(key);
      });
    });
    // node -> polygons, for fast hilite redraw (only flagged nodes)
    this.segsByNode = {};
    var flagged = this.nodeCat;
    this.L.segs.forEach(function (s) {
      if (flagged[s[0]]) (self.segsByNode[s[0]] || (self.segsByNode[s[0]] = [])).push(s);
    });
    this._buildLegend();
    this._renderHilite();
  };

  DieViewer.prototype._renderHilite = function () {
    var ctx = this.hi.getContext("2d");
    ctx.clearRect(0, 0, this.cw, this.ch);
    if (!this.hl) return;
    var cats = this.hl.categories, self = this;
    // draw inactive-first so active categories sit on top; here just draw active ones
    Object.keys(cats).forEach(function (key) {
      if (!self.activeCats[key]) return;
      var c = cats[key];
      ctx.fillStyle = self._rgba(c.color, 0.66);
      ctx.strokeStyle = self._rgba(c.color, 0.95); ctx.lineWidth = 1.2;
      c.nodes.forEach(function (n) {
        var segs = self.segsByNode[n]; if (!segs) return;   // node has no visible polygon
        for (var i = 0; i < segs.length; i++) { self._poly(ctx, segsCoords(segs[i])); ctx.fill(); ctx.stroke(); }
      });
    });
  };

  DieViewer.prototype._buildLegend = function () {
    var cats = this.hl.categories, self = this;
    var head = el("div", "dv-legend-head");
    head.innerHTML = '<b>' + (this.hl.mechanism || "") + '</b> <span data-en="detected structures — click a swatch to toggle" data-zh="偵測到的結構 — 點色塊開關">detected structures — click to toggle</span>';
    this.legend.appendChild(head);
    Object.keys(cats).forEach(function (key) {
      var c = cats[key];
      var row = el("button", "dv-leg-row"); row.type = "button"; row.dataset.cat = key;
      row.innerHTML = '<span class="dv-sw" style="background:' + c.color + '"></span>' +
        '<span class="dv-leg-lbl">' + c.label + '</span>' +
        '<span class="dv-leg-n">' + c.nodes.length + '</span>';
      row.addEventListener("click", function () {
        self.activeCats[key] = !self.activeCats[key];
        row.classList.toggle("off", !self.activeCats[key]);
        self._renderHilite();
      });
      self.legend.appendChild(row);
    });
  };

  DieViewer.prototype._wire = function () {
    var self = this, vp = this.viewport, dragging = false, lx = 0, ly = 0, moved = 0;
    this._apply();
    vp.addEventListener("mousedown", function (e) { dragging = true; moved = 0; lx = e.clientX; ly = e.clientY; vp.classList.add("dv-grab"); });
    window.addEventListener("mouseup", function () { dragging = false; vp.classList.remove("dv-grab"); });
    vp.addEventListener("mousemove", function (e) {
      if (dragging) {
        var dx = e.clientX - lx, dy = e.clientY - ly; lx = e.clientX; ly = e.clientY;
        moved += Math.abs(dx) + Math.abs(dy);
        self.panX += dx; self.panY += dy; self._apply();
      } else { self._hover(e); }
    });
    vp.addEventListener("mouseleave", function () { self.tip.style.display = "none"; });
    vp.addEventListener("wheel", function (e) {
      e.preventDefault();
      var rect = vp.getBoundingClientRect();
      var mx = e.clientX - rect.left, my = e.clientY - rect.top;
      var factor = Math.exp(-e.deltaY * 0.0016);
      var nz = Math.max(MIN_ZOOM, Math.min(MAX_ZOOM, self.zoom * factor));
      var k = nz / self.zoom;
      // keep the point under the cursor fixed
      self.panX = mx - k * (mx - self.panX);
      self.panY = my - k * (my - self.panY);
      self.zoom = nz; self._apply();
    }, { passive: false });
    this.hud.querySelector(".dv-reset").addEventListener("click", function () { self._fit(); });
    window.addEventListener("resize", function () { self._clampPan(); self._apply(); });
    // fit once the viewport has a size
    requestAnimationFrame(function () { self._fit(); });
  };

  DieViewer.prototype._fit = function () {
    var vp = this.viewport, rect = vp.getBoundingClientRect();
    var z = Math.min(rect.width / this.cw, rect.height / this.ch);
    this.zoom = z <= 0 ? 1 : z;
    this.panX = (rect.width - this.cw * this.zoom) / 2;
    this.panY = (rect.height - this.ch * this.zoom) / 2;
    this._apply();
  };

  DieViewer.prototype._clampPan = function () { /* free pan; no clamp for study use */ };

  DieViewer.prototype._apply = function () {
    this.stack.style.transform = "translate(" + this.panX + "px," + this.panY + "px) scale(" + this.zoom + ")";
  };

  // hover: read the hit buffer at the cursor's die-canvas coordinate
  DieViewer.prototype._hover = function (e) {
    var rect = this.viewport.getBoundingClientRect();
    var vx = e.clientX - rect.left, vy = e.clientY - rect.top;
    var cx = Math.round((vx - this.panX) / this.zoom);
    var cy = Math.round((vy - this.panY) / this.zoom);
    if (cx < 0 || cy < 0 || cx >= this.cw || cy >= this.ch) { this.tip.style.display = "none"; return; }
    var px = this._hitCtx.getImageData(cx, cy, 1, 1).data;
    var id = (px[0] << 16) | (px[1] << 8) | px[2];
    if (id === 0) { this.tip.style.display = "none"; return; }
    var name = (this.L.names && this.L.names[id]) || ("node " + id);
    var cats = (this.nodeCat && this.nodeCat[id]) || [];
    var catHtml = "";
    if (this.hl) {
      catHtml = cats.length
        ? cats.map(function (k) { return '<span class="dv-tip-cat" style="color:' + this.hl.categories[k].color + '">' + this.hl.categories[k].label + '</span>'; }, this).join("")
        : '<span class="dv-tip-none" data-en="not in this mechanism\'s set" data-zh="不在此機制的集合">not flagged</span>';
    }
    this.tip.innerHTML = '<b>' + name + '</b> <span class="dv-tip-id">#' + id + '</span>' + (catHtml ? '<br>' + catHtml : '');
    this.tip.style.display = "block";
    this.tip.style.left = (vx + 14) + "px";
    this.tip.style.top = (vy + 14) + "px";
  };

  DieViewer.prototype._rgba = function (hex, a) {
    var n = parseInt(hex.slice(1), 16);
    return "rgba(" + ((n >> 16) & 255) + "," + ((n >> 8) & 255) + "," + (n & 255) + "," + a + ")";
  };

  function segsCoords(s) { return s.slice(2); } // [node,layer,coords...] -> coords

  // init all viewers on the page
  function init() {
    var hosts = document.querySelectorAll(".die-viewer");
    for (var i = 0; i < hosts.length; i++) new DieViewer(hosts[i]);
  }
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", init);
  else init();

  window.DieViewer = DieViewer;
})();
