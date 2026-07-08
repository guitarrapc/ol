# Backlog

This document tracks ideas that are intentionally outside the current v1, v2, and v3 specifications. Items here are not committed behavior until promoted into the relevant spec and implementation plan.

## Policy and Enforcement

- Add allow-list policy checking after the v1-v3 scan phases.
- Define a `check` command or equivalent policy phase that fails closed for allow-list misses, unknown licenses, conflicts, ambiguous values, and invalid SPDX expressions.
- Consider richer policy categories such as `deny`, `review`, `exception`, `notice_required`, `source_disclosure_required`, and `copyleft_review`.
- Define how human review results or project-specific exceptions are stored and audited.

## Additional Output Formats

- SPDX JSON output with scan results attached or mapped back to SPDX package fields.
- CycloneDX output with scan results attached through properties or annotations.
- SARIF output for code scanning and CI annotations.
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

## Source Repository Expansion

- Add GitHub Contents API fallback for root `LICENSE`, `COPYING`, and `NOTICE` files if GitHub License API evidence is insufficient.
- Consider recursive or path-specific license discovery only if there is a clear audit need.
- Consider GitHub Enterprise Server support with explicit host/API configuration.
- Consider source archive inspection only if repository API hints are insufficient.

## Reproducibility Metadata

- Record more SBOM generation conditions when available, such as generator name/version, build target, platform, lockfile hash, commit hash, and dependency scope.
- Decide how much generation metadata belongs in scan reports versus upstream SBOM documents.

## Review Notes

- Items should move out of this backlog only when their WHAT/WHY are added to a spec and their detailed work is added to an implementation plan.
- Avoid treating backlog items as implicit product commitments.
