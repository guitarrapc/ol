# Stability and Public Output Verification

This document defines the repository-level verification contract that keeps supported ecosystems and user-visible reports from drifting away from the implementation.

## Ecosystem smoke contract

`sandbox/ecosystems/manifest.json` is the repository fixture catalog for package-metadata providers. It is not a second product registry: automated tests require a one-to-one match with the providers registered by Ol, and CI consumes the same catalog as its matrix. Consequently, adding package-manager support is incomplete until a minimal runnable repository fixture exists for that ecosystem.

Each fixture run restores or resolves its real package-manager dependency data, generates a CycloneDX SBOM, runs Ol through the generalized input API with default auto detection in text, Markdown, and JSON formats, and retains those reports as CI artifacts. The assertions require the expected input identity and human-readable input header, a complete JSON inventory, the expected purl ecosystem, successful deduplicated provider scheduling, and no package-registry fetch error. The NuGet fixture additionally scans its restored `project.assets.json` directly as `nuget-assets`, covering package-manager input without reconstructing dependency resolution. Source-repository enrichment remains best-effort as defined by [source.md](source.md).

The fixtures are intentionally owned by this repository instead of cloning arbitrary third-party default branches. This keeps dependency identity explicit and reviewable while avoiding unrelated upstream branch changes from becoming Ol regressions.

## Ol self-scan snapshots

`sandbox/self/` contains the current CycloneDX SBOM for Ol and its text, Markdown, and JSON reports. `sandbox/Update-SelfScan.ps1` is the supported regeneration entry point and scans the generated document through the auto-detected `--input` path. CI regenerates these files with the repository-pinned SBOM generator and fails when the committed snapshots differ, making report-contract and dependency changes visible in an ordinary code review.

The self-scan uses SBOM-only evidence so it is deterministic and does not silently snapshot mutable registry or repository responses. Volatile generator identity fields such as timestamps and random serial numbers are excluded, while dependency identities, licenses, graph relationships, and Ol report content remain reviewable. Full registry behavior is covered independently by the ecosystem smoke contract.
