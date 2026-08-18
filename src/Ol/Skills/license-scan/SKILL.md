---
name: license-scan
description: Run and evaluate ol license-compliance scans against repositories and resolved dependency artifacts. Use when an agent needs to discover supported SBOM or package-manager inputs, combine a CycloneDX/SPDX SBOM with resolver outputs, collect registry and GitHub license evidence, diagnose unresolved results, produce canonical JSON reports, or apply an SPDX allow-list with ol check.
---

# ol License Scan

Scan dependencies the build actually resolved. Prefer one SBOM plus package-manager inputs for the same build scope; fall back to either input alone only when the other cannot be produced reliably.

## Establish the executable and scope

1. Run `ol scan --help` and use it as the version-specific option reference.
2. Identify the intended audit scope: release projects, one solution, or the whole repository. Ask when this changes the compliance meaning.
3. Find existing SBOMs and resolved inputs, including ignored build directories. Do not rely on `rg --files` alone because it normally omits `obj/`.
4. Never pass unresolved manifests such as `*.csproj`, `package.json`, or `Cargo.toml` to ol. Generate an SBOM or ecosystem-specific resolved input first.

For .NET, locate relevant `obj/project.assets.json` files with `Get-ChildItem -Recurse -Filter project.assets.json` or `find ... -name project.assets.json`. Check timestamps and solution/project membership. Run `dotnet restore` only when assets are missing or stale, and state that it can update build artifacts and contact configured feeds.

## Select inputs

Use this order:

1. **SBOM plus package-manager input:** Repeat `--input` when both describe the same resolved build.
2. **SBOM only:** Use when package-manager output is unavailable, non-portable, or cannot be aligned with the SBOM scope.
3. **Package-manager input only:** Use when no trustworthy SBOM can be generated.

Do not silently fall back. Attempt the companion input or record the exact blocker. Merely finding no pre-existing SBOM is not a blocker; attempt generation.

Align both input sets. Do not combine a solution-level SBOM with a repository root containing unrelated samples, tests, performance projects, target frameworks, or old builds. In a large mono-repository, prefer an explicit solution/subtree or repeat the exact resolver files that produced the SBOM. Compare component counts and `suppliedBy`; a large package-manager-only remainder usually indicates scope or identity mismatch.

```text
ol scan --input bom.cdx.json --input path/to/aligned-scope --format json
```

## Generate a .NET SBOM when needed

Prefer the official CycloneDX .NET tool and write outside the target repository unless the user requests a committed SBOM:

```text
dotnet tool install CycloneDX --tool-path <temporary-tool-directory>
dotnet-CycloneDX <solution-or-project> --output <temporary-output-directory> --filename bom.cdx.json --output-format Json --disable-package-restore
```

Use `--disable-package-restore` only when current assets exist. It disables restore, not all NuGet access: CycloneDX may still query feeds for nuspec metadata. Before scanning a private repository or contacting a feed, explain that dependency coordinates may be sent to that service and obtain required approval.

When target files must remain untouched and assets are stale, restore into isolated artifacts/packages directories. Point CycloneDX at them with its version-appropriate custom intermediate-output option. Read its log and verify the assets paths it actually opened; an accepted option may not redirect a particular layout. If it read stale assets, regenerate safely or compare old/fresh package name-version identities and report the caveat.

In sandboxed Windows environments, read `packageFolders` from `project.assets.json` and set `NUGET_PACKAGES` to an existing cache for the command. Do not assume a fixed user path or print environment values.

## Run the evidence scan

Keep external evidence enabled for the primary result so ol combines input claims with package artifacts, registries, declared GitHub files, and source repositories.

```text
ol scan --input <sbom> --input <aligned-resolved-input> --format json
```

Use an isolated `--cache-dir` for reproducibility experiments. For ordinary use, retain the normal cache. Use `--refresh` only when stale evidence is suspected and lower `--concurrency` when a service rate-limits requests.

Do not make `--no-external-evidence` the default. It also prevents package-artifact collection and can turn NuGet resolver input into entirely unknown results. Use it only for an explicitly offline/input-only comparison and label that report incomplete.

### Apply GitHub authentication safely

Check `gh auth status`. If it succeeds, obtain the token without displaying it and set `OL_GITHUB_TOKEN` only in the scan process environment:

```powershell
$env:OL_GITHUB_TOKEN = gh auth token
ol scan ...
Remove-Item Env:OL_GITHUB_TOKEN
```

```bash
OL_GITHUB_TOKEN="$(gh auth token)" ol scan ...
```

ol does not implicitly read `GITHUB_TOKEN`. Never echo, log, store, or place the token on the command line. If authentication is invalid, ask the user to re-authenticate or clearly report the lower unauthenticated rate limit.

## Judge the result

Treat scan exit code zero as successful execution, not a clean compliance result. Read:

- `summary` and every component `status`.
- `metadata.input` to confirm parser, input count, and source.
- `metadata.packageArtifacts` for local document matches.
- `metadata.packageMetadata`, `metadata.declaredGitHubFiles`, and `metadata.sourceRepository` for cache, request, fetch-error, and unknown counts.
- `metadata.network.githubAuth` to confirm authentication.
- component `suppliedBy`, `dependency`, `licenseCandidates`, and `warnings` to diagnose merge coverage.

If a combined scan has unexpectedly many unresolved statuses, rerun SBOM-only and package-manager-only against the identical scope and cache. Look for placeholders such as `Unknown`, `NOASSERTION`, or `Unknown - See URL` competing with stronger evidence. Preserve the combined report and identify the input introducing ambiguity rather than silently discarding it.

Evaluate collector fetch-error counters separately from component statuses. A weak SBOM claim can change a component from `error` to `ambiguous` without restoring missing evidence.

If network access fails, do not present the degraded run as definitive. Retry after explicit network/auth approval or report exactly which collectors failed.

Use `ol check` only after producing canonical JSON and obtaining the intended SPDX policy:

```text
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-2-Clause,BSD-3-Clause
```

Do not invent an allow-list. `unknown`, `ambiguous`, and `error` require evidence review or an explicit baseline/policy decision.

## Report the assessment

State the executable/version, scope, inputs, scan mode, component/status/dependency counts, collector health, limitations, and next action. Preserve a full canonical JSON report before producing filtered or grouped human views. Remember that `--dependency` filters the view, not the underlying analysis.
