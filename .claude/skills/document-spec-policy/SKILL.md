---
name: document-spec-policy
description: Policy for reading and writing specification documents under `.github/docs/`. Covers what belongs in specs (WHAT, WHY, lessons learned), what does not (detailed HOW), post-implementation updates, and cross-document consistency rules between parser specs and downstream documents.
---

# Specification Document Policy

Spec files live under `.github/docs`. When reading or writing them, follow these rules (see `.github/docs/docs_authoring_guidelines.md` for the full authoring reference).

## What Belongs in a Spec

- **WHAT** — what the feature or behavior is
- **WHY** — the reasoning and motivation behind the decision
- **Lessons learned** — things that were only discovered by actually trying (e.g., unexpected constraints, failed approaches, surprising behavior)

## What Does NOT Belong in a Spec

- Detailed HOW — step-by-step implementation instructions, code structure, algorithm internals. Those belong in code comments, `.claude/skills/`, or the implementation itself.

## After Implementing

- Always update any related spec files to reflect what was actually built, especially documenting lessons learned or decisions made during implementation that weren't captured upfront.

## Cross-Document Consistency

- `.github/docs/specs/spec_xxxxx.md` is the **source of truth** for the parser specification. When it is revised, you **must** review and update the following downstream documents for consistency:
- Conversely, if a downstream doc is updated with new implementation details or lessons learned, check whether the change implies a spec-level update to `.github/docs/specs/spec_xxxxx.md`.
