# Top 3 Most Likely Technical Failure Points

## 1. Incorrect Abstraction of Dynamic Nodes and Memory Behavior

This is the core risk of the entire project, and the one most likely to produce results that look reasonable but are not actually correct.

The difficulty comes from the fact that:

- many state elements in switch-level logic are not explicit DFFs
- some nodes retain value by floating and parasitic capacitance
- some nodes are recharged or discharged through pass transistors
- the simplified rule `connected_to_gnd / connected_to_high / hold_previous` is useful, but may not fully capture all real behavior

If this abstraction is wrong, then:

- the CPU evaluator
- the CUDA backend
- the Verilog output

all become faster ways to execute the wrong model.

This is the number one risk because it does not always fail loudly. It can look correct on simple examples while drifting on real 2A03 / 2C02 subregions or edge timing behavior.

## 2. Failure to Correctly Classify Feedback, Shared Buses, and Bidirectional Transmission Structures

The second major risk is oversimplifying a complicated bidirectional network into an ordinary logic tree.

The difficulty comes from:

- bidirectional pass transistors
- shared buses
- loops that are not equivalent to simple latches
- SCC / loop detection finding structure, but not automatically proving the semantic meaning of the structure

If classification fails, common consequences include:

- shared bus islands being split into isolated nodes incorrectly
- feedback regions being mistaken for ordinary combinational logic
- regions that should use `current_state` being force-expanded into huge Boolean expressions

That directly leads to:

- non-convergence
- expression explosion
- semantic mismatch with the real circuit

## 3. Insufficient Validation, Leading to an Impressive but Untrustworthy Result

The third major risk is not the algorithm itself, but a weak validation strategy.

This kind of project can easily fall into a dangerous state where:

- the parser works
- the code generator works
- the CUDA kernel runs
- even local traces "look plausible"

but there is still no real proof that:

- the graph is correct
- the extracted logic is equivalent
- the dynamic-state abstraction is correct
- the CPU and CUDA backends match

One of the biggest risks in this kind of project is completing a lot of engineering work without producing something that can truly be trusted.

## Final Ranking

If the three most likely failure points must be ranked, the order is:

1. incorrect abstraction of dynamic nodes and memory behavior
2. failure to classify feedback / buses / pass-transistor structures correctly
3. insufficient validation

The first two are model-level failures. The third is a methodology failure. If any one of these is unstable, the project can easily become a system that looks impressive but cannot really be trusted.
