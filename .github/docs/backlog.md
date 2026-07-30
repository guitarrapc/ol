# Backlog

This document tracks ideas that are intentionally outside the current v1, v2, and v3 specifications. Items here are not committed behavior until promoted into the relevant spec and implementation plan.

## Policy and Enforcement

Implemented and specified in [cli.md](specs/cli.md#contract-policy-checks): allow-list checking that fails closed, an acknowledgement [baseline](specs/cli.md#contract-policy-baseline) for reviewed unresolved components, and [persisted-report evaluation](specs/cli.md#contract-policy-report-input).

Remaining:

- Consider richer policy categories such as `deny`, `review`, `notice_required`, `source_disclosure_required`, and `copyleft_review`. Deliberately deferred: a deny-list reduces noise rather than adding detection, and the baseline removes noise more precisely. Deny becomes meaningful only if acknowledgement is ever widened beyond unresolved components, where it would act as a floor that acknowledgement cannot cross.
- License curation, that is recording that an upstream claim is factually wrong and what the correct license is. Not needed for a pass/fail verdict, which acknowledgement already covers; it becomes required for NOTICE generation, which needs an actual license value.
- Per-package policy exceptions with owner and expiry, distinct from factual correction.

## Additional Output Formats

Implemented: [SARIF](specs/cli.md#contract-policy-sarif) for code scanning and CI annotations, and a [report diff](specs/cli.md#contract-diff) in text and JSON.

Remaining:

- SPDX JSON output with scan results attached or mapped back to SPDX package fields.
- CycloneDX output with scan results attached through properties or annotations.
- CSV output for spreadsheet review.
- HTML output for human-readable audit reports.

## Redistribution Artifacts

- Generate `THIRD-PARTY-NOTICES` from resolved facts, including original license text, attribution, and an explicit list of components whose text could not be obtained.
- Requires license text collection and license curation first. Generic text substituted from an SPDX template would drop package-specific additional terms, so it must never be a default.
- An `OR` license choice must be an explicit user input, never inferred from which branch an allow-list happened to accept. Recording an inferred choice would make Ol assert a licensing election on the user's behalf.

## SBOM Generation

- Provide optional SBOM generation wrappers after the scan behavior is stable.
- Consider wrapping Syft as an initial generator.
- Consider ecosystem-specific generators such as CycloneDX for .NET, Cargo, npm, Go, and other package managers.
- Keep SBOM generation separate from core scan semantics so scan results remain explainable and reproducible.

## GitHub Actions

- Provide a GitHub Action wrapper for common CI usage.
- Keep SBOM generation and license scanning responsibilities explicit in Action inputs.
- Consider emitting Markdown summaries for pull requests and job summaries.
- Consider SARIF upload support if policy checking is added.

## Package and Ecosystem Expansion

- Add Maven package metadata support after the initial v2 ecosystems.
- Evaluate other ecosystems based on purl support and registry metadata quality.
- Consider whether lockfiles or manifests should be used as supplemental evidence for direct dependency classification or reproducibility checks.

## Source Repository Expansion

- Add GitHub Contents API fallback for root `LICENSE`, `COPYING`, and `NOTICE` files if GitHub License API evidence is insufficient.
- Consider recursive or path-specific license discovery only if there is a clear audit need.
- Consider GitHub Enterprise Server support with explicit host/API configuration.
- Consider source archive inspection only if repository API hints are insufficient.

## Reproducibility Metadata

- Record more SBOM generation conditions when available, such as generator name/version, build target, platform, lockfile hash, commit hash, and dependency scope.
- Decide how much generation metadata belongs in scan reports versus upstream SBOM documents.

## Dependency Scope Policy (`--allow-dev-licenses`)

- Development usage is classified for npm, pnpm, Composer, Maven (`test` scope), and Cargo (dev-only reachability). Every supported adapter derives usage from data inside the resolved input itself.
- Yarn was implemented and then withdrawn. `yarn.lock` records no development scope, so classification required joining the lockfile with a sibling `package.json` by **package name** — a weak key, because Yarn resolves by descriptor (`name@range`) and descriptors are only available during parsing, not in the inventory. That join produced three fail-open or misleading defects in review: seeding every same-named entry (admitting entries the manifest never declared), then, after making seeding unambiguous-only, shrinking production reachability so a dependency of an ambiguous production declaration became development-only, plus reporting every entry as runtime when the manifest matched nothing. Correctness needs asymmetric approximation (over-approximate production, under-approximate development), which is subtle and easy to invert. Dropping costs Yarn users only convenience — usage stays unknown and they keep using the primary allow-list — while keeping it risked admitting licenses in a compliance gate. Revisit only with descriptor-precise matching: `name@range` against the entry's descriptor list for Classic, and the workspace entry's dependency list for Berry. Workspaces remain out of scope regardless: discovering each workspace's manifest from untrusted `workspaces` globs is a path-traversal and filesystem-enumeration surface.
- Ecosystems that leave usage unknown (fail-closed) because their standard input records no development scope: CycloneDX/SPDX, Yarn `yarn.lock`, NuGet `project.assets.json`, Go module graph, pip inspect, and Bundler `Gemfile.lock` (its groups are not in the lock).

## Review Notes

- Items should move out of this backlog only when their WHAT/WHY are added to a spec and their detailed work is added to an implementation plan.
- Avoid treating backlog items as implicit product commitments.
