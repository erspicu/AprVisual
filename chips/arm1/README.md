# ARM1 Netlist Snapshot

Downloaded from the Visual 6502 ARM1 gate-level simulator on 2026-07-23.

Source page:

- http://www.visual6502.org/sim/varm/armgl.html

Source data files:

| File | Purpose | Source URL | SHA-256 |
| --- | --- | --- | --- |
| `transdefs.js` | ARM1 transistor netlist records. | http://www.visual6502.org/sim/varm/transdefs.js | `40D82906C86982C9F23047E908BFEB0AD15E87A1F0BEFD2A222D58D1A2632F9B` |
| `nodenames.js` | Node ID to signal-name map. | http://www.visual6502.org/sim/varm/nodenames.js | `176B50DDD0C6AEB42A99C0BDD752FAC44E17A27173434132B7B14552815AF602` |
| `ffdefs.js` | Auxiliary flip-flop definition data used by the ARM1 simulator. | http://www.visual6502.org/sim/varm/ffdefs.js | `5A15ABF9E924783CDA05AA5DCF0E6EFF5FD8A0A580922BAC54B9D89A0EDC32E9` |

The three JavaScript files are copied verbatim from their source URLs. Their
SHA-256 values were verified against a fresh download after retrieval.

This snapshot intentionally excludes WebGL geometry, image, UI, and simulator
runtime files because they are not required to consume the circuit netlist.
Original ownership and licensing remain with the source publisher.
