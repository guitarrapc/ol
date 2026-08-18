---
name: license-scan
description: Scan dependency licenses with ol by combining a CycloneDX/SPDX SBOM with resolved package-manager inputs, then enforce the intended SPDX policy with check, reviewed baselines, and CI. Supports single-language, polyglot, and monorepo builds, including input alignment, evidence collection, unresolved-result diagnosis, baseline updates, and license-regression detection.
---

# ol License Scan

Scan the dependencies the audited build actually resolved. Treat repositories as potentially polyglot: discover every in-scope ecosystem before selecting inputs.

## Run the compliance lifecycle

Use ol as a review loop, not as a one-time inventory:

1. **Scan:** generate a canonical JSON report from the aligned SBOM and resolved package-manager inputs.
2. **Review facts:** confirm coverage, then inspect detected licenses and unresolved evidence. Scan results describe evidence; they do not decide organizational intent.
3. **Define policy:** obtain the intended SPDX allow-list and any separately approved development-only licenses.
4. **Check:** run `ol check` without a baseline first so every policy violation is visible.
5. **Decide:** for each violation, fix bad evidence or dependencies; add a resolved license to the allow-list only after approval; consider a baseline only for reviewed evidence that remains unresolved.
6. **Adopt a baseline:** generate `baseline.json` once from the reviewed unresolved set, inspect its contents, and commit it.
7. **Verify:** rerun `check` without `--update-baseline`; it must pass with the committed policy and baseline.
8. **Enforce in CI:** regenerate the report, then run the same `check`. New or changed evidence must fail until reviewed.
9. **Update deliberately:** when dependencies or evidence change, review the violation and baseline diff before replacing the complete baseline snapshot.

```text
ol scan --input <sbom> --input <aligned-resolved-inputs> --format json > ol-report.json
ol check --report ol-report.json --allow-licenses <approved-SPDX-ids>
ol check --report ol-report.json --allow-licenses <approved-SPDX-ids> --baseline baseline.json --update-baseline
ol check --report ol-report.json --allow-licenses <approved-SPDX-ids> --baseline baseline.json
```

Read [references/policy-workflow.md](references/policy-workflow.md) before creating or updating a baseline. Never use a baseline to absorb a resolved but unapproved license or a collection error.

## Establish the executable and scope

1. Run `ol scan --help`; use it as the version-specific option reference.
2. Define the auditable subject: a release artifact, application, workspace, subtree, or whole repository. Ask when the choice changes compliance meaning.
3. Inventory manifests, workspaces, existing SBOMs, lockfiles, and generated resolver outputs across the entire subject, including ignored build directories.
4. Map each in-scope component to its package manager and build context. Do not stop after finding the first ecosystem.
5. Read [references/ecosystem-inputs.md](references/ecosystem-inputs.md) for supported inputs and preparation commands relevant to the ecosystems found.

Do not pass unresolved manifests such as `package.json`, `Cargo.toml`, project files, `go.mod`, or requirements files by themselves. They state requests, not necessarily the versions and transitive graph selected by the build. Generate a supported SBOM or resolved input first.

## Select and align inputs

Prefer inputs in this order:

1. **One SBOM plus aligned package-manager inputs:** use one CycloneDX/SPDX JSON SBOM for the auditable subject and the resolved outputs for every in-scope ecosystem.
2. **One SBOM:** use it alone only when aligned resolver outputs cannot be produced reliably.
3. **Resolved package-manager inputs:** use all in-scope ecosystem outputs together when no trustworthy subject-wide SBOM can be generated.

Do not silently fall back. Attempt the companion input or record its exact blocker. Validate that generated inputs are current for the same commit, configuration, platform, feature set, and production/development scope. A file's presence does not prove freshness.

Pass a directory when it contains only aligned supported inputs. Otherwise repeat explicit paths. ol accepts at most one SBOM per input collection.

```text
ol scan --input bom.cdx.json \
  --input path/to/ecosystem-a/resolved-input \
  --input path/to/ecosystem-b/resolved-input \
  --format json
```

For a monorepo, do not mix a repository-wide SBOM with resolver files from unrelated samples, tests, tools, old builds, or excluded release units. For separate independently shipped products, scan and report each auditable subject separately instead of manufacturing one repository-wide result.

When resolution or SBOM generation can mutate the target repository, write outputs and caches outside it where the ecosystem permits. Before contacting private feeds or external services, explain that dependency coordinates may leave the environment and obtain any required approval.

## Run the evidence scan

Keep external evidence enabled for the primary result so ol can combine input claims with available package artifacts, registries, declared GitHub files, and source repositories.

```text
ol scan --input <sbom-or-resolved-input> --format json
```

Preserve the canonical JSON report. Use an isolated `--cache-dir` for reproducibility experiments; retain the normal cache for ordinary use. Use `--refresh` only when stale evidence is suspected, and reduce `--concurrency` when a service rate-limits requests.

Do not make `--no-external-evidence` the default. Use it only for an explicitly offline/input-only comparison and label the result incomplete. Its impact varies by ecosystem and available local artifacts.

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

ol does not implicitly read `GITHUB_TOKEN`. Never echo, log, store, or place the token on the command line. If authentication is invalid, request re-authentication or report the unauthenticated limitation.

## Judge coverage before license status

Treat exit code zero as successful execution, not a clean compliance result. Read:

- `metadata.input`: confirm every expected parser, file, and build context is represented.
- inventory and dependency counts: compare them with each ecosystem's resolver and with the SBOM.
- `suppliedBy`: confirm expected identities merged across the SBOM and resolver inputs.
- `metadata.packageArtifacts`, `metadata.packageMetadata`, `metadata.declaredGitHubFiles`, and `metadata.sourceRepository`: separate collector coverage and fetch failures from component status.
- `metadata.network.githubAuth`: confirm the intended authentication mode.
- every component's `status`, `dependency`, `licenseCandidates`, and `warnings`.

In a polyglot scan, group coverage diagnostics by ecosystem and input context. One healthy ecosystem must not hide a missing, stale, or unsupported graph in another. A large resolver-only remainder, duplicate-looking identities, or unexpected `dependency: unknown` usually indicates scope, identity, or graph mismatch.

If a combined scan has unexpected unresolved or ambiguous results, rerun the same scope as SBOM-only and resolved-input-only, then isolate individual ecosystem inputs when needed. Preserve the combined result and identify which input changes the evidence; do not discard inconvenient evidence silently.

Evaluate fetch-error counters separately from statuses. If network collection fails, do not present the degraded run as definitive.

## Apply policy only after assessment

Run `ol check` only after producing canonical JSON and obtaining the intended SPDX policy:

```text
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-2-Clause,BSD-3-Clause
```

Do not invent an allow-list. Review `unknown`, `ambiguous`, `conflict`, and `invalid` before any baseline decision; repair `error` and scan again. Apply development-only allowances only where resolver evidence proves the dependency classification.

## Report the assessment

State the ol executable/version, auditable subject, languages/ecosystems found, included and excluded build contexts, input-generation commands, scan mode, component/status/dependency counts by ecosystem where useful, collector health, limitations, and next action. For policy onboarding or maintenance, also state the allow-list source, initial violations, baseline path and acknowledged count, steady-state exit code, and exact CI command. Preserve the full canonical JSON report before producing filtered views. Remember that `--dependency` filters presentation, not analysis.
