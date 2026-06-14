/*
 * visual6502-node/bench.js — headless Node.js benchmark of the ORIGINAL visual6502
 * JavaScript simulator (chipsim.js + wires.js + macros.js), run from the console.
 *
 * Purpose: measure the pristine visual6502 algorithm's throughput (half-cycles/s) as a
 * REFERENCE BASELINE to compare against AprVisual S1/etc. The original sim is a browser
 * app; we load its core files VERBATIM and only stub out the DOM/UI functions, then drive
 * it with the original macros.js halfStep / handleBusRead / handleBusWrite over a flat
 * memory[] NOP-sled (the synthetic "Infinite NOP Sled" benchmark — no test ROM needed).
 *
 *   node tools/visual6502-node/bench.js [--chip 6502] [--hc 1000000] [--warmup 200000] [--rounds 5]
 *
 * Data is read from ref/visual6502-master/ (the gitignored upstream copy).
 * Currently implements the 6502 (root chipsim.js/wires.js/macros.js match it exactly);
 * 6800 / z80 use chip-specific support.js harnesses (different bus protocol) — TODO.
 *
 * NB this is a fidelity baseline, NOT our engine: chipsim.js is the simple recursive
 * group-walk (the slow original), not the optimized expertWires.js. Report it as such.
 */

'use strict';
const fs = require('fs');
const path = require('path');
const vm = require('vm');

// ---- args ----
function argval(flag, def) {
  const i = process.argv.indexOf(flag);
  return (i >= 0 && i + 1 < process.argv.length) ? process.argv[i + 1] : def;
}
const chip    = argval('--chip', '6502');
// NB the original chipsim.js (recursive group-walk) is VERY slow (~250 hc/s on a 6502!) —
// magnitudes are tiny on purpose; the rate is so stable that a few thousand hc suffices.
const HC      = parseInt(argval('--hc', '8000'), 10);
const WARMUP  = parseInt(argval('--warmup', '2000'), 10);
const ROUNDS  = parseInt(argval('--rounds', '3'), 10);

const REF = path.resolve(__dirname, '../../ref/visual6502-master');

// chip -> { dir for netlist data, NOP opcode, reset/clock node names }
const CHIPS = {
  '6502': { dataDir: REF, nop: 0xEA },
  // '6800': { dataDir: path.join(REF,'chip-6800'), nop: 0x01 },  // TODO: chip-6800/support.js harness
  // 'z80' : { dataDir: path.join(REF,'chip-z80'),  nop: 0x00 },  // TODO: chip-z80/support.js harness
};
const cfg = CHIPS[chip];
if (!cfg) { console.error(`unsupported chip '${chip}' (have: ${Object.keys(CHIPS).join(', ')})`); process.exit(2); }

function read(p) { return fs.readFileSync(p, 'utf8'); }

// ---- build a single source blob: data + original core, all VERBATIM ----
const core = [
  ['nodenames.js', read(path.join(cfg.dataDir, 'nodenames.js'))],
  ['segdefs.js',   read(path.join(cfg.dataDir, 'segdefs.js'))],
  ['transdefs.js', read(path.join(cfg.dataDir, 'transdefs.js'))],
  ['chipsim.js',   read(path.join(REF, 'chipsim.js'))],
  ['wires.js',     read(path.join(REF, 'wires.js'))],
  ['macros.js',    read(path.join(REF, 'macros.js'))],
].map(([n, s]) => `/* ===== ${n} ===== */\n${s}`).join('\n');

// ---- headless driver appended to the SAME scope (uses the original functions) ----
const driver = `
/* ===== headless driver (stub UI, NOP-sled, time the original halfStep) ===== */
// stub every DOM/UI function the called code touches, AFTER the verbatim load
refresh = function(){};
chipStatus = function(){};
setStatus = function(){};
selectCell = function(){};
setCellValue = function(){};
updateLogbox = function(){};
hiliteNode = function(){};

setupNodes();
setupTransistors();

// "Infinite NOP Sled": fill all of memory with the chip's NOP opcode (no ROM).
// Reset vector reads NOP too -> PC starts mid-sled and increments forever.
for (var __a = 0; __a < 65536; __a++) memory[__a] = ${cfg.nop};

var __nodeCount = 0; for (var __n in nodes) if (nodes[__n] != undefined) __nodeCount++;
var __transCount = 0; for (var __t in transistors) __transCount++;
__BENCH__.nodeCount = __nodeCount;
__BENCH__.transCount = __transCount;

initChip();

// warm-up (past reset transient + JIT warm)
for (var __i = 0; __i < ${WARMUP}; __i++) { halfStep(); cycle++; }

// timed rounds — the original macros.js goForN inner loop: halfStep(); cycle++;
__BENCH__.rates = [];
for (var __r = 0; __r < ${ROUNDS}; __r++) {
  var __t0 = process.hrtime.bigint();
  for (var __k = 0; __k < ${HC}; __k++) { halfStep(); cycle++; }
  var __t1 = process.hrtime.bigint();
  var __secs = Number(__t1 - __t0) / 1e9;
  __BENCH__.rates.push(${HC} / __secs);
}
`;

// ---- run it all in one context ----
const sandbox = {
  console,
  process,
  __BENCH__: { rates: [], nodeCount: 0, transCount: 0 },
  // minimal DOM stub for any incidental getElementById in unused code paths
  document: { getElementById: () => ({ style: {}, getContext: () => ({}) }) },
  // a couple of browser globals referenced by name in unused functions
  XMLHttpRequest: function () {},
};
sandbox.window = sandbox;
vm.createContext(sandbox);

const t0 = Date.now();
vm.runInContext(core + '\n' + driver, sandbox, { filename: 'visual6502-bench' });
const loadAndRunSecs = (Date.now() - t0) / 1000;

// ---- report ----
const rates = sandbox.__BENCH__.rates.slice().sort((a, b) => a - b);
const median = rates[Math.floor(rates.length / 2)];
const best = rates[rates.length - 1];
const mean = rates.reduce((a, b) => a + b, 0) / rates.length;

console.log('');
console.log(`# visual6502 (original JS, chipsim.js recursive group-walk) — Node ${process.version}`);
console.log(`#   chip:        ${chip}`);
console.log(`#   nodes:       ${sandbox.__BENCH__.nodeCount}`);
console.log(`#   transistors: ${sandbox.__BENCH__.transCount}`);
console.log(`#   workload:    Infinite NOP Sled (opcode 0x${cfg.nop.toString(16).toUpperCase()})`);
console.log(`#   per round:   ${HC.toLocaleString()} half-cycles, warmup ${WARMUP.toLocaleString()}, ${ROUNDS} rounds`);
console.log('#   ' + '-'.repeat(50));
rates.forEach((r, i) => console.log(`#   round ${i + 1}: ${Math.round(r).toLocaleString()} hc/s`));
console.log('#   ' + '-'.repeat(50));
console.log(`#   median: ${Math.round(median).toLocaleString()} hc/s   best: ${Math.round(best).toLocaleString()}   mean: ${Math.round(mean).toLocaleString()}`);
console.log(`#   (total load+run wall time: ${loadAndRunSecs.toFixed(1)} s)`);
