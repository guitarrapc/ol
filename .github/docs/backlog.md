# Backlog

This document tracks ideas that are intentionally outside the current v1, v2, and v3 specifications. Items here are not committed behavior until promoted into the relevant spec and implementation plan.

## Policy and Enforcement

Implemented and specified in [cli.md](specs/cli.md#contract-policy-checks): allow-list checking that fails closed, a [baseline](specs/cli.md#contract-policy-baseline) of acknowledged unresolved components, and [persisted-report evaluation](specs/cli.md#contract-policy-report-input).

Remaining:

- Consider richer policy categories such as `deny`, `review`, `notice_required`, `source_disclosure_required`, and `copyleft_review`. Deliberately deferred: a deny-list reduces noise rather than adding detection, and a baseline removes noise more precisely. Deny becomes meaningful only if acknowledgement is ever widened beyond unresolved components, where it would act as a floor that acknowledgement cannot cross.
- License curation, that is recording that an upstream claim is factually wrong and what the correct license is. Not needed for a pass/fail verdict, which a baseline already covers, so it is justified only by verdict accuracy: a baseline can record that a reviewer accepted unresolved evidence, but not that the evidence itself is wrong.
- Per-package policy exceptions with owner and expiry, distinct from factual correction. Currently [outside the `check` contract](specs/cli.md#contract-policy-checks); the earlier design document was removed and would need rewriting.

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

- Decide whether a GitHub Action is a dependency Ol resolves. A general-purpose scanner catalogues the actions a repository's workflows use, so `pkg:github/<owner>/<repo>` reaches almost any SBOM of a repository: measuring eighteen projects, it appeared in fifteen of them, 235 components in all, none resolvable. Resolution itself would be mechanical, because the namespace and name are the repository, and Ol already collects licenses from GitHub. The open question is scope, not feasibility — third-party code a pipeline executes is a compliance subject under one reading and not a library dependency under another. Until it is decided, Ol reports them as `unsupported_package_metadata`, which is accurate, and `--exclude-packages pkg:github/` drops them in one entry.
- Add Maven package metadata support after the initial v2 ecosystems.
- Survey Dart Pub. `pubspec.lock` is the only resolved-input ecosystem identified during the adapter work that was never evaluated, so its determinism, context model, and allocation floor are unknown. Adoption criteria are the ones every adopted adapter met: a machine-readable resolved graph the tool itself emits, contexts expressible without inferring anything from the scanning host, and no Native AOT dependency.
- Evaluate other ecosystems based on purl support and registry metadata quality.
- Consider whether lockfiles or manifests should be used as supplemental evidence for direct dependency classification or reproducibility checks.

## Free-Text License Values

Implemented: exact matching against the SPDX license list's own [`name`](specs/spdx.md#contract-spdx-license-name) field, which resolves `MIT License` and `Apache License 2.0`; and recognition of the PyPI [license family classifiers](specs/spdx.md#contract-license-family-classifier) PEP 639 excludes from inference, which resolve nothing but explain why.

Remaining, all deliberately unresolved:

- Mapping the PyPI classifiers PEP 639 says do correspond to one identifier. Deferred on measurement rather than principle: every classifier appearing in the 15-project evaluation corpus is on PEP 639's excluded list, so a table would have resolved nothing there. Reconsider if a corpus shows packages whose only license evidence is a specific classifier. It needs roughly 45 curated entries, because only 29 of PyPI's 89 license classifiers are derivable from the SPDX name they contain; the vocabulary is frozen, so such a table would not rot.
- Near-miss spellings (`Apache 2.0`, `Modified BSD License`, `PSFL`) name no SPDX record, and PEP 639 forbids converting the free-text `License` field without affirmative user action. Resolving them needs a curated alias table, which is guessing with extra steps and belongs with license curation above.
- Values that state a relationship without an operator (`Dual License`), and tool placeholders (`Unknown - See URL`), name no licenses at all. Together these are 68 of the 104 unresolved free-text occurrences measured, and no rule can resolve them because the fact is absent from the field.

## Warning Vocabulary Budget

`LicenseCandidateWarnings` is one bit per warning in a `ushort`, so the vocabulary is a bounded resource. Retiring the three NuGet license warnings returned three bits and left thirteen in use.

- Before adding a warning, check whether the report already carries the fact in typed form. The retired three each restated one `DeclaredLicenseReferenceKind` for one ecosystem, which is why they were derivable and why the same fact went unreported in Cargo, PyPI, and CocoaPods.
- A warning earns a bit when it records an outcome nothing else states: a collection that failed, was refused, or was never attempted.
- `WarningVocabulary_EveryFlag_RoundTripsAndFitsItsStorage` fails if the set outgrows its storage or two flags share an identifier. Widen the enum deliberately rather than in passing.

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
