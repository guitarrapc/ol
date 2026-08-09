# Backlog

This document tracks ideas that are intentionally outside the current v1, v2, and v3 specifications. Items here are not committed behavior until promoted into the relevant spec and implementation plan.

## Policy and Enforcement

Implemented and specified in [cli.md](specs/cli.md#contract-policy-checks): allow-list checking that fails closed, a [baseline](specs/cli.md#contract-policy-baseline) of acknowledged unresolved components, and [persisted-report evaluation](specs/cli.md#contract-policy-report-input).

Remaining:

- Consider richer policy categories such as `deny`, `review`, `notice_required`, `source_disclosure_required`, and `copyleft_review`. Deliberately deferred: a deny-list reduces noise rather than adding detection, and a baseline removes noise more precisely. Deny becomes meaningful only if acknowledgement is ever widened beyond unresolved components, where it would act as a floor that acknowledgement cannot cross.
- License curation, that is recording that an upstream claim is factually wrong and what the correct license is. Not needed for a pass/fail verdict, which a baseline already covers, so it is justified only by verdict accuracy: a baseline can record that a reviewer accepted unresolved evidence, but not that the evidence itself is wrong.
- Per-package policy exceptions with owner and expiry, distinct from factual correction.

## Non-Public Registry Handling

Resolved: a registry `404` contributes unknown evidence rather than a collection error, so a package published only to a private feed can be acknowledged into a baseline in every ecosystem. Cargo and Bundler additionally withhold a public-registry identity from packages their input records as coming from another source, which avoids the request entirely.

Deliberately not done: deriving the origin from a lockfile download URL for npm, pnpm, or Composer. A corporate proxy serves public packages from an internal host, so the host would misclassify an entire proxied dependency tree as private and silently disable enrichment for it. A public Composer package likewise records a GitHub `dist.url`, which says nothing about Packagist membership.

Remaining:

- Negative cache entries. A private package is requested on every run because failures are not cached, so a persistent "not published here" record would remove the repeated request. This needs a cache schema decision in [cache_format.md](specs/cache_format.md), including how such an entry expires when a package is later published.

## Additional Output Formats

Implemented: [SARIF](specs/cli.md#contract-policy-sarif) for code scanning and CI annotations, and a [report diff](specs/cli.md#contract-diff) in text and JSON.

Remaining:

- SPDX JSON output with scan results attached or mapped back to SPDX package fields.
- CycloneDX output with scan results attached through properties or annotations.
- CSV output for spreadsheet review.
- HTML output for human-readable audit reports.

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

## SPDX Full License Name Matching

Ol resolves an SPDX License Identifier but not the SPDX full license name, although the SPDX license list publishes both in the same record. `uri-template@1.3.0` declares `MIT License`, which is exactly the `name` SPDX gives `MIT`, and it is reported ambiguous.

- Consider matching a declared value against the SPDX license list's own `name` field, exactly, as a second lookup after the identifier. This resolves a name SPDX itself defines rather than guessing at a spelling.
- This deliberately does not resolve values that only resemble a license name. `Apache 2.0`, `Modified BSD License`, `PSFL`, and `Dual License` are not SPDX names, and PyPI Trove classifiers such as `License :: OSI Approved :: BSD License` and `License :: OSI Approved :: Apache Software License` name a family without a version, so all of them must stay ambiguous.
- Cost is not only the lookup: names must be added to the generated license data and to the `spdx update` parsing path, and name collisions with deprecated identifiers need a rule before this can be accepted.

## Repository Detection Versus Declared Conjunctions

`unicode-ident@1.0.24` declares `(MIT OR Apache-2.0) AND Unicode-3.0` on crates.io and its repository root reports `Apache-2.0`, so every Rust project that reaches it reports one conflict. Reconciliation already treats a repository detector's single answer as satisfying a declared disjunction, on the stated ground that a detector names one option out of several by construction. The same limitation applies to a conjunction — the GitHub License API cannot express `AND` at all — but a conjunction currently becomes a disagreement, which is [pinned behavior](specs/spdx.md#contract-expression-agreement) rather than an oversight — the measurement that produced the shallow relation recorded this exact case as the one conflict it left standing.

- Consider treating a detector answer that occurs as a term of the declared expression as corroboration, keeping the declared expression as the result. Allow-list evaluation is unaffected because the declared expression is the stricter one and is what is retained.
- The rule must keep `Apache-2.0 WITH LLVM-exception OR MIT` versus `Apache-2.0` a conflict: a `WITH` expression is one term, and the bare identifier is not among the options offered.
- Left open deliberately: it weakens the one signal that says valid sources disagree, and the current behavior was chosen with that trade-off in view.

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
