# Stability and Public Output Verification

This document defines the repository-level verification contract that keeps supported ecosystems and user-visible reports from drifting away from the implementation.

## Ecosystem smoke contract

`sandbox/ecosystems/manifest.json` is the repository fixture catalog for package-metadata providers. It is not a second product registry: automated tests require a one-to-one match with the providers registered by Ol, and CI consumes the same catalog as its matrix. Consequently, adding package-manager support is incomplete until a minimal runnable repository fixture exists for that ecosystem.

Each fixture run restores or resolves its real package-manager dependency data, generates a CycloneDX SBOM, runs Ol through the generalized input API with default auto detection in text, Markdown, and JSON formats, and retains those reports as CI artifacts. The assertions require the expected input identity and human-readable input header, a complete JSON inventory, the expected purl ecosystem, successful deduplicated provider scheduling, and no package-registry fetch error. The NuGet, Composer, Ruby, and Maven fixtures additionally scan their restored package-manager inputs directly as `nuget-assets`, `composer-lock`, `bundler-lock`, and `maven-dependency-tree`, covering those adapters without reconstructing dependency resolution. Source-repository enrichment remains best-effort as defined by [source.md](source.md).

The fixtures that scan both an SBOM and their restored package-manager input additionally scan the two together and assert the [combination contract](cli.md#contract-input-combination): the collection reports the `collection` input kind, every component names its supply, and the combined component set contains everything each single-input scan reported. Those are properties rather than counts. Resolution rates move with registry responses and SPDX license-list versions, so asserting them here would fail on days Ol did not change; the 5-ecosystem resolution measurement stays a deliberate exercise rather than a CI gate.

The fixtures are intentionally owned by this repository instead of cloning arbitrary third-party default branches. This keeps dependency identity explicit and reviewable while avoiding unrelated upstream branch changes from becoming Ol regressions.

## Ol self-scan snapshots

`sandbox/self/` contains a fixed CycloneDX SBOM for Ol and its text, Markdown, and JSON golden reports. `sandbox/Update-SelfScan.ps1` is the supported regeneration entry point and scans the document through the auto-detected `--input` path. CI treats the committed SBOM as the golden input, regenerates only the three reports with `-ReportsOnly`, and fails when those reports differ. This makes report-contract changes visible in an ordinary code review without coupling the snapshots to the runner's installed SDK.

CI also generates a separate live self-scan of `src/Ol/Ol.csproj` with the latest .NET SDK selected by `10.0.x`. The live output is stored only as a CI artifact rather than compared byte-for-byte. CI validates both the non-empty CycloneDX dependency inventory and its canonical JSON scan report, then passes that report unchanged to `ol check --report ... --allow-licenses MIT`. `check` evaluates the persisted report and excludes its first-party root from policy evaluation, so CI covers the distributable dependencies without reconstructing or rescanning the input. This preserves latest-SDK and current product-dependency coverage while keeping the golden report contract deterministic.

Both paths use SBOM-only evidence and do not silently snapshot mutable registry or repository responses. Volatile generator identity fields such as timestamps and random serial numbers are excluded. Full registry behavior is covered independently by the ecosystem smoke contract.
