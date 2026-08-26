---
name: license-scan
description: Scan dependency licenses with ol by combining resolved package-manager inputs with an optional CycloneDX/SPDX SBOM, judge coverage, then enforce the intended SPDX policy with check, reviewed baselines, and CI. Supports single-language, polyglot, and monorepo builds, including input alignment, evidence collection, unresolved-result diagnosis, baseline composition, and license-regression detection.
---

# ol License Scan

Scan what the build actually resolved. Judge coverage before reading licenses. Apply policy last.

Repositories are polyglot until proven otherwise: find every in-scope ecosystem before choosing inputs.

## Lifecycle

```text
ol scan --input . --format json > ol-report.json
ol check --report ol-report.json --allow-licenses <approved-SPDX-ids>
```

1. **Scan** into canonical JSON and keep it. Every later step reads that file, not a new scan.
2. **Judge coverage.** Exit `0` means the command ran, not that the audit is complete.
3. **Check without a baseline** so every violation is visible.
4. **Triage by population**, not row by row.
5. **Baseline** only reviewed evidence that stays unresolved.
6. **Verify** steady state, then run the same check in CI.

Read [references/policy-workflow.md](references/policy-workflow.md) before creating or updating a baseline.

## Scope and inputs

1. Run `ol scan --help` as the version-specific option reference.
2. Name the auditable subject: a release artifact, application, workspace, subtree, or whole repository. Ask when the choice changes compliance meaning.
3. Find every ecosystem in that subject, including inside ignored build directories. Do not stop at the first one.
4. Read [references/ecosystem-inputs.md](references/ecosystem-inputs.md) for each ecosystem's supported input and how to produce it.

`ol scan --input .` discovers every supported resolved input recursively and is the normal starting point. It also finds test fixtures, vendored trees, and unrelated sample projects, which are not dependencies of the subject. The `Input discovery` line is the detector: an ecosystem you did not expect, or a detected-file count far above the number of projects you know about, means the scan reached beyond the subject. Narrow it with `--exclude-input-path` or explicit paths.

Never pass an unresolved manifest alone — `package.json`, `Cargo.toml`, `*.csproj`, `go.mod`, requirements files. They state requests, not the graph the build selected. ol reports such a file as an ignored candidate and names the command that produces the real input; act on that rather than scanning the manifest.

### Adding an SBOM

An SBOM is a second input, not a replacement. Add one when ol has no adapter for an ecosystem in the subject, or when CI must not install that ecosystem's resolver.

Where a resolved input exists, it is the stronger evidence: only a resolved input records the installed location that lets ol read licenses out of package artifacts. Adding an SBOM there usually changes nothing and can add components that are not dependencies — see [references/sbom-generators.md](references/sbom-generators.md).

ol accepts at most one SBOM per scan, and every input must describe the same commit, configuration, platform, and feature set. For separate independently shipped products, scan each subject separately rather than manufacturing one repository-wide result.

## Run the scan

Keep external evidence enabled for the primary result. `--no-external-evidence` is for an explicitly offline comparison; label such a result incomplete.

Use `--refresh` only when stale evidence is suspected, and lower `--concurrency` when a service rate-limits. In CI, persist `--cache-dir` between runs: a cold cache costs hundreds of GitHub requests on a large repository, and `GITHUB_TOKEN` allows 1,000 per hour per repository.

### GitHub authentication

ol does not read `GITHUB_TOKEN`. Set `OL_GITHUB_TOKEN` in the scan process environment only, never on the command line, and never echo or store it.

```bash
OL_GITHUB_TOKEN="$(gh auth token)" ol scan ...
```

```powershell
$env:OL_GITHUB_TOKEN = gh auth token
ol scan ...
Remove-Item Env:OL_GITHUB_TOKEN
```

## Judge coverage first

Read these before any license, cheapest first.

| Signal | Question it answers |
|---|---|
| `metadata.inputDiscovery` (`Input discovery` line: detected files, ignored candidates, incomplete input sets; excluded paths are `metadata.inputScope`) | Did every ecosystem in the subject get scanned, and only those? An ignored candidate is an unscanned ecosystem. |
| `summary.supply` (`Supplied by` line; `--verbose` splits it per ecosystem) | Did the second input earn its place? A large `sbomOnly` count means the generator catalogued things that are not dependencies. |
| `warnings`, including `input_declares_no_components` | Did an input resolve nothing? Every count zero reads exactly like a clean project. |
| `metadata.packageMetadata`, `packageArtifacts`, `declaredGitHubFiles`, `sourceRepository` | Separate collector failure from component status. `fetchErrorCount` above zero means the run is degraded. |
| `metadata.network.githubAuth` | Was the intended authentication mode used? |

In a polyglot scan, read these per ecosystem. One healthy ecosystem hides a missing graph in another.

If a combined scan surprises you, rerun the same scope with each input alone and identify which one changed the evidence. Keep the combined result; do not discard inconvenient evidence.

## Triage violations by population

`ol check` exits `0` pass, `2` policy violations, `3` inconclusive — the run proved nothing, so fix the pipeline or retry rather than treat it as a licensing fact. A CI job that collapses `2` and `3` cannot tell a registry outage from a forbidden license. Two things reach `3`: every finding was a collection failure, or the report carries `input_declares_no_components` and there was nothing to evaluate at all.

Violations name a `Mechanism` and end with an `Unresolved mechanisms` tally. **Read the tally first.** A hundred unresolved rows are usually a few populations, and one decision covers each.

| Mechanism | What it means | Action |
|---|---|---|
| `declared_license_location_not_collected` | The publisher named a URL ol does not fetch. | Open the `Reference` URL. Legacy NuGet `licenseUrl` values often lead to no license at all; then the component is inherently unresolved. |
| `declared_license_file_not_collected` | A license file ships inside the package. | Open that path in the published package. |
| `license_not_recognized` | A repository license file exists that GitHub could not classify. | Open the `Reference` URL. |
| `license_not_detected` | The repository was read and holds no license file. | Ask the publisher, or accept it as unresolved. |
| `external_evidence_not_collected` | Collection was never attempted for this component. | Expected under `--no-external-evidence` or `--skip-evidence-packages`; rerun with collection before concluding anything. |
| `package_metadata_not_found` | The registry said the package is not published there. | Expected for private-feed packages. |
| `package_metadata_no_purl` | The component carries no package identity, so nothing can ever be asked. | A generator problem, not a collection problem. Fix the generator's scope or cataloger selection. |
| `unsupported_package_metadata`, `package_metadata_unversioned_purl` | ol has no provider for that ecosystem, or the purl names no single version. | Fix the input, or accept the limitation. |
| `-` on a `license is not allowed` row | The license resolved; policy rejected it. | Allow-list decision, never a baseline one. |

`--allow-dev-licenses` relaxes only components a resolver proved development-only. An input that determines no usage — an SBOM, Yarn, NuGet, Go, pip, Bundler — abstains rather than cancelling that proof, but a component no input classified stays unknown and is never relaxed.

## Policy and baseline

Do not invent an allow-list; obtain the intended one. Resolved-but-rejected licenses belong in the allow-list. Only `unknown`, `ambiguous`, `conflict`, and `invalid` may be baselined, and `error` never is.

`--baseline` is repeatable and the files compose: a component is acknowledged when any of them states it. Use one shared file for a population several repositories share and a local file for what only this repository accepts.

```text
ol check --report ol-report.json --allow-licenses <ids> --baseline <shared> --baseline <local>
```

`--update-baseline` rewrites the **last** file with what the earlier ones do not already acknowledge, and never touches the earlier ones. Never run it in CI: a failing check is the review gate.

## Detect regressions

`check` answers "does the current state satisfy policy". It cannot say what a change did, so every pre-existing violation blocks every pull request until it is resolved. Commit a report and compare:

```text
ol diff --previous before.json --current after.json
```

This is what surfaces a dependency whose license changed between versions. `--sarif` on `check` writes the same violations for code scanning, with dependency paths.

## Report

State the ol version, the auditable subject, the ecosystems found and any excluded, the exact input-generation commands, coverage signals above, component and status counts per ecosystem, unresolved mechanisms with counts, collector health, limitations, and the next action. For policy work also state the allow-list source, initial violations, baseline paths and acknowledged counts, steady-state exit code, and the exact CI command.

Keep the canonical JSON before producing any filtered view. `--dependency` filters presentation, not analysis.
