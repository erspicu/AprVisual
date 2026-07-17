/*
  nes-pad-behavioral — connector-only variant of nes-pad.

  The gate-level nes-pad (CD4021 built from pslatch modules) does not survive this
  engine's group-resolution semantics: the latch pass-gates backdrive the buttons
  nodes and in-group GND beats any external drive, so the latch can never load a
  released (high) button. The controller therefore moves to the behavioral layer
  (same abstraction level as the cartridge): this def keeps only the port pins;
  WireCore's Joypad handler implements the 4021 latch/shift semantics and drives
  d0 directly. Selected at load time by WireCore.EnableJoypadHandler (test mode);
  the default build keeps the original gate-level nes-pad (golden checksum intact).
*/

var module = {

name: "nes-pad",
description: "NES D-pad (behavioral connector)",

pins: [
    [ 1,   'vss'   ],
    [ 2,   'oe'    ],
    [ 3,   'out'   ],
    [ 4,   'd0'    ],
    [ 5,   'vcc'   ],
    [ 6,   'd3'    ],
    [ 7,   'd4'    ],
],

modules: [],

connections: [
    ["d1",  "vcc"],
    ["d2",  "vss"],
    ["d3",  "vss"],
    ["d4",  "vss"],
],

nodenames :
{
    vcc: 1,
    vss: 2,
    out: 3,
    clk: 4,
    oe:  4,
    d0:  5,
    d1:  6,
    d2:  7,
    d3:  8,
    d4:  9,
},

segdefs : [],
transdefs: []
};
