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
const WORKLOAD = String(argval('--workload', 'nop')).toLowerCase();   // nop | fuzz | reset

const REF = path.resolve(__dirname, '../../ref/visual6502-master');

// chip -> { netlist data dir, NOP opcode, optional chip-specific support.js }.
// 6502 uses the root macros.js harness directly; 6800 / z80 each ship a support.js that
// OVERRIDES setupTransistors / halfStep / initChip (different clocks + bus protocol), loaded
// last so its definitions win. The driver below is chip-agnostic — it just calls the global
// setupNodes / setupTransistors / initChip / halfStep, which resolve to the right versions.
// jam[] = opcodes that halt/lock the CPU (excluded from fuzz so the PC keeps advancing) —
// per Gemini 2026-06-15: 6502 KIL/JAM x12, 6800 WAI+HCF x5, z80 HALT x1.
const CHIPS = {
  '6502': { dataDir: REF,                        nop: 0xEA, support: null,
            jam: [0x02,0x12,0x22,0x32,0x42,0x52,0x62,0x72,0x92,0xB2,0xD2,0xF2] },
  '6800': { dataDir: path.join(REF, 'chip-6800'), nop: 0x01, support: path.join(REF, 'chip-6800', 'support.js'),
            jam: [0x3E,0x9D,0xDD,0xED,0xFD] },
  'z80':  { dataDir: path.join(REF, 'chip-z80'),  nop: 0x00, support: path.join(REF, 'chip-z80',  'support.js'),
            jam: [0x76] },
};
const cfg = CHIPS[chip];
if (!cfg) { console.error(`unsupported chip '${chip}' (have: ${Object.keys(CHIPS).join(', ')})`); process.exit(2); }

function read(p) { return fs.readFileSync(p, 'utf8'); }

// ---- build a single source blob: data + original core, all VERBATIM ----
const files = [
  ['nodenames.js', path.join(cfg.dataDir, 'nodenames.js')],
  ['segdefs.js',   path.join(cfg.dataDir, 'segdefs.js')],
  ['transdefs.js', path.join(cfg.dataDir, 'transdefs.js')],
  ['chipsim.js',   path.join(REF, 'chipsim.js')],
  ['wires.js',     path.join(REF, 'wires.js')],
  ['macros.js',    path.join(REF, 'macros.js')],
];
// chip-specific harness LAST (overrides setupTransistors / halfStep / initChip / ngnd / npwr)
if (cfg.support) files.push([`support.js (${chip})`, cfg.support]);
const core = files.map(([n, p]) => `/* ===== ${n} ===== */\n${read(p)}`).join('\n');

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

var __WL = ${JSON.stringify(WORKLOAD)};
var __CHIP = ${JSON.stringify(chip)};

// Random Bus Fuzzing: feed a fixed-seed LCG byte on every read instead of memory (max entropy),
// re-rolling past halt/lock opcodes so the CPU keeps executing (real stress, not a jam).
if (__WL === 'fuzz') {
  var __jamArr = ${JSON.stringify(cfg.jam || [])};
  var __jam = {}; for (var __jj = 0; __jj < __jamArr.length; __jj++) __jam[__jamArr[__jj]] = 1;
  var __fz = 0x1357BD2F >>> 0;
  mRead = function(a){ var __b; do { __fz = (Math.imul(__fz, 1664525) + 1013904223) >>> 0; __b = (__fz >>> 16) & 0xFF; } while (__jam[__b]); return __b; };
}

// Reset-Hold clock-only step: toggle just the clock tree, no bus handling (mirrors the C# harness).
function __clockOnly(){
  if (__CHIP === '6800') {
    if (isNodeHigh(nodenames['phi2'])) { setLow('phi2'); setLow('dbe'); setHigh('phi1'); }
    else { setHigh('phi1'); setLow('phi1'); setHigh('phi2'); setHigh('dbe'); }
  } else {
    var __ck = (__CHIP === 'z80') ? 'clk' : 'clk0';
    if (isNodeHigh(nodenames[__ck])) setLow(__ck); else setHigh(__ck);
  }
}

initChip();

// pick the per-half-cycle step; reset-hold re-asserts reset (initChip released it) and runs clock-only
var __step = halfStep;
if (__WL === 'reset') { setLow(nodenamereset); __step = __clockOnly; }

// warm-up (past reset transient + JIT warm)
for (var __i = 0; __i < ${WARMUP}; __i++) { __step(); cycle++; }

// timed rounds — the original macros.js goForN inner loop: step(); cycle++;
__BENCH__.rates = [];
for (var __r = 0; __r < ${ROUNDS}; __r++) {
  var __t0 = process.hrtime.bigint();
  for (var __k = 0; __k < ${HC}; __k++) { __step(); cycle++; }
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
console.log(`#   workload:    ${WORKLOAD === 'fuzz' ? 'Random Bus Fuzzing (fixed-seed LCG)' : WORKLOAD === 'reset' ? 'Reset-Hold (clock tree only)' : 'Infinite NOP Sled (opcode 0x' + cfg.nop.toString(16).toUpperCase() + ')'}`);
console.log(`#   per round:   ${HC.toLocaleString()} half-cycles, warmup ${WARMUP.toLocaleString()}, ${ROUNDS} rounds`);
console.log('#   ' + '-'.repeat(50));
rates.forEach((r, i) => console.log(`#   round ${i + 1}: ${Math.round(r).toLocaleString()} hc/s`));
console.log('#   ' + '-'.repeat(50));
console.log(`#   median: ${Math.round(median).toLocaleString()} hc/s   best: ${Math.round(best).toLocaleString()}   mean: ${Math.round(mean).toLocaleString()}`);
console.log(`#   (total load+run wall time: ${loadAndRunSecs.toFixed(1)} s)`);
