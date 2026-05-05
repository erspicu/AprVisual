# 14-Day Action Plan

## Goal

This plan is not meant to finish the project in 14 days. It is meant to establish a real starting point in two weeks. The core goals are:

- narrow the scope
- start building the knowledge foundation
- create the minimal implementation skeleton
- leave behind verifiable outputs

## Day 1

- write down the first-stage project goal explicitly
- constrain it to "parser + graph + evaluator for a local 2A03 region"
- write down a clear "not doing yet" list: PPU, mapper, fully in-GPU, performance tuning

Output:

- a one-page scope statement

## Day 2

- read through the basic Visual6502 / Visual2A03 data format
- identify what `segdefs.js` and `transdefs.js` each contain
- list the fields you truly need to preserve

Output:

- parser field notes

## Day 3

- define `NetNode`, `NetTransistor`, and `NetlistGraph`
- write the minimum C# class skeletons
- do not chase full functionality yet; only lock down the data model

Output:

- basic graph-model classes

## Day 4

- write a minimal parser prototype
- aim only to convert a small amount of data into nodes and transistors
- validate that counts and basic links are correct

Output:

- parser prototype

## Day 5

- create handcrafted inverter / NAND / pass-transistor test netlists
- do not depend on real source data yet
- first prove that your graph and evaluator ideas work on minimal examples

Output:

- handcrafted test cases

## Day 6

- write a conduction analyzer
- only handle basic source/drain connectivity first
- implement connected-component search

Output:

- first conduction analyzer

## Day 7

- write a minimal CPU evaluator
- use the simplified rule:
  - connected to GND -> low
  - connected to high -> high
  - else hold previous

Output:

- first evaluator

## Day 8

- write the settle loop
- support iterate-until-converged
- record iteration counts

Output:

- first settle engine

## Day 9

- produce traces for the handcrafted cases
- compare them manually with expected behavior
- fix the most obvious graph / conduction mistakes

Output:

- first trace set

## Day 10

- start handling real source data
- choose a very small 2A03 region, not the full CPU
- only complete parse and graph dump at first

Output:

- graph output for one real local region

## Day 11

- run conduction and evaluator logic on the real local region
- do not require perfection yet, only that it runs, traces, and can be inspected

Output:

- first trace for a real region

## Day 12

- add minimal loop / SCC analysis
- at least identify obvious feedback regions
- record which nodes may need `current_state`

Output:

- first loop-detection result

## Day 13

- organize everything produced during these two weeks
- separate:
  - what is already demonstrated
  - what is still only a hypothesis
  - what should be done next

Output:

- a two-week review

## Day 14

- decide the single main track for the next month
- I recommend choosing only one of these:
  - strengthen the parser
  - strengthen evaluator correctness
  - strengthen real local-region validation

Output:

- one clear next-month direction

## What You Should Have After 14 Days

If these 14 days go well, you will not have a finished system. But you should have:

- a clearly narrowed scope
- a basic graph model
- a minimal parser
- a minimal evaluator
- a set of handcrafted test cases
- at least one real local-region graph or trace
- a clear judgment about your immediate next step

## The Most Important Principle Behind This Plan

Do not spend these 14 days chasing the final architecture.

The real task of these 14 days is not to show off. It is to:

- build foundations
- prove that you can really keep moving forward
- turn the project from an abstract idea into something with artifacts, traces, and logic
