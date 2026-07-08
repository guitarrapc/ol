# ol - Project Instructions

## Working Principles

### Think Before Coding

Don't assume. Don't hide confusion. Surface tradeoffs.

- State assumptions explicitly. If uncertain, ask.
- If multiple interpretations exist, present them — don't pick silently.
- If a simpler approach exists, say so. Push back when warranted.
- If something is unclear, stop. Name what's confusing. Ask.

### Minimal & Surgical Changes

Minimum code that solves the problem. Touch only what you must.

- No features, abstractions, or error handling beyond what was asked.
- Don't "improve" adjacent code, comments, or formatting. Match existing style.
- Remove only imports/variables/functions that YOUR changes made unused.
- Every changed line should trace directly to the user's request.

### Goal-Driven Execution

Define success criteria. Loop until verified.

- "Add validation" → "Write tests for invalid inputs, then make them pass"
- "Fix the bug" → "Write a test that reproduces it, then make it pass"
- "Lesson learned" → "Write a lesson learned to plan, then next time avoid the same mistake"

For multi-step tasks, state a brief plan with verification per step.

### Data-Oriented Design

Prefer data-oriented design with explicit types and explicit side-effect boundaries.

- Model domain state with simple typed data structures first; keep transformations deterministic.
- Make type intent obvious at API boundaries (input/output, ownership, nullability, lifetimes).
- Isolate side effects (I/O, network, time, environment access) to clear boundary layers so core parsing/linting logic remains pure where practical.
- Avoid over-OOP abstractions: do not add inheritance, service layers, or interface indirection unless there is a measured and recurring need.
- Prefer value/data modeling (`struct`, `record struct`, plain data classes) and explicit control flow over deep class hierarchies or behavior-heavy objects.
- Keep polymorphism at narrow extension points only (for example, rule/plugin boundaries). Do not spread dynamic dispatch across core hot paths.

## What is this project?

ol is a command-line tool for license compliance and SPDX validation. It scans SBOMs, package metadata, and GitHub License to produce a license report.
